using System;
using System.Collections.Generic;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;
using DiagnosticDamageProbe.Attribution;

namespace DiagnosticDamageProbe.Encounter;

internal enum EncounterState
{
    NoEncounter,
    Active,
    Recovery,
    Finished
}

internal enum PlayerDeathResult
{
    Committed,
    NoActiveEncounter,
    NoActiveParticipant,
    Duplicate
}

// Pure deterministic core. All time values are monotonic seconds supplied by the caller.
internal sealed class EncounterManager
{
    private readonly Dictionary<long, EncounterParticipant> _participants = new Dictionary<long, EncounterParticipant>();
    private readonly Dictionary<long, RecoveryTicket> _recoveryTickets = new Dictionary<long, RecoveryTicket>();
    private readonly Dictionary<long, PlayerDpsActivityTracker> _dpsActivity = new Dictionary<long, PlayerDpsActivityTracker>();
    private readonly Action<string> _log;
    internal CombatStatisticsAggregator Statistics { get; }
    internal EncounterSettings Settings { get; }
    internal double SoftTimeout => Settings.SoftTimeoutSeconds;
    internal double RecoveryTimeout => Settings.RecoveryTimeoutSeconds;
    internal double DpsIdleTimeout => Settings.DpsIdleTimeoutSeconds;
    internal EncounterState State { get; private set; }
    internal double StartTime { get; private set; }
    internal double LastActivityTime { get; private set; }
    internal double EndTime { get; private set; }
    internal long EncounterId { get; private set; }
    internal IEnumerable<EncounterParticipant> Participants => _participants.Values;
    internal int RecoveryTicketCount => _recoveryTickets.Count;

    internal EncounterManager(CombatStatisticsAggregator statistics, EncounterSettings settings = null, Action<string> log = null)
    {
        Statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        Settings = settings ?? new EncounterSettings();
        _log = log;
    }

    // Called only after canonical host validation/dedup accepted the commit.
    internal bool Accept(DamageCommit commit, double now, double? eventTime = null)
    {
        ValidateTime(now);
        double chronology = eventTime ?? now;
        ValidateTime(chronology);
        Update(now);
        PveClassification classification = CombatStatisticsAggregator.Classify(commit);
        if (!classification.IsRelevant) return false;

        bool resumed = State == EncounterState.Recovery;
        if (State == EncounterState.NoEncounter || State == EncounterState.Finished) Start(now);
        bool eligibleReengagement = TryClearRecoveryTicket(classification.PlayerId, chronology);
        Statistics.Apply(classification, commit.EffectiveHpLoss);
        if (!_participants.ContainsKey(classification.PlayerId))
            _participants.Add(classification.PlayerId, new EncounterParticipant(classification.PlayerId));
        if (classification.Kind == PveStatisticKind.DamageDone && commit.EffectiveHpLoss > 0f)
            RecordOffensiveActivity(classification.PlayerId, chronology);
        if (!resumed || eligibleReengagement)
        {
            LastActivityTime = Math.Max(LastActivityTime, chronology);
            State = EncounterState.Active;
            if (resumed) Log("EncounterStateChanged Recovery->Active reason=RelevantPveActivity");
        }
        return true;
    }

    internal bool Accept(AttributedDamageEvent attributed, double now, double? eventTime = null)
    {
        if (attributed.ApplyVanillaClassification) return Accept(attributed.Commit, now, eventTime);
        ValidateTime(now); double chronology = eventTime ?? now; ValidateTime(chronology); Update(now);
        if (attributed.Commit.Facts.VictimIsPlayer) return false;
        bool relevant = false;
        foreach (DamagePortion portion in attributed.DamageDone)
            if (portion.PlayerId.HasValue && portion.Damage > 0f) { relevant = true; break; }
        if (!relevant) return false;
        bool resumed = State == EncounterState.Recovery;
        if (State == EncounterState.NoEncounter || State == EncounterState.Finished) Start(now);
        bool eligibleReengagement = false;
        foreach (DamagePortion portion in attributed.DamageDone)
        {
            if (!portion.PlayerId.HasValue || !(portion.Damage > 0f)) continue;
            eligibleReengagement |= TryClearRecoveryTicket(portion.PlayerId.Value, chronology);
            Statistics.Apply(new PveClassification(PveStatisticKind.DamageDone, portion.PlayerId.Value, ""), portion.Damage);
            if (!_participants.ContainsKey(portion.PlayerId.Value))
                _participants.Add(portion.PlayerId.Value, new EncounterParticipant(portion.PlayerId.Value));
            RecordOffensiveActivity(portion.PlayerId.Value, chronology);
        }
        if (!resumed || eligibleReengagement)
        {
            LastActivityTime = Math.Max(LastActivityTime, chronology); State = EncounterState.Active;
            if (resumed) Log("EncounterStateChanged Recovery->Active reason=RelevantPveActivity");
        }
        return true;
    }

