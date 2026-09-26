using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using DiagnosticDamageProbe.Transport;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Attribution;
using DiagnosticDamageProbe.Snapshot;
using DiagnosticDamageProbe.Diagnostics;

namespace DiagnosticDamageProbe;

internal sealed class DamageCommitTransport
{
    internal readonly struct DeathObservation
    {
        internal readonly string Result;
        internal readonly EncounterState StateBefore;
        internal readonly EncounterState StateAfter;
        internal DeathObservation(string result, EncounterState before, EncounterState after)
        { Result = result; StateBefore = before; StateAfter = after; }
    }

    internal const string CommitRpc = "CombatMeter.DamageCommit.v1";
    internal const string AckRpc = "CombatMeter.DamageCommitAck.v1";
    internal const string AttributionRpc = "CombatMeter.MagicAttribution.v1";
    internal const string AttributionAckRpc = "CombatMeter.MagicAttributionAck.v1";
    internal const string SnapshotRpc = "CombatMeter.CombatSnapshot.v1";
    internal const double SnapshotIntervalSeconds = 0.5d;
    // ZRoutedRpc has Register(Dictionary.Add), but no public Unregister in this build.
    // Register once per instance. Inactive callbacks are gated; weak keys don't retain old worlds.
    private readonly ConditionalWeakTable<ZRoutedRpc, object> _registrations = new ConditionalWeakTable<ZRoutedRpc, object>();
    private ZNet _net;
    private ZRoutedRpc _rpc;
    private WeakReference<ZNet> _blockedNet;
    private CommitSession _session;
    // Transitional legacy diagnostics/DeathObservation only; never a snapshot source.
    private EncounterManager _encounter;
    private CombatClusterManager _clusters;
    private CombatMeterPerformance _performance;
    private readonly Func<long> _performanceTimestamp;
    private MagicAttributionResolver _attribution;
    private EventSequence _attributionSequence;
    private double _clockAnchorMonotonic;
    private long _clockAnchorUtcTicks;
    private readonly CombatSnapshotStore _snapshotStore = new CombatSnapshotStore();
    private Guid _snapshotEpoch;
    private long _snapshotSequence;
    private double _nextSnapshotAt;
    private string _lastLocalSnapshotDiagnosticKey;
    private string _lastPublishedSnapshotDiagnosticKey;
    private string _lastAcceptedSnapshotDiagnosticKey;
    private double _nextSnapshotReceivedDiagnosticAt;
    private readonly AttributionAcceptor _attributionAcceptor = new AttributionAcceptor();
    private readonly AttributionOutbox _attributionOutbox = new AttributionOutbox();
    private readonly System.Collections.Generic.List<(DamageCommit Commit, double AcceptedAt, double Expires)> _pendingSummon = new System.Collections.Generic.List<(DamageCommit, double, double)>();
    private readonly System.Collections.Generic.List<(DamageCommit Commit, double AcceptedAt, double Expires)> _pendingDot = new System.Collections.Generic.List<(DamageCommit, double, double)>();
    private bool _disposed;
    private readonly Func<double> _now;
    private readonly Func<double> _softTimeout;
    private readonly Func<double> _recoveryTimeout;
    private readonly Func<double> _dpsIdleTimeout;
    private EncounterSettings _encounterSettings;

    internal DamageCommitTransport(Func<double> now = null, Func<double> softTimeout = null,
        Func<double> recoveryTimeout = null, Func<double> dpsIdleTimeout = null, Func<long> performanceTimestamp = null)
    {
        _now = now ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _softTimeout = softTimeout;
        _recoveryTimeout = recoveryTimeout;
        _dpsIdleTimeout = dpsIdleTimeout;
        _performanceTimestamp = performanceTimestamp;
    }

    internal void Bind(ZNet net)
    {
        if (_disposed || !net || net.IsDedicated() || IsBlocked(net)) return;
        ZRoutedRpc rpc = ZRoutedRpc.instance;
        if (rpc == null || ZDOMan.instance == null) return;
        if (!ReferenceEquals(net, _net) || !ReferenceEquals(rpc, _rpc))
        {
            Close("NetworkInstanceChanged");
            _net = net; _rpc = rpc;
            _blockedNet = null;
        }
        if (!_registrations.TryGetValue(rpc, out _))
        {
            _registrations.Add(rpc, new object());
            try
            {
                rpc.Register<ZPackage>(CommitRpc, (sender, pkg) => Receive(rpc, sender, pkg));
                rpc.Register<ZPackage>(AckRpc, (sender, pkg) => ReceiveAck(rpc, sender, pkg));
                rpc.Register<ZPackage>(AttributionRpc, (sender, pkg) => ReceiveAttribution(rpc, sender, pkg));
                rpc.Register<ZPackage>(AttributionAckRpc, (sender, pkg) => ReceiveAttributionAck(rpc, sender, pkg));
                rpc.Register<ZPackage>(SnapshotRpc, (sender, pkg) => ReceiveSnapshot(rpc, sender, pkg));
            }
            catch { _blockedNet = new WeakReference<ZNet>(net); Close("RpcRegistrationFailed"); throw; }
        }
        EnsureSession();
    }

