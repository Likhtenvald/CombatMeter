using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using DiagnosticDamageProbe.Configuration;
using System.Linq;
using System.Collections.Generic;
using DiagnosticDamageProbe;
using DiagnosticDamageProbe.Diagnostics;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Snapshot;
using DiagnosticDamageProbe.Transport;

internal static class PerformanceChecks
{
    private static int _passed;
    internal static int Run()
    {
        Test("timing count total and maximum use supplied ticks", () =>
        { var t = new TimingCounter(); t.Record(5); t.Record(15); t.Record(10); Eq(3L, t.Count); Eq(30L, t.TotalTicks); Eq(15L, t.MaxTicks); Eq(10d, t.AverageMicroseconds(1000000)); Eq(15d, t.MaxMicroseconds(1000000)); });
        Test("zero-count average is finite zero", () =>
        { var t = new TimingCounter(); Eq(0d, t.AverageMicroseconds(1)); Eq(0d, t.MaxMicroseconds(1)); });
        Test("all workload and category counters increment", () =>
        {
            var p = Perf(); p.DamageObserved++; p.DamageAccepted++; p.DamageIgnored++;
            p.CommitProcessing.Record(12); p.SnapshotCycle.Record(13); p.RecordIdentity(14, true); p.RecordIdentity(15, false);
            p.RecordBuild(16, false); p.RecordBuild(17, true); p.Serialization.Record(18);
            Eq(1L, p.DamageObserved); Eq(1L, p.DamageAccepted); Eq(1L, p.DamageIgnored);
            Eq(2L, p.IdentityResolution.Count); Eq(29L, p.IdentityResolution.TotalTicks); Eq(1L, p.IdentityResolutionFailures);
            Eq(2L, p.SnapshotBuild.Count); Eq(1L, p.EmptySnapshotsBuilt); Eq(1L, p.ActiveSnapshotsBuilt);
        });
        Test("payload byte totals and max accumulate actual supplied sizes", () =>
        { var p = Perf(); p.RecordSend(52); p.RecordSend(140); p.RecordSend(80); Eq(3L, p.SnapshotsSent); Eq(272L, p.SnapshotBytesSent); Eq(140L, p.SnapshotMaxBytes); });
        Test("report resets every interval count total and maximum", () =>
        {
            var p = Perf(); p.DamageObserved = p.DamageAccepted = p.DamageIgnored = 3;
            p.CommitProcessing.Record(1); p.SnapshotCycle.Record(2); p.RecordIdentity(3, false); p.RecordBuild(4, true); p.RecordBuild(5, false); p.Serialization.Record(6); p.RecordSend(52);
            True(p.TryReport(60000000, true, 2) != null);
            Eq(0L, p.DamageObserved + p.DamageAccepted + p.DamageIgnored);
            foreach (var t in new[] { p.CommitProcessing, p.SnapshotCycle, p.IdentityResolution, p.SnapshotBuild, p.Serialization })
            { Eq(0L, t.Count); Eq(0L, t.TotalTicks); Eq(0L, t.MaxTicks); }
            Eq(0L, p.IdentityResolutionFailures + p.EmptySnapshotsBuilt + p.ActiveSnapshotsBuilt + p.SnapshotsSent + p.SnapshotBytesSent + p.SnapshotMaxBytes);
        });
        Test("report interval uses elapsed monotonic ticks and actual duration", () =>
        {
            var p = Perf(); Eq<string>(null, p.TryReport(59999999, true, 0));
            var fields = Fields(p.TryReport(61000000, true, 0)); Eq("61.000", fields["windowSeconds"]);
            Eq<string>(null, p.TryReport(120000000, true, 0)); True(p.TryReport(121000000, true, 0) != null);
        });
        Test("partial close window reports actual seconds and byte rate", () =>
        { var p = Perf(); p.RecordSend(120); var fields = Fields(p.TryReport(2500000, true, 0, true)); Eq("2.500", fields["windowSeconds"]); Eq("48.000", fields["bytesPerSecond"]); });
        Test("disabled diagnostics reset due windows without report", () =>
        { var p = Perf(); p.RecordSend(100); Eq<string>(null, p.TryReport(60000000, false, 0)); Eq(0L, p.SnapshotsSent); Eq<string>(null, p.TryReport(60000000, true, 0, true)); });
        Test("report is single-line invariant with zero-count timing values", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = new CultureInfo("ru-RU"); var p = Perf(); p.CommitProcessing.Record(1250); string line = p.TryReport(60000000, true, 0);
                True(line.StartsWith("CombatMeterPerf ")); True(!line.Contains('\n') && !line.Contains('\r'));
                var fields = Fields(line); Eq("1250.000", fields["commitAvgUs"]); Eq("0.000", fields["serializationAvgUs"]);
            } finally { CultureInfo.CurrentCulture = previous; }
        });
        Test("window max does not leak into next window; context is current", () =>
        {
            var p = Perf(); p.ReadyPeers = 3; p.CommitProcessing.Record(100); p.TryReport(60000000, true, 4);
            p.CommitProcessing.Record(2); var fields = Fields(p.TryReport(120000000, true, 1));
            Eq("2.000", fields["commitMaxUs"]); Eq("3", fields["readyPeers"]); Eq("1", fields["activeClusters"]);
        });
        Test("host counters distinguish attempts accepted commits and ignored policy", () =>
        {
            var h = new Host(); h.Hit(1001, 10); h.Adapter.Observe(101, new DamageFacts(99, 100, true, 1001, AttackerClass.None, null, 3, "P", ""), 2);
            var c = new DamageCommit(new EventId(202, Guid.NewGuid(), 1), Facts(2002, 20), 4, DateTime.UtcNow.Ticks);
            for (int i = 0; i < 2; i++) h.Rpc.Handlers[DamageCommitTransport.CommitRpc](202, new ZPackage(CommitCodec.Encode(c)));
            var p = h.Adapter.Performance; Eq(4L, p.DamageObserved); Eq(3L, p.DamageAccepted); Eq(1L, p.DamageIgnored); Eq(3L, p.CommitProcessing.Count);
            Eq(2, h.Adapter.Clusters.ActiveCount);
        });
        Test("snapshot metrics use sent ZPackage sizes and exclude local apply", () =>
        {
            var h = new Host(); h.Hit(1001, 10); h.Adapter.Update(); var p = h.Adapter.Performance;
            Eq(1L, p.SnapshotCycle.Count); Eq(2L, p.IdentityResolution.Count); Eq(2L, p.SnapshotBuild.Count);
            Eq(1L, p.ActiveSnapshotsBuilt); Eq(1L, p.EmptySnapshotsBuilt); Eq(1L, p.Serialization.Count);
            Eq(1L, p.SnapshotsSent); Eq((long)h.PayloadSizes.Single(), p.SnapshotBytesSent); Eq((long)h.PayloadSizes.Single(), p.SnapshotMaxBytes);
            Eq(1, p.ReadyPeers); Eq(0L, p.IdentityResolutionFailures);
        });
        Test("unknown recipient increments identity failure without changing routing", () =>
        {
            var h = new Host(); Player.Instances.RemoveAll(player => player.PlayerId == 2002); h.Hit(1001, 10); h.Hit(2002, 20); h.Adapter.Update();
            Eq(1L, h.Adapter.Performance.IdentityResolutionFailures); Eq(EncounterState.NoEncounter, h.Remote.Single().EncounterState);
            True(h.Adapter.Clusters.TryGetClusterForPlayer(2002, out _)); Eq(2, h.Adapter.Clusters.ActiveCount);
        });
        Test("timing instrumentation preserves shared sequence frequency and cluster isolation", () =>
        {
            var h = new Host(); h.Hit(1001, 10); h.Hit(2002, 20); h.Adapter.Update();
            True(h.Adapter.SnapshotStore.TryGetLatest(out var local)); Eq(1L, local.Sequence); Eq(local.Sequence, h.Remote.Last().Sequence);
            Eq(1001L, local.Players.Single().PlayerId); Eq(2002L, h.Remote.Last().Players.Single().PlayerId);
            h.Now = .49; h.Adapter.Update(); Eq(1L, h.Adapter.Performance.SnapshotCycle.Count);
            h.Now = .5; h.Adapter.Update(); Eq(2L, h.Adapter.Performance.SnapshotCycle.Count); Eq(2L, h.Remote.Last().Sequence);
        });
        Test("timed ignored commits preserve Recovery ticket and snapshot state", () =>
        {
            var h = new Host(); h.Hit(1001, 10); h.Now = 1; h.Adapter.ObservePlayerDeath(1001); h.Now = 21; h.Adapter.Update();
            True(h.Adapter.Clusters.TryGetClusterForPlayer(1001, out var c)); c.Encounter.TryGetRecoveryTicket(1001, out var ticket);
            h.Adapter.Observe(101, new DamageFacts(99, 100, true, 1001, AttackerClass.None, null, 3, "P", ""), 2);
            Eq(EncounterState.Recovery, c.Encounter.State); True(c.Encounter.TryGetRecoveryTicket(1001, out var after)); True(ReferenceEquals(ticket, after));
            Eq(181d, after.ExpiryTime); Eq(1L, h.Adapter.Performance.DamageIgnored);
        });
        Test("deferred commit is accepted once and processed once when resolved", () =>
        {
            var h = new Host(); h.Adapter.Observe(101, new DamageFacts(99, 100, true, 1001, AttackerClass.NPC, null, 1, "P", "NPC", 99, 30), 2);
            Eq(1L, h.Adapter.Performance.DamageAccepted); Eq(0L, h.Adapter.Performance.CommitProcessing.Count);
            h.Now = 4; h.Adapter.Update(); Eq(1L, h.Adapter.Performance.DamageAccepted); Eq(1L, h.Adapter.Performance.CommitProcessing.Count);
        });
        Test("client does not allocate or report host counters", () =>
        {
            ZNet.instance = new ZNet { Server = false, ServerPeer = new ZNetPeer { m_uid = 101 } };
            ZRoutedRpc.instance = new ZRoutedRpc(); ZDOMan.Session = 202;
            var a = new DamageCommitTransport(() => 0); a.Bind(ZNet.instance); Eq<CombatMeterPerformance>(null, a.Performance); a.Dispose();
        });
        Test("partial window emits one diagnostic on close only when enabled", () =>
        {
            Plugin.TransportMessages.Clear(); Plugin.PerformanceLoggingEnabled = true;
            try
            {
                var h = new Host(); h.Hit(1001, 10); h.Adapter.Update(); Eq(0, PerfLines());
                h.PerformanceTicks = Stopwatch.Frequency;
                h.Adapter.Dispose(); Eq(1, PerfLines()); h.Adapter.Dispose(); Eq(1, PerfLines());
                Plugin.PerformanceLoggingEnabled = false; var silent = new Host(); silent.Hit(1001, 10); silent.Adapter.Dispose(); Eq(1, PerfLines());
            } finally { Plugin.PerformanceLoggingEnabled = false; }
        });

        Test("dedicated config defaults off and binds its own lifecycle", () =>
        {
            var entry = ConfigCatalog.Find("Diagnostics", "EnablePerformanceDiagnosticLogging");
            Eq("false", entry.DefaultValue); True(entry.LiveUpdate); Eq(ConfigAuthority.Local, entry.Authority);
            string source = PluginSource();
            True(Regex.IsMatch(source, @"EnablePerformanceDiagnosticLogging\s*=\s*Config.Bind\(""Diagnostics"",\s*""EnablePerformanceDiagnosticLogging"",\s*false,"));
            True(source.Contains("EnablePerformanceDiagnosticLogging.SettingChanged += OnPerformanceLoggingChanged;"));
            True(source.Contains("EnablePerformanceDiagnosticLogging.SettingChanged -= OnPerformanceLoggingChanged;"));
            True(source.Contains("EnablePerformanceDiagnosticLogging = null;"));
        });
        Test("production performance gate references only the dedicated flag", () =>
        {
            string expression = Regex.Match(PluginSource(), @"bool PerformanceLoggingEnabled\s*=>\s*([^;]+);").Groups[1].Value;
            Eq("EnablePerformanceDiagnosticLogging?.Value == true", expression);
        });
        Test("legacy configs recognize opt-in and missing key remains absent", () =>
        {
            var old = LegacyConfigMigration.ParseKnown("[Diagnostics]\nEnableDiagnosticLogging=true");
            True(!LegacyConfigMigration.TryGet(old, "Diagnostics", "EnablePerformanceDiagnosticLogging", out _));
            var optIn = LegacyConfigMigration.ParseKnown("[Diagnostics]\nEnablePerformanceDiagnosticLogging=true");
            True(LegacyConfigMigration.TryGet(optIn, "Diagnostics", "EnablePerformanceDiagnosticLogging", out var value)); Eq("true", value);
        });
        Test("disabled host bypasses every telemetry clock and counter path", () =>
        {
            Plugin.PerformanceLoggingEnabled = false; Plugin.TransportMessages.Clear();
            var h = new Host(); h.Hit(1001, 10); h.Environment(HitData.HitType.Fall); h.RemoteHit();
            h.Now = 61; h.Adapter.Update(); h.Adapter.Dispose();
            Eq(0, h.PerformanceClockReads); Eq<CombatMeterPerformance>(null, h.Adapter.Performance); Eq(0, PerfLines());
        });
        Test("enabled host reports at sixty synthetic seconds with existing fields", () =>
        {
            Plugin.TransportMessages.Clear(); var h = new Host(); h.Hit(1001, 10); h.Adapter.Update();
            h.PerformanceTicks = 59 * Stopwatch.Frequency; h.Adapter.Update(); Eq(0, PerfLines());
            h.PerformanceTicks = 60 * Stopwatch.Frequency; h.Adapter.Update(); Eq(1, PerfLines());
            var fields = Fields(Plugin.TransportMessages.Single(x => x.StartsWith("CombatMeterPerf ")));
            Eq("60.000", fields["windowSeconds"]); Eq("1", fields["damageObserved"]); Eq("1", fields["commitCount"]);
            Eq("2", fields["snapshotBuildCount"]); Eq("1", fields["activeSnapshots"]); Eq("1", fields["emptySnapshots"]);
            Eq("1", fields["snapshotsSent"]); Eq(h.PayloadSizes.Single().ToString(CultureInfo.InvariantCulture), fields["bytesSent"]);
            Eq(0L, h.Adapter.Performance.CommitProcessing.Count);
        });
        Test("enable starts clean and excludes disabled damage and elapsed time", () =>
        {
            Plugin.PerformanceLoggingEnabled = false; Plugin.TransportMessages.Clear();
            var h = new Host(); h.Hit(1001, 10); h.PerformanceTicks = 100 * Stopwatch.Frequency; h.Toggle(true);
            Eq(0L, h.Adapter.Performance.DamageObserved); Eq(0L, h.Adapter.Performance.CommitProcessing.Count);
            h.Adapter.Update(); Eq(0, PerfLines());
            h.PerformanceTicks = 159 * Stopwatch.Frequency; h.Adapter.Update(); Eq(0, PerfLines());
            h.PerformanceTicks = 160 * Stopwatch.Frequency; h.Adapter.Update(); Eq(1, PerfLines());
            Eq("0", Fields(Plugin.TransportMessages.Last(x => x.StartsWith("CombatMeterPerf ")))["damageAccepted"]);
        });
        Test("disable discards partial window immediately and close stays silent", () =>
        {
            Plugin.TransportMessages.Clear(); var h = new Host(); h.Hit(1001, 10);
            var discarded = h.Adapter.Performance; int reads = h.PerformanceClockReads;
            h.Toggle(false); Eq<CombatMeterPerformance>(null, h.Adapter.Performance); Eq(reads, h.PerformanceClockReads);
            h.Hit(2002, 20); h.Adapter.Update(); h.Adapter.Dispose();
            Eq(reads, h.PerformanceClockReads); Eq(1L, discarded.DamageAccepted); Eq(0, PerfLines());
        });
        Test("off then on between updates starts another fresh window", () =>
        {
            Plugin.TransportMessages.Clear(); var h = new Host(); h.Hit(1001, 10); var first = h.Adapter.Performance;
            h.PerformanceTicks = 30 * Stopwatch.Frequency; h.Toggle(false); h.Toggle(true);
            True(!ReferenceEquals(first, h.Adapter.Performance)); Eq(0L, h.Adapter.Performance.DamageAccepted);
            h.PerformanceTicks = 60 * Stopwatch.Frequency; h.Adapter.Update(); Eq(0, PerfLines());
            h.PerformanceTicks = 90 * Stopwatch.Frequency; h.Adapter.Update(); Eq(1, PerfLines());
        });
        Test("toggle during send cannot finish timing into discarded or new window", () =>
        {
            var h = new Host(); h.Hit(1001, 10); var first = h.Adapter.Performance;
            var send = h.Rpc.Send;
            h.Rpc.Send = (peer, name, package) => { send(peer, name, package); h.Toggle(false); h.Toggle(true); };
            h.Adapter.Update(); var fresh = h.Adapter.Performance;
            Eq(0L, first.SnapshotCycle.Count); Eq(0L, first.SnapshotsSent);
            Eq(0L, fresh.SnapshotCycle.Count); Eq(0L, fresh.SnapshotsSent); Eq(0L, fresh.SnapshotBuild.Count);
        });
        foreach (bool enabled in new[] { false, true })
        {
            Test("routing isolation merge local apply and shared sequence with telemetry=" + enabled, () =>
            {
                Plugin.PerformanceLoggingEnabled = enabled;
                var h = new Host(); h.Hit(1001, 10); h.Adapter.Update();
                True(h.Adapter.SnapshotStore.TryGetLatest(out var local)); Eq(1L, local.Sequence);
                Eq(EncounterState.NoEncounter, h.Remote.Last().EncounterState); Eq(local.Sequence, h.Remote.Last().Sequence);
                h.Now = .5; h.Hit(2002, 20); h.Adapter.Update(); Eq(2, h.Adapter.Clusters.ActiveCount);
                True(h.Adapter.SnapshotStore.TryGetLatest(out local)); Eq(1001L, local.Players.Single().PlayerId);
                Eq(2002L, h.Remote.Last().Players.Single().PlayerId); Eq(local.Sequence, h.Remote.Last().Sequence);
                h.Now = 1; h.Hit(1001, 20); h.Adapter.Update(); Eq(1, h.Adapter.Clusters.ActiveCount);
                True(h.Adapter.SnapshotStore.TryGetLatest(out local)); Eq(2, local.Players.Count);
                Eq(2, h.Remote.Last().Players.Count); Eq(local.Sequence, h.Remote.Last().Sequence);
                Eq(12f, local.Players.Single(p => p.PlayerId == 1001).DamageDone);
                Eq(6f, local.Players.Single(p => p.PlayerId == 2002).DamageDone);
                // Fixture validates each actual RPC destination equals the nonzero remote peer.
                if (!enabled) Eq(0, h.PerformanceClockReads);
            });
            Test("environmental and Recovery policy with telemetry=" + enabled, () =>
            {
                Plugin.PerformanceLoggingEnabled = enabled; var h = new Host();
                foreach (var hit in new[] { HitData.HitType.Fall, HitData.HitType.Drowning, HitData.HitType.Smoke }) h.Environment(hit);
                Eq(0, h.Adapter.Clusters.ActiveCount);
                foreach (var hit in new[] { HitData.HitType.Poisoned, HitData.HitType.Burning, HitData.HitType.Tree, HitData.HitType.Incinerator }) h.Environment(hit);
                True(h.Adapter.Clusters.TryGetClusterForPlayer(1001, out var cluster));
                Eq(8f, cluster.Encounter.Statistics.Players.Single().DamageTaken);
                h.Now = 1; h.Adapter.ObservePlayerDeath(1001); h.Now = 21; h.Adapter.Update();
                Eq(EncounterState.Recovery, cluster.Encounter.State);
                True(cluster.Encounter.TryGetRecoveryTicket(1001, out var ticket));
                h.Environment(HitData.HitType.Smoke); Eq(EncounterState.Recovery, cluster.Encounter.State);
                True(cluster.Encounter.TryGetRecoveryTicket(1001, out var after)); True(ReferenceEquals(ticket, after)); Eq(181d, after.ExpiryTime);
                h.Now = 181; h.Adapter.Update(); Eq(0, h.Adapter.Clusters.ActiveCount);
                if (!enabled) Eq(0, h.PerformanceClockReads);
            });
        }
        return _passed;
    }
    private static CombatMeterPerformance Perf() => new CombatMeterPerformance(0, 1000000);
    private static Dictionary<string, string> Fields(string text) => text.Split(' ').Skip(1).Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
    private static int PerfLines() => Plugin.TransportMessages.Count(x => x.StartsWith("CombatMeterPerf "));
    private static DamageFacts Facts(long player, uint npc) => new DamageFacts(99, npc, false, null, AttackerClass.Player, player, 2, "NPC", "P");
    private sealed class Host
    {
        internal double Now;
        internal long PerformanceTicks;
        internal int PerformanceClockReads;
        internal readonly DamageCommitTransport Adapter;
        internal readonly ZRoutedRpc Rpc = new ZRoutedRpc();
        internal readonly List<int> PayloadSizes = new List<int>();
        internal readonly List<CombatSnapshot> Remote = new List<CombatSnapshot>();
        internal Host()
        {
            ZDOMan.instance = new ZDOMan(); ZDOMan.Session = 101;
            ZNet.instance = new ZNet { Server = true, SinglePlayer = false }; ZNet.instance.Peers.Add(new ZNetPeer { m_uid = 202 });
            ZRoutedRpc.instance = Rpc; Player.Instances.Clear(); Player.m_localPlayer = new Player { PlayerId = 1001 };
            Player.m_localPlayer.View.Zdo.Owner = 101; Player.Instances.Add(Player.m_localPlayer);
            var remote = new Player { PlayerId = 2002 }; remote.View.Zdo.Owner = 202; Player.Instances.Add(remote);
            Rpc.Send = (peer, name, package) => { if (name == DamageCommitTransport.SnapshotRpc) { Eq(202L, peer); PayloadSizes.Add(package.Size()); Remote.Add(CombatSnapshotCodec.Decode(package.GetArray())); } };
            Adapter = new DamageCommitTransport(() => Now, performanceTimestamp: () => { PerformanceClockReads++; return PerformanceTicks; }); Adapter.Bind(ZNet.instance);
        }
        internal void Toggle(bool enabled) { Plugin.PerformanceLoggingEnabled = enabled; Adapter.RefreshPerformanceSettings(); }
        internal void Environment(HitData.HitType hit) => Adapter.Observe(101,
            new DamageFacts(99, 100, true, 1001, AttackerClass.None, null, (byte)hit, "P", ""), 2);
        internal void RemoteHit() => Rpc.Handlers[DamageCommitTransport.CommitRpc](202, new ZPackage(CommitCodec.Encode(
            new DamageCommit(new EventId(202, Guid.NewGuid(), 1), Facts(2002, 20), 4, DateTime.UtcNow.Ticks))));
        internal void Hit(long player, uint npc) => Adapter.Observe(101, Facts(player, npc), 6);
    }
    private static string PluginSource()
    {
        // ProbeChecks substitutes Plugin for adapter tests; audit the actual BepInEx binding separately.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string path = Path.Combine(directory.FullName, "src", "Plugin.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Production Plugin.cs");
    }
    private static void Test(string name, Action action)
    {
        bool previous = Plugin.PerformanceLoggingEnabled;
        try { Plugin.PerformanceLoggingEnabled = true; action(); _passed++; Console.WriteLine("PASS performance: " + name); }
        finally { Plugin.PerformanceLoggingEnabled = previous; }
    }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Performance assertion failed"); }
}