    internal PlayerDeathResult OnPlayerDied(long playerId, double now, string incarnation = null)
    {
        ValidateTime(now);
        Update(now);
        if (State != EncounterState.Active && State != EncounterState.Recovery)
            return PlayerDeathResult.NoActiveEncounter;
        if (!_participants.TryGetValue(playerId, out EncounterParticipant participant))
            return PlayerDeathResult.NoActiveParticipant;
        if (!participant.Died(incarnation)) return PlayerDeathResult.Duplicate;
        if (_recoveryTickets.TryGetValue(playerId, out RecoveryTicket ticket))
        {
            ticket.Refresh(now, RecoveryTimeout);
            Log("RecoveryTicketRefreshed player=" + playerId + " deathTime=" + now + " expiry=" + ticket.ExpiryTime);
        }
        else
        {
            ticket = new RecoveryTicket(playerId, now, RecoveryTimeout);
            _recoveryTickets.Add(playerId, ticket);
            Log("RecoveryTicketCreated player=" + playerId + " deathTime=" + now + " expiry=" + ticket.ExpiryTime);
        }
        return PlayerDeathResult.Committed;
    }

    internal void OnPlayerRespawned(long playerId, double now)
    {
        ValidateTime(now);
        Update(now);
        if ((State == EncounterState.Active || State == EncounterState.Recovery) &&
            _participants.TryGetValue(playerId, out EncounterParticipant participant))
            participant.Respawned();
    }

    internal void Update(double now)
    {
        ValidateTime(now);
        if (State != EncounterState.Active && State != EncounterState.Recovery) return;
        double lastExpiredDeadline = ExpireRecoveryTickets(now);
        if (State == EncounterState.Recovery)
        {
            if (_recoveryTickets.Count == 0)
                Finish(lastExpiredDeadline > 0d ? lastExpiredDeadline : now, "Recovery", "NoPendingRecovery");
            return;
        }
        double inactive = now - LastActivityTime;
        if (inactive < SoftTimeout) return;

        if (_recoveryTickets.Count == 0)
        {
            Finish(LastActivityTime + SoftTimeout, "Active", "SoftTimeoutNoRecovery");
            return;
        }
        State = EncounterState.Recovery;
        FreezeDpsActivity(now);
        Log("EncounterStateChanged Active->Recovery reason=PendingRecovery");
    }

    internal double Duration(double now)
    {
        ValidateTime(now);
        if (State == EncounterState.NoEncounter) return 0d;
        double end = State == EncounterState.Finished ? EndTime : now;
        return Math.Max(0d, end - StartTime);
    }

    internal double Dps(long playerId, double now)
    {
        double active = ActiveDpsTime(playerId, now);
        if (active <= 0d || !Statistics.TryGet(playerId, out PlayerCombatStatistics player)) return 0d;
        return player.DamageDone / active;
    }

    internal double ActiveDpsTime(long playerId, double now)
    {
        ValidateTime(now);
        if (!_dpsActivity.TryGetValue(playerId, out PlayerDpsActivityTracker tracker)) return 0d;
        bool mayGrow = State == EncounterState.Active;
        double boundary = State == EncounterState.Finished ? EndTime : now;
        return tracker.ActiveTime(boundary, DpsIdleTimeout, mayGrow);
    }

    internal bool TryGetParticipant(long playerId, out EncounterParticipant participant) =>
        _participants.TryGetValue(playerId, out participant);

    // Cluster-only lifecycle entry: IDs are allocated across all clusters in a session.
    internal void BeginCluster(long id, double now)
    {
        ValidateTime(now);
        if (State != EncounterState.NoEncounter || id <= 0) throw new InvalidOperationException("Invalid cluster start");
        Start(now);
        EncounterId = id;
    }