    private bool EnsureSession()
    {
        if (_disposed || !_net || IsBlocked(_net) ||
            !ReferenceEquals(_net, ZNet.instance) || !ReferenceEquals(_rpc, ZRoutedRpc.instance) || ZDOMan.instance == null)
            return false;
        long peer = ZDOMan.GetSessionID();
        if (peer == 0) return false;
        bool host = _net.IsServer();
        // No ready host => no target. In particular never use target 0 (Everybody).
        if (!host && _net.GetServerPeer() == null) return false;
        if (_session != null && (_session.Peer != peer || _session.IsHost != host)) Close("PeerChanged");
        if (_session == null)
        {
            _encounterSettings = host ? new EncounterSettings(ReadSoftTimeout(), ReadRecoveryTimeout(), ReadDpsIdleTimeout()) : null;
            _encounter = host ? new EncounterManager(new CombatStatisticsAggregator(), _encounterSettings, Plugin.EncounterLog) : null;
            _clusters = host ? new CombatClusterManager(_encounterSettings) : null;
            if (host) LogEncounterSettings("CombatEncounterSettings");
            _attribution = host ? new MagicAttributionResolver() : null;
            _attributionSequence = new EventSequence(peer, Guid.NewGuid());
            _clockAnchorMonotonic = _now();
            _clockAnchorUtcTicks = DateTime.UtcNow.Ticks;
            _snapshotStore.Clear();
            _snapshotEpoch = Guid.NewGuid();
            _snapshotSequence = 0;
            _nextSnapshotAt = _clockAnchorMonotonic;
            _lastLocalSnapshotDiagnosticKey = null;
            _lastPublishedSnapshotDiagnosticKey = null;
            _lastAcceptedSnapshotDiagnosticKey = null;
            _nextSnapshotReceivedDiagnosticAt = 0d;
            _session = new CommitSession(peer, Guid.NewGuid(), host, Send, Log,
                host ? AcceptForAttribution : (Action<DamageCommit>)null);
            if (!host)
                Plugin.EncounterLog("CombatSettingsAuthority role=Client source=ListenHostSnapshot localCombatConfigIgnored=true");
            Plugin.TransportLog("DamageCommitSessionReady " + new LogFields()
                .Text("PeerSessionId", peer.ToString(CultureInfo.InvariantCulture))
                .Text("SourceEpoch", _session.Sequence.Epoch.ToString("N"))
                .Flag("IsHost", host));
        }
        RefreshPerformanceSettings();
        return true;
    }

