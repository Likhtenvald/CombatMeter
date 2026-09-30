using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using DiagnosticDamageProbe.Attribution;
using DiagnosticDamageProbe.Configuration;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Snapshot;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;
using DiagnosticDamageProbe.UI;

internal static class OptionalMetricsChecks
{
    private static int _passed;
    private static long _sequence;
    private static readonly Guid Epoch = Guid.NewGuid();
    internal static int Run()
    {
        Test("new statistics start at zero", () => { var s = new PlayerCombatStatistics(1, "A"); Eq(0f, s.LargestHit); Eq(0, s.Deaths); });
        Test("first accepted hit establishes largest", () => { var e = Manager(); e.Accept(Done(1, 40), 0); Eq(40f, Stats(e).LargestHit); });
        Test("smaller accepted hit retains largest", () => { var e = Manager(); e.Accept(Done(1, 40), 0); e.Accept(Done(1, 10), 1); Eq(40f, Stats(e).LargestHit); Eq(50f, Stats(e).DamageDone); });
        Test("larger accepted hit replaces largest", () => { var e = Manager(); e.Accept(Done(1, 40), 0); e.Accept(Done(1, 60), 1); Eq(60f, Stats(e).LargestHit); Eq(100f, Stats(e).DamageDone); });
        Test("damage taken cannot increase largest", () => { var e = Manager(); e.Accept(Done(1, 40), 0); e.Accept(Taken(1, 90, 1), 1); Eq(40f, Stats(e).LargestHit); Eq(90f, Stats(e).DamageTaken); });
        foreach (byte hit in new byte[] { 3, 4, 9 })
            Test("environment ignored for metrics " + hit, () => { var e = Manager(); e.Accept(Done(1, 40), 0); e.Accept(Taken(1, 90, hit), 1); Eq(40f, Stats(e).LargestHit); Eq(0f, Stats(e).DamageTaken); Eq(0, Stats(e).Deaths); });
        Test("attributed contributions retain each player's share", () =>
        {
            var e = Manager(); var commit = Done(1, 100);
            e.Accept(new AttributedDamageEvent(commit, new List<DamagePortion> { new DamagePortion(1, 35), new DamagePortion(2, 65) }, false), 0);
            Eq(35f, Stats(e, 1).LargestHit); Eq(65f, Stats(e, 2).LargestHit);
        });
        Test("duplicate and rejected commits cannot inflate metrics", () =>
        {
            var e = Manager(); var session = new CommitSession(101, Epoch, true, _ => { }, (_, _, _) => { }, c => e.Accept(c, 0));
            var hit = Done(1, 60);
            Eq(Acceptance.Accepted, session.ProcessDamageCommit(hit, 101, CommitOrigin.Remote));
            Eq(Acceptance.Duplicate, session.ProcessDamageCommit(hit, 101, CommitOrigin.Remote));
            Eq(Acceptance.SenderMismatch, session.ProcessDamageCommit(Done(1, 1000), 202, CommitOrigin.Remote));
            Eq(60f, Stats(e).LargestHit); Eq(60f, Stats(e).DamageDone);
        });
        Test("accepted death increments once", () => { var e = Active(); Eq(PlayerDeathResult.Committed, e.OnPlayerDied(1, 1, "life1")); Eq(1, Stats(e).Deaths); });
        Test("duplicate incarnation death does not increment", () => { var e = Active(); e.OnPlayerDied(1, 1, "life1"); Eq(PlayerDeathResult.Duplicate, e.OnPlayerDied(1, 2, "life1")); Eq(1, Stats(e).Deaths); });
        Test("duplicate anonymous death does not increment", () => { var e = Active(); e.OnPlayerDied(1, 1); Eq(PlayerDeathResult.Duplicate, e.OnPlayerDied(1, 2)); Eq(1, Stats(e).Deaths); });
        Test("Recovery preserves both metrics", () => { var e = Active(); e.OnPlayerDied(1, 1, "life1"); e.Update(20); Eq(EncounterState.Recovery, e.State); Eq(1, Stats(e).Deaths); Eq(40f, Stats(e).LargestHit); });
        Test("Recovery to Active preserves counts", () => { var e = Active(); e.OnPlayerDied(1, 1, "life1"); e.Update(20); e.Accept(Done(1, 5), 21); Eq(EncounterState.Active, e.State); Eq(1, Stats(e).Deaths); Eq(40f, Stats(e).LargestHit); });
        Test("second legitimate death accumulates", () => { var e = Active(); e.OnPlayerDied(1, 1, "life1"); e.OnPlayerRespawned(1, 2); e.Accept(Done(1, 5), 3); Eq(PlayerDeathResult.Committed, e.OnPlayerDied(1, 4, "life2")); Eq(2, Stats(e).Deaths); });
        Test("death outside encounter creates no statistics", () => { var e = Manager(); Eq(PlayerDeathResult.NoActiveEncounter, e.OnPlayerDied(1, 1)); Eq(0, e.Statistics.Count); Eq(EncounterState.NoEncounter, e.State); });
        Test("unknown participant death creates no row", () => { var e = Active(); Eq(PlayerDeathResult.NoActiveParticipant, e.OnPlayerDied(2, 1)); Eq(1, e.Statistics.Count); Eq(0, Stats(e).Deaths); });
        Test("finished encounter does not accept another death", () => { var e = Active(); e.Update(20); Eq(PlayerDeathResult.NoActiveEncounter, e.OnPlayerDied(1, 21)); Eq(0, Stats(e).Deaths); });
        Test("new encounter resets both values", () => { var e = Active(); e.OnPlayerDied(1, 1); e.Update(181); e.Accept(Taken(1, 3, 1), 182); Eq(0f, Stats(e).LargestHit); Eq(0, Stats(e).Deaths); });
        Test("merge preserves disjoint player metrics and statistics objects", () =>
        {
            var clusters = new CombatClusterManager();
            var a = clusters.Accept(Done(1, 40, 10), 0); var b = clusters.Accept(Done(2, 70, 20), 0);
            clusters.OnPlayerDied(1, 1, "a"); clusters.OnPlayerDied(2, 1, "b1");
            b.Encounter.OnPlayerRespawned(2, 2); clusters.Accept(Done(2, 5, 20), 2); clusters.OnPlayerDied(2, 3, "b2");
            var sa = Stats(a.Encounter, 1); var sb = Stats(b.Encounter, 2);
            var merged = clusters.Accept(Done(1, 5, 20), 4);
            True(ReferenceEquals(sa, Stats(merged.Encounter, 1))); True(ReferenceEquals(sb, Stats(merged.Encounter, 2)));
            Eq(40f, sa.LargestHit); Eq(1, sa.Deaths); Eq(70f, sb.LargestHit); Eq(2, sb.Deaths);
        });
        Test("v2 roundtrip preserves independent row metrics and zeros", () =>
        {
            var snapshot = Snapshot(new CombatSnapshotPlayer(1, "A", 100, 10, 0, 60, 1), new CombatSnapshotPlayer(2, "B", 150, 15, 0, 70, 2), new CombatSnapshotPlayer(3, "", 0, 0, 0));
            var copy = CombatSnapshotCodec.Decode(CombatSnapshotCodec.Encode(snapshot));
            Eq(60f, copy.Players[0].LargestHit); Eq(1, copy.Players[0].Deaths);
            Eq(70f, copy.Players[1].LargestHit); Eq(2, copy.Players[1].Deaths); Eq(0f, copy.Players[2].LargestHit); Eq(0, copy.Players[2].Deaths);
        });
        foreach (float bad in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Test("invalid LargestHit rejected " + bad, () =>
            {
                Fails(() => CombatSnapshotCodec.Encode(Snapshot(new CombatSnapshotPlayer(1, "", 0, 0, 0, bad))));
                var bytes = CombatSnapshotCodec.Encode(Snapshot(new CombatSnapshotPlayer(1, "", 0, 0, 0)));
                Array.Copy(BitConverter.GetBytes(bad), 0, bytes, bytes.Length - 8, 4); Fails(() => CombatSnapshotCodec.Decode(bytes));
            });
        Test("negative deaths rejected by encoder and decoder", () =>
        {
            Fails(() => CombatSnapshotCodec.Encode(Snapshot(new CombatSnapshotPlayer(1, "", 0, 0, 0, 0, -1))));
            var bytes = CombatSnapshotCodec.Encode(Snapshot(new CombatSnapshotPlayer(1, "", 0, 0, 0)));
            Array.Copy(BitConverter.GetBytes(-1), 0, bytes, bytes.Length - 4, 4); Fails(() => CombatSnapshotCodec.Decode(bytes));
        });
        Test("truncated deaths and trailing data rejected", () =>
        {
            var bytes = CombatSnapshotCodec.Encode(Snapshot(new CombatSnapshotPlayer(1, "", 0, 0, 0)));
            Fails(() => CombatSnapshotCodec.Decode(bytes.Take(bytes.Length - 1).ToArray()));
            Fails(() => CombatSnapshotCodec.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        });
        Test("v1 protocol is rejected without fallback", () =>
        {
            var bytes = CombatSnapshotCodec.Encode(CombatSnapshotBuilder.Empty(101, Epoch, 1)); bytes[0] = 1;
            Fails(() => CombatSnapshotCodec.Decode(bytes)); Eq((byte)2, CombatSnapshot.ProtocolVersion);
        });
        Test("packet limit remains enforced", () => Fails(() => CombatSnapshotCodec.Decode(new byte[CombatSnapshotCodec.MaxBytes + 1])));
        Test("largest permitted row count roundtrips new fields", () =>
        {
            var rows = Enumerable.Range(1, 64).Select(i => new CombatSnapshotPlayer(i, new string('Ж', 64), 100, 10, 0, 60, i)).ToArray();
            Eq(64, CombatSnapshotCodec.Decode(CombatSnapshotCodec.Encode(Snapshot(rows))).Players[63].Deaths);
        });
        Test("v2 adds exactly eight bytes per player row", () =>
        {
            int empty = CombatSnapshotCodec.Encode(CombatSnapshotBuilder.Empty(101, Epoch, 1)).Length;
            int one = CombatSnapshotCodec.Encode(Snapshot(new CombatSnapshotPlayer(1, "P", 100, 10, 0, 60, 1))).Length;
            Eq(51, empty); Eq(85, one); Eq(8, one - (51 + 8 + 2 + 4 + 8 + 4));
        });
        Test("metric text uses damage integer formatting and whole deaths", () =>
        {
            var row = CombatMeterPresenter.Build(Snapshot(new CombatSnapshotPlayer(1, "P", 100, 10, 0, 60.25f, 2))).Rows[0];
            Eq("60", row.LargestHitText); Eq("2", row.DeathsText);
        });
        Test("optional configs are local false and live", () =>
        {
            foreach (string key in new[] { "Show Largest Hit", "Show Deaths" })
            { var c = ConfigCatalog.Find("UI", key); Eq("false", c.DefaultValue); Eq(ConfigAuthority.Local, c.Authority); True(c.LiveUpdate); }
        });
        foreach (bool percent in new[] { false, true })
        foreach (bool largest in new[] { false, true })
        foreach (bool deaths in new[] { false, true })
        {
            Test("actual column slots and geometry " + percent + "/" + largest + "/" + deaths, () =>
            {
                foreach (float width in new[] { 350f, 480f, 800f })
                {
                    var columns = new CombatMeterColumns(width, percent, largest, deaths);
                    var order = Enumerable.Range(0, CombatMeterColumns.SlotCount).Select(CombatMeterColumns.At).Where(columns.Visible).ToArray();
                    if (largest) Eq(Array.IndexOf(order, MeterColumn.Dps) + 1, Array.IndexOf(order, MeterColumn.LargestHit));
                    if (deaths) Eq(order.Length - 1, Array.IndexOf(order, MeterColumn.Deaths));
                    Eq(largest, order.Contains(MeterColumn.LargestHit)); Eq(deaths, order.Contains(MeterColumn.Deaths)); Eq(percent, order.Contains(MeterColumn.Percent));
                    for (int i = 1; i < order.Length; i++) True(columns.X(order[i - 1]) + columns.CellWidth(order[i - 1]) <= columns.X(order[i]));
                    True(columns.X(order.Last()) + columns.CellWidth(order.Last()) <= columns.Width - 12f);
                    // Toggling away and back produces identical layout, with no persistent insertion state.
                    var other = new CombatMeterColumns(width, !percent, !largest, !deaths);
                    var again = new CombatMeterColumns(width, percent, largest, deaths);
                    foreach (var column in order) Eq(columns.X(column), again.X(column));
                }
            });
        }
        return _passed;
    }
    private static EncounterManager Manager() => new EncounterManager(new CombatStatisticsAggregator(), new EncounterSettings(20, 180));
    private static EncounterManager Active() { var e = Manager(); e.Accept(Done(1, 40), 0); return e; }
    private static PlayerCombatStatistics Stats(EncounterManager e, long id = 1) { True(e.Statistics.TryGet(id, out var s)); return s; }
    private static DamageCommit Done(long id, float damage, uint npc = 10) => new DamageCommit(new EventId(101, Epoch, ++_sequence),
        new DamageFacts(99, npc, false, null, AttackerClass.Player, id, 1, "NPC", "P"), damage, DateTime.UtcNow.Ticks);
    private static DamageCommit Taken(long id, float damage, byte hit) => new DamageCommit(new EventId(101, Epoch, ++_sequence),
        new DamageFacts(99, 1, true, id, AttackerClass.None, null, hit, "P", ""), damage, DateTime.UtcNow.Ticks);
    private static CombatSnapshot Snapshot(params CombatSnapshotPlayer[] rows) => new CombatSnapshot(101, Epoch, 1, 1, EncounterState.Active, 1, rows);
    private static void Fails(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException) { return; } throw new Exception("Expected malformed packet rejection"); }
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS optional metrics: " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Optional metric assertion failed"); }
}