    // Membership guarantees disjoint players. Move their exact state objects, including
    // compacted DPS intervals, death-incarnation deduplication and ticket deadlines.
    // No replay, rounding, reset of the survivor, or fabricated offensive activity.
    internal void AbsorbDisjoint(EncounterManager source)
    {
        if (source == null || ReferenceEquals(this, source) || !ReferenceEquals(Settings, source.Settings) ||
            (State != EncounterState.Active && State != EncounterState.Recovery) ||
            (source.State != EncounterState.Active && source.State != EncounterState.Recovery))
            throw new InvalidOperationException("Cannot merge inactive or incompatible encounters");
        foreach (long player in source._participants.Keys)
            if (_participants.ContainsKey(player)) throw new InvalidOperationException("Cluster memberships overlap");
        Statistics.MoveDisjointFrom(source.Statistics);
        foreach (var pair in source._participants) _participants.Add(pair.Key, pair.Value);
        foreach (var pair in source._recoveryTickets) _recoveryTickets.Add(pair.Key, pair.Value);
        foreach (var pair in source._dpsActivity) _dpsActivity.Add(pair.Key, pair.Value);
        StartTime = Math.Min(StartTime, source.StartTime);
        LastActivityTime = Math.Max(LastActivityTime, source.LastActivityTime);
        if (source.State == EncounterState.Active) State = EncounterState.Active;
        source.Reset();
        // The caller now applies the bridge once through Accept, preserving its existing
        // event-time/reengagement guard (a delayed pre-death hit cannot clear a ticket).
    }
    internal void Reset()
    {
        Statistics.Reset();
        _participants.Clear();
        _recoveryTickets.Clear();
        _dpsActivity.Clear();
        State = EncounterState.NoEncounter;
        StartTime = LastActivityTime = EndTime = 0d;
        EncounterId = 0;
    }

    internal bool TryGetRecoveryTicket(long playerId, out RecoveryTicket ticket) =>
        _recoveryTickets.TryGetValue(playerId, out ticket);

    private void Start(double now)
    {
        Statistics.Reset();
        _participants.Clear();
        _recoveryTickets.Clear();
        _dpsActivity.Clear();
        StartTime = LastActivityTime = now;
        EndTime = 0d;
        EncounterId = checked(EncounterId + 1);
        State = EncounterState.Active;
    }

    private void Finish(double endTime, string from, string reason)
    {
        FreezeDpsActivity(endTime);
        EndTime = endTime;
        State = EncounterState.Finished;
        _recoveryTickets.Clear();
        Log("EncounterStateChanged " + from + "->Finished reason=" + reason);
    }

    private void RecordOffensiveActivity(long playerId, double eventTime)
    {
        if (!_dpsActivity.TryGetValue(playerId, out PlayerDpsActivityTracker tracker))
        { tracker = new PlayerDpsActivityTracker(); _dpsActivity.Add(playerId, tracker); }
        tracker.Add(Math.Max(StartTime, eventTime), DpsIdleTimeout);
    }

    private void FreezeDpsActivity(double boundary)
    { foreach (PlayerDpsActivityTracker tracker in _dpsActivity.Values) tracker.Freeze(boundary, DpsIdleTimeout); }

    private bool TryClearRecoveryTicket(long playerId, double eventTime)
    {
        if (!_recoveryTickets.TryGetValue(playerId, out RecoveryTicket ticket)) return true;
        if (eventTime <= ticket.DeathTime)
        {
            Log("RecoveryTicketClearIgnored player=" + playerId + " reason=EventNotAfterDeath eventTime=" +
                eventTime + " deathTime=" + ticket.DeathTime);
            return false;
        }
        _recoveryTickets.Remove(playerId);
        Log("RecoveryTicketCleared player=" + playerId + " reason=Reengaged eventTime=" + eventTime +
            " deathTime=" + ticket.DeathTime);
        return true;
    }

    private double ExpireRecoveryTickets(double now)
    {
        if (_recoveryTickets.Count == 0) return 0d;
        var expired = new List<long>(); double last = 0d;
        foreach (KeyValuePair<long, RecoveryTicket> pair in _recoveryTickets)
        {
            pair.Value.ApplyTimeout(RecoveryTimeout);
            if (now >= pair.Value.ExpiryTime) { expired.Add(pair.Key); last = Math.Max(last, pair.Value.ExpiryTime); }
        }
        foreach (long playerId in expired)
        {
            double expiry = _recoveryTickets[playerId].ExpiryTime;
            _recoveryTickets.Remove(playerId);
            Log("RecoveryTicketExpired player=" + playerId + " expiry=" + expiry);
        }
        return last;
    }

    private void Log(string message) => _log?.Invoke(message);

    private static void ValidateTime(double now)
    {
        if (double.IsNaN(now) || double.IsInfinity(now) || now < 0d) throw new ArgumentOutOfRangeException(nameof(now));
    }
}