    internal bool ObserveSummonProvenance(long observedPeer, long summonCreator, uint summonObject, long playerId)
    {
        if (!EnsureSession()) { Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=TransportUnavailable"); return false; }
        if (observedPeer != _session.Peer) { Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=NotExpectedPeer"); return false; }
        if (summonCreator == 0 || summonObject == 0) { Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=SummonZdoUnavailable"); return false; }
        if (playerId == 0) { Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=PlayerIdZero"); return false; }
        PublishAttribution(new AttributionMessage(_attributionSequence.Next(), AttributionMessageKind.Summon,
            summonCreator, summonObject, playerId));
        return true;
    }

    internal void ObserveDotPool(long observedPeer, long victimCreator, uint victimObject, DotKind kind,
        float before, float after, AttackerClass sourceClass, long? sourcePlayerId, long sourceCreator, uint sourceObject,
        bool updateAccepted)
    {
        if (!EnsureSession() || observedPeer != _session.Peer || victimCreator == 0 || victimObject == 0 || !updateAccepted) return;
        PublishAttribution(new AttributionMessage(_attributionSequence.Next(), AttributionMessageKind.DotPool,
            victimCreator, victimObject, null, kind, before, after, sourceClass, sourcePlayerId, sourceCreator, sourceObject, updateAccepted));
    }

    private void PublishAttribution(AttributionMessage message)
    {
        if (_session.IsHost) ProcessAttribution(message, _session.Peer);
        else if (!_attributionOutbox.Enqueue(message)) Plugin.Warn("SummonProvenanceRejected reason=PendingCapacity");
        else if (message.Kind == AttributionMessageKind.Summon)
            Plugin.MagicLog("SummonProvenanceMessageQueued summon=" + message.SubjectZdoId + " event=" + message.Id);
    }

    internal DeathObservation ObservePlayerDeath(long playerId, string incarnation = null)
    {
        if (playerId == 0L) return new DeathObservation("MissingPlayerID", EncounterState.NoEncounter, EncounterState.NoEncounter);
        if (_disposed || !_net || !_net.IsServer() || _net.IsDedicated())
            return new DeathObservation("NotHost", EncounterState.NoEncounter, EncounterState.NoEncounter);
        if (!EnsureSession() || _session == null || !_session.IsHost || _encounter == null)
            return new DeathObservation("NoActiveSession", EncounterState.NoEncounter, EncounterState.NoEncounter);

        EncounterState before = _encounter.State;
        double now = _now();
        PlayerDeathResult result = _encounter.OnPlayerDied(playerId, now, incarnation);
        _clusters.OnPlayerDied(playerId, now, incarnation);
        return new DeathObservation(result.ToString(), before, _encounter.State);
    }

    internal EncounterManager Encounter => _encounter;
    internal CombatClusterManager Clusters => _clusters;
    internal CombatMeterPerformance Performance => _performance;
    internal MagicAttributionResolver Attribution => _attribution;
    internal int AttributionPendingCount => _attributionOutbox.Count;
    internal CombatSnapshotStore SnapshotStore => _snapshotStore;
    internal bool HasActiveSession => _session != null;

    internal void Observe(long observedPeer, DamageFacts facts, float loss)
    {
        if (!EnsureSession() || observedPeer != _session.Peer)
        { Plugin.Warn("DamageCommitObservationRejected reason=NoActiveSession"); return; }
        CombatMeterPerformance performance = GetPerformance();
        if (performance != null) performance.DamageObserved++;
        _session.Observe(facts, loss, DateTime.UtcNow.Ticks);
        Pump(false);
    }

    internal void Update()
    {
        if (_disposed) return;
        if (!_net || !ReferenceEquals(_net, ZNet.instance) || !ReferenceEquals(_rpc, ZRoutedRpc.instance))
        {
            Close("NetworkUnavailable");
            if (ZNet.instance) Bind(ZNet.instance);
            return;
        }
        if (_session != null && !_session.IsHost && _net.GetServerPeer() == null) Close("Disconnected");
        if (EnsureSession()) Pump(true);
    }

    private void Pump(bool publishSnapshot)
    {
        double now = _now();
        SyncEncounterSettings();
        _encounter?.Update(now);
        _clusters?.Update(now);
        _session?.Pump(now);
        if (_session != null && !_session.IsHost)
        {
            AttributionMessage message = _attributionOutbox.Due(now, out AttributionMessage expired);
            if (expired?.Kind == AttributionMessageKind.Summon)
                Plugin.MagicLog("SummonProvenanceRetryExpired summon=" + expired.SubjectZdoId + " event=" + expired.Id);
            if (message != null) SendAttribution(message);
        }
        if (_session != null && _session.IsHost)
        {
            for (int i = _pendingSummon.Count - 1; i >= 0; i--)
                if (now >= _pendingSummon[i].Expires) { var p = _pendingSummon[i]; _pendingSummon.RemoveAt(i); ProcessAttributed(p.Commit, p.AcceptedAt); }
            for (int i = _pendingDot.Count - 1; i >= 0; i--)
                if (now >= _pendingDot[i].Expires) { var p = _pendingDot[i]; _pendingDot.RemoveAt(i); ProcessAttributed(p.Commit, p.AcceptedAt); }
            if (publishSnapshot && now >= _nextSnapshotAt) PublishSnapshot(now);
            if (publishSnapshot) ReportPerformance();
        }
    }

    private double ReadSoftTimeout() => _softTimeout?.Invoke() ?? EncounterSettings.DefaultSoftTimeoutSeconds;
    private double ReadRecoveryTimeout() => _recoveryTimeout?.Invoke() ?? EncounterSettings.DefaultRecoveryTimeoutSeconds;
    private double ReadDpsIdleTimeout() => _dpsIdleTimeout?.Invoke() ?? EncounterSettings.DefaultDpsIdleTimeoutSeconds;

    private void SyncEncounterSettings()
    {
        if (_encounterSettings == null || _session == null || !_session.IsHost) return;
        if (_encounterSettings.Update(ReadSoftTimeout(), ReadRecoveryTimeout(), ReadDpsIdleTimeout()))
            LogEncounterSettings("CombatEncounterSettingsChanged");
    }

    private void LogEncounterSettings(string marker) => Plugin.EncounterLog(marker +
        " combatTimeout=" + _encounterSettings.SoftTimeoutSeconds.ToString(CultureInfo.InvariantCulture) +
        " recoveryTimeout=" + _encounterSettings.RecoveryTimeoutSeconds.ToString(CultureInfo.InvariantCulture) +
        " dpsIdleTimeout=" + _encounterSettings.DpsIdleTimeoutSeconds.ToString(CultureInfo.InvariantCulture) +
        " authority=" + (ZNet.IsSinglePlayer ? "SinglePlayer" : "ListenHost"));

    private void PublishSnapshot(double now)
    {
        CombatMeterPerformance performance = GetPerformance();
        long started = performance != null ? PerformanceTimestamp() : 0;
        try
        {
            _nextSnapshotAt = now + SnapshotIntervalSeconds;
            if (IsCurrentPerformance(performance)) performance.ReadyPeers = 0;
            long sequence = checked(++_snapshotSequence); // Once per cycle, shared by all recipients.
            CombatSnapshot snapshot = BuildRecipientSnapshot(sequence, ResolveSnapshotPlayer(_session.Peer, true), now);
            SnapshotApplyResult local = _snapshotStore.Apply(snapshot, _session.Peer, _session.Peer);
            if (local != SnapshotApplyResult.Accepted) throw new InvalidOperationException("LocalSnapshot" + local);
            LogSnapshotChange("CombatSnapshotAppliedLocal", snapshot);
            if (ZNet.IsSinglePlayer) return;
            var sent = new System.Collections.Generic.HashSet<long>();
            foreach (ZNetPeer peer in _net.GetPeers())
            {
                if (peer == null || !peer.IsReady() || peer.m_uid == 0 || peer.m_uid == _session.Peer || !sent.Add(peer.m_uid)) continue;
                if (IsCurrentPerformance(performance)) performance.ReadyPeers++;
                CombatSnapshot remote = BuildRecipientSnapshot(sequence, ResolveSnapshotPlayer(peer.m_uid, false), now);
                long serializationStart = IsCurrentPerformance(performance) ? PerformanceTimestamp() : 0;
                var package = new ZPackage(CombatSnapshotCodec.Encode(remote));
                if (IsCurrentPerformance(performance)) performance.Serialization.Record(PerformanceTimestamp() - serializationStart);
                _rpc.InvokeRoutedRPC(peer.m_uid, SnapshotRpc, package);
                if (IsCurrentPerformance(performance)) performance.RecordSend(package.Size());
                LogSnapshotChange("CombatSnapshotPublished", remote);
            }

        }
        finally { if (IsCurrentPerformance(performance)) performance.SnapshotCycle.Record(PerformanceTimestamp() - started); }
    }

    private long? ResolveSnapshotPlayer(long peer, bool local)
    {
        CombatMeterPerformance performance = GetPerformance();
        long started = performance != null ? PerformanceTimestamp() : 0;
        long? player = local ? HostPlayerIdentity.ResolveLocal(peer) : HostPlayerIdentity.ResolveRemote(peer);
        if (IsCurrentPerformance(performance)) performance.RecordIdentity(PerformanceTimestamp() - started, player.HasValue);
        return player;
    }

    private CombatSnapshot BuildRecipientSnapshot(long sequence, long? playerId, double now)
    {
        CombatMeterPerformance performance = GetPerformance();
        long started = performance != null ? PerformanceTimestamp() : 0;
        CombatSnapshot snapshot = CombatSnapshotBuilder.ForPlayer(_session.Peer, _snapshotEpoch, sequence, _clusters, playerId, now);
        if (IsCurrentPerformance(performance)) performance.RecordBuild(PerformanceTimestamp() - started, snapshot.EncounterState == EncounterState.NoEncounter);
        return snapshot;
    }

    // SettingChanged also calls this so even an off/on toggle between updates discards the old window.
    // No clock read or telemetry allocation while disabled or on clients.
    internal void RefreshPerformanceSettings()
    {
        if (!Plugin.PerformanceLoggingEnabled || _session?.IsHost != true)
            _performance = null;
        else if (_performance == null)
            _performance = new CombatMeterPerformance(PerformanceTimestamp());
    }

    private CombatMeterPerformance GetPerformance()
    {
        RefreshPerformanceSettings();
        return _performance;
    }

    // A synchronous callback may toggle diagnostics during a measured call.
    // Never finish a sample into a discarded or newly enabled window.
    private bool IsCurrentPerformance(CombatMeterPerformance performance) =>
        performance != null && Plugin.PerformanceLoggingEnabled && ReferenceEquals(performance, _performance);

    private long PerformanceTimestamp() => _performanceTimestamp?.Invoke() ?? Stopwatch.GetTimestamp();

    private void ReportPerformance(bool force = false)
    {
        CombatMeterPerformance performance = GetPerformance();
        if (performance == null) return;
        string report = performance.TryReport(PerformanceTimestamp(), true, _clusters.ActiveCount, force);
        if (report != null) Plugin.PerformanceLog(report);
    }
    private void ReceiveSnapshot(ZRoutedRpc rpc, long sender, ZPackage package)
    {
        if (_disposed || !ReferenceEquals(rpc, _rpc) || !EnsureSession() || _session.IsHost) return;
        ZNetPeer host = _net.GetServerPeer();
        if (host == null || sender != host.m_uid)
        { Plugin.SnapshotLog("CombatSnapshotRejected reason=UnexpectedSender sender=" + sender); return; }
        try
        {
            if (package == null || package.Size() > CombatSnapshotCodec.MaxBytes) throw new InvalidDataException("PacketSize");
            CombatSnapshot snapshot = CombatSnapshotCodec.Decode(package.GetArray());
            double now = _now();
            if (now >= _nextSnapshotReceivedDiagnosticAt)
            {
                _nextSnapshotReceivedDiagnosticAt = now + 5d;
                Plugin.SnapshotLog("CombatSnapshotReceived epoch=" + snapshot.SnapshotEpoch.ToString("N") + " sequence=" + snapshot.Sequence);
            }
            SnapshotApplyResult result = _snapshotStore.Apply(snapshot, sender, host.m_uid);
            if (result == SnapshotApplyResult.Accepted) LogSnapshotChange("CombatSnapshotAccepted", snapshot);
            else if (result == SnapshotApplyResult.Duplicate || result == SnapshotApplyResult.Stale)
                Plugin.SnapshotLog("CombatSnapshotIgnored reason=" + result + " sequence=" + snapshot.Sequence);
            else Plugin.SnapshotLog("CombatSnapshotRejected reason=" + result + " sequence=" + snapshot.Sequence);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is ArgumentException)
        { Plugin.SnapshotLog("CombatSnapshotRejected reason=MalformedPayload"); }
    }

    private void LogSnapshotChange(string marker, CombatSnapshot snapshot)
    {
        string key = snapshot.EncounterId + "/" + snapshot.EncounterState + "/" + snapshot.Players.Count;
        foreach (CombatSnapshotPlayer row in snapshot.Players)
            key += "/" + row.PlayerId + "/" + row.DamageDone + "/" + row.DamageTaken;
        string previous;
        if (marker == "CombatSnapshotAppliedLocal") previous = _lastLocalSnapshotDiagnosticKey;
        else if (marker == "CombatSnapshotPublished") previous = _lastPublishedSnapshotDiagnosticKey;
        else previous = _lastAcceptedSnapshotDiagnosticKey;
        if (key == previous) return;
        if (marker == "CombatSnapshotAppliedLocal") _lastLocalSnapshotDiagnosticKey = key;
        else if (marker == "CombatSnapshotPublished") _lastPublishedSnapshotDiagnosticKey = key;
        else _lastAcceptedSnapshotDiagnosticKey = key;
        Plugin.SnapshotLog(marker + " epoch=" + snapshot.SnapshotEpoch.ToString("N") + " sequence=" + snapshot.Sequence +
            " encounterId=" + snapshot.EncounterId + " state=" + snapshot.EncounterState +
            " elapsed=" + snapshot.EncounterElapsedSeconds.ToString(CultureInfo.InvariantCulture) + " players=" + snapshot.Players.Count);
    }

    private void Send(DamageCommit commit)
    {
        ZNetPeer server = _net.GetServerPeer();
        if (server == null || !server.IsReady() || server.m_uid == 0) throw new InvalidOperationException("HostNotReady");
        _rpc.InvokeRoutedRPC(server.m_uid, CommitRpc, new ZPackage(CommitCodec.Encode(commit)));
    }

    private void SendAttribution(AttributionMessage message)
    {
        ZNetPeer server = _net.GetServerPeer();
        if (server == null || !server.IsReady() || server.m_uid == 0) return;
        _rpc.InvokeRoutedRPC(server.m_uid, AttributionRpc, new ZPackage(AttributionCodec.Encode(message)));
        if (message.Kind == AttributionMessageKind.Summon)
            Plugin.MagicLog("SummonProvenanceMessageSent summon=" + message.SubjectZdoId + " event=" + message.Id + " host=" + server.m_uid);
    }

    private void ReceiveAttribution(ZRoutedRpc rpc, long sender, ZPackage package)
    {
        if (_disposed || !ReferenceEquals(rpc, _rpc) || !EnsureSession() || !_session.IsHost) return;
        try
        {
            AttributionMessage message = AttributionCodec.Decode(Bytes(package));
            if (message.Kind == AttributionMessageKind.Summon)
                Plugin.MagicLog("SummonProvenanceReceived summon=" + message.SubjectZdoId + " event=" + message.Id + " sender=" + sender);
            string result = ProcessAttribution(message, sender);
            if ((result == "Accepted" || result == "Duplicate") && sender != 0 && sender != _session.Peer)
            {
                rpc.InvokeRoutedRPC(sender, AttributionAckRpc, new ZPackage(AttributionCodec.EncodeAck(message.Id)));
                if (message.Kind == AttributionMessageKind.Summon)
                    Plugin.MagicLog("SummonProvenanceAckSent summon=" + message.SubjectZdoId + " event=" + message.Id + " result=" + result);
            }
        }
        catch (InvalidDataException) { Plugin.MagicLog("SummonProvenanceRejected reason=MalformedPayload"); }
        catch (Exception ex) { Plugin.MagicLog("SummonProvenanceRejected reason=" + ex.GetType().Name); }
    }

    private void ReceiveAttributionAck(ZRoutedRpc rpc, long sender, ZPackage package)
    {
        if (_disposed || !ReferenceEquals(rpc, _rpc) || !EnsureSession() || _session.IsHost) return;
        ZNetPeer host = _net.GetServerPeer();
        if (host == null || sender != host.m_uid)
        { Plugin.MagicLog("SummonProvenanceAckUnknown reason=UnexpectedSender sender=" + sender); return; }
        try
        {
            EventId id = AttributionCodec.DecodeAck(Bytes(package));
            Plugin.MagicLog("SummonProvenanceAckReceived event=" + id + " sender=" + sender);
            if (_attributionOutbox.Complete(id, out AttributionMessage completed))
            {
                if (completed.Kind == AttributionMessageKind.Summon)
                    Plugin.MagicLog("SummonProvenanceMessageCompleted summon=" + completed.SubjectZdoId + " event=" + id);
            }
            else Plugin.MagicLog("SummonProvenanceAckUnknown reason=EventIdMismatch event=" + id);
        }
        catch (InvalidDataException) { Plugin.MagicLog("SummonProvenanceAckUnknown reason=MalformedPayload"); }
    }

    private string ProcessAttribution(AttributionMessage message, long sender)
    {
        string result = _attributionAcceptor.Accept(message, sender);
        if (result == "Duplicate")
        {
            if (message.Kind == AttributionMessageKind.Summon)
                Plugin.MagicLog("SummonProvenanceDuplicate summon=" + message.SubjectZdoId + " event=" + message.Id);
            return result;
        }
        if (result != "Accepted")
        {
            string reason = result == "InvalidSenderOrEvent" ?
                (message != null && message.Id.IsValid && message.Id.SourcePeerId != sender ? "UnexpectedSender" : "InvalidEventId") :
                result == "InvalidPayload" ? "MalformedPayload" : result;
            Plugin.MagicLog("SummonProvenanceRejected reason=" + reason);
            return result;
        }
        if (message.Kind == AttributionMessageKind.Summon)
        {
            if (!_attribution.Summons.Register(message.SubjectZdoId, message.PlayerId.Value))
            { Plugin.MagicLog("SummonProvenanceRejected reason=ConflictingMapping"); return "ConflictingMapping"; }
            Plugin.MagicLog("SummonProvenanceAccepted summon=" + message.SubjectZdoId + " player=" + message.PlayerId.Value);
            for (int i = _pendingSummon.Count - 1; i >= 0; i--)
                if (_pendingSummon[i].Commit.Facts.AttackerZdoId == message.SubjectZdoId)
                { var p = _pendingSummon[i]; _pendingSummon.RemoveAt(i); ProcessAttributed(p.Commit, p.AcceptedAt); }
        }
        else
        {
            _attribution.ApplyPoolUpdate(message.SubjectZdoId, message.DotKind, message.PoolBefore, message.PoolAfter,
                message.SourceClass, message.SourcePlayerId, message.SourceZdoId, message.PoolUpdateAccepted);
            Plugin.MagicLog((message.DotKind == DotKind.Poison ? "PoisonOwnerChanged" : "DotContributionAdded") +
                " victim=" + message.SubjectZdoId + " kind=" + message.DotKind + " delta=" + (message.PoolAfter - message.PoolBefore));
            for (int i = _pendingDot.Count - 1; i >= 0; i--)
                if (_pendingDot[i].Commit.Facts.VictimZdoId == message.SubjectZdoId &&
                    _pendingDot[i].Commit.Facts.DotKind == (byte)((int)message.DotKind + 1))
                { var p = _pendingDot[i]; _pendingDot.RemoveAt(i); ProcessAttributed(p.Commit, p.AcceptedAt); }
        }
        return "Accepted";
    }

    private void AcceptForAttribution(DamageCommit commit)
    {
        CombatMeterPerformance performance = GetPerformance();
        if (performance != null) performance.DamageAccepted++;
        double now = _now(); DamageFacts f = commit.Facts;
        if (f.Attacker == AttackerClass.NPC && !string.IsNullOrEmpty(f.AttackerZdoId) &&
            !_attribution.Summons.TryResolve(f.AttackerZdoId, out _) && _pendingSummon.Count < 256)
        { _pendingSummon.Add((commit, now, now + 3d)); return; }
        if (f.DotKind > 0)
        {
            DotKind kind = (DotKind)(f.DotKind - 1);
            if (!_attribution.HasDotState(f.VictimZdoId, kind) && _pendingDot.Count < 256)
            { _pendingDot.Add((commit, now, now + 3d)); return; }
        }
        ProcessAttributed(commit, now);
    }

    private void ProcessAttributed(DamageCommit commit, double now)
    {
        CombatMeterPerformance performance = GetPerformance();
        long started = performance != null ? PerformanceTimestamp() : 0;
        try
        {
            AttributedDamageEvent attributed = _attribution.Resolve(commit);
            bool any = false; float sum = 0f;
            foreach (DamagePortion p in attributed.DamageDone) { if (p.PlayerId.HasValue) any = true; sum += p.Damage; }
            Plugin.MagicLog((any ? "MagicDamageAttributed" : "MagicDamageUnattributed") +
                " event=" + commit.Id + " portions=" + attributed.DamageDone.Count + " sum=" + sum.ToString(CultureInfo.InvariantCulture));
            if (commit.Facts.DotKind > 0)
                Plugin.MagicLog("DotTickDistributed event=" + commit.Id + " kind=" + (DotKind)(commit.Facts.DotKind - 1) +
                    " effective=" + commit.EffectiveHpLoss.ToString(CultureInfo.InvariantCulture) + " portions=" + attributed.DamageDone.Count);
            double eventTime = EventTime(commit);
            _encounter.Accept(attributed, now, eventTime);
            if (_clusters.Accept(attributed, now, eventTime) == null && IsCurrentPerformance(performance)) performance.DamageIgnored++;

        }
        finally { if (IsCurrentPerformance(performance)) performance.CommitProcessing.Record(PerformanceTimestamp() - started); }
    }

    private double EventTime(DamageCommit commit)
    {
        double projected = _clockAnchorMonotonic +
            (commit.TimestampUtcTicks - _clockAnchorUtcTicks) / (double)TimeSpan.TicksPerSecond;
        return Math.Max(0d, projected);
    }

    private void Receive(ZRoutedRpc rpc, long sender, ZPackage package)
    {
        if (_disposed || !ReferenceEquals(rpc, _rpc) || !EnsureSession() || !_session.IsHost) return;
        try
        {
            DamageCommit commit = CommitCodec.Decode(Bytes(package));
            CombatMeterPerformance performance = GetPerformance();
            if (performance != null) performance.DamageObserved++;
            Acceptance result = _session.ProcessDamageCommit(commit, sender, CommitOrigin.Remote);
            // Reply only to the actual nonzero sender, never a payload-supplied destination/broadcast.
            if (sender != 0 && sender != _session.Peer)
                rpc.InvokeRoutedRPC(sender, AckRpc, new ZPackage(CommitCodec.EncodeAck(commit.Id, result)));
        }
        catch (Exception ex)
        { Plugin.TransportLog("DamageCommitRejected " + new LogFields().Text("Reason", "MalformedOrHandlerFailure").Text("Exception", ex.GetType().Name)); }
    }

    private void ReceiveAck(ZRoutedRpc rpc, long sender, ZPackage package)
    {
        if (_disposed || !ReferenceEquals(rpc, _rpc) || !EnsureSession() || _session.IsHost) return;
        ZNetPeer host = _net.GetServerPeer();
        if (host == null || sender != host.m_uid || sender == 0) return;
        try
        {
            var ack = CommitCodec.DecodeAck(Bytes(package));
            _session.Acknowledge(ack.Id, ack.Result);
            // Next queued item is sent in Update, avoiding recursive synchronous ACK chains.
        }
        catch (Exception ex)
        { Plugin.TransportLog("DamageCommitAckRejected " + new LogFields().Text("Exception", ex.GetType().Name)); }
    }

    private static byte[] Bytes(ZPackage package)
    {
        if (package == null || package.Size() > CommitCodec.MaxPacketBytes) throw new InvalidDataException("PacketSize");
        return package.GetArray();
    }

    internal void Disconnected(ZNet net)
    {
        if (ReferenceEquals(net, _net) && !net.IsServer() && net.GetServerPeer() == null) Close("Disconnected");
    }

    internal void Stop(ZNet net)
    {
        if (!ReferenceEquals(net, _net)) return;
        _blockedNet = new WeakReference<ZNet>(net); // Block restart without retaining a destroyed world.
        Close("WorldStopped");
        _net = null; _rpc = null;
    }

    internal void Dispose()
    {
        _disposed = true; Close("PluginStopped"); _net = null; _rpc = null;
    }

    private void Close(string reason)
    {
        ReportPerformance(true);
        _session?.Close(reason);
        _encounter?.Reset();
        _clusters?.Reset();
        _attribution?.Reset();
        _attributionAcceptor.Reset(); _attributionOutbox.Reset(); _pendingSummon.Clear(); _pendingDot.Clear();
        _snapshotStore.Clear(); _snapshotEpoch = Guid.Empty; _snapshotSequence = 0; _nextSnapshotAt = 0d;
        _lastLocalSnapshotDiagnosticKey = null; _lastPublishedSnapshotDiagnosticKey = null;
        _lastAcceptedSnapshotDiagnosticKey = null; _nextSnapshotReceivedDiagnosticAt = 0d;
        _session = null;
        _encounter = null;
        _clusters = null;
        _performance = null;
        _encounterSettings = null;
        _attribution = null;
        _attributionSequence = null;
    }

    private bool IsBlocked(ZNet net) => _blockedNet != null && _blockedNet.TryGetTarget(out ZNet blocked) && ReferenceEquals(net, blocked);

    private static void Log(string kind, DamageCommit c, string detail)
    {
        var fields = new LogFields().Text("LoggedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (c != null)
        {
            fields.Text("EventId", c.Id.ToString()).Text("SourcePeerId", c.SourcePeerId.ToString(CultureInfo.InvariantCulture))
                .Text("VictimZDOID", c.Facts.VictimZdoId).Text("VictimName", c.Facts.VictimName)
                .Flag("VictimIsPlayer", c.Facts.VictimIsPlayer)
                .Text("VictimPlayerID", c.Facts.VictimPlayerId?.ToString(CultureInfo.InvariantCulture))
                .Text("AttackerClass", c.Facts.Attacker.ToString())
                .Text("AttackerPlayerID", c.Facts.AttackerPlayerId?.ToString(CultureInfo.InvariantCulture))
                .Text("AttackerName", c.Facts.AttackerName)
                .Text("HitType", ((HitData.HitType)c.Facts.HitType).ToString())
                .Number("EffectiveHpLoss", c.EffectiveHpLoss)
                .Text("TimestampUtcTicks", c.TimestampUtcTicks.ToString(CultureInfo.InvariantCulture));
        }
        fields.Text(kind == "DamageCommitCreated" ? "Route" : kind == "DamageCommitAccepted" ? "Origin" : "Reason", detail);
        string message = kind + " " + fields;
        if (kind == "DamageCommitAbandoned" || kind == "DamageCommitDeliveryFailed" || kind == "DamageCommitObservationRejected") Plugin.Warn(message);
        else Plugin.TransportLog(message);
    }
}
