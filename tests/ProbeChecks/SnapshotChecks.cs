using System;
using System.Collections.Generic;
using System.IO;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Snapshot;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;

internal static class SnapshotChecks
{
    private static readonly Guid Epoch = new Guid("60234567-89ab-cdef-0123-456789abcdef");
    private static long _event;
    private static int _passed;

    internal static int Run()
    {
        Test("snapshot 01: NoEncounter builds empty", () => { var s = Build(Manager(), 1, 5); Eq(0L, s.EncounterId); Eq(EncounterState.NoEncounter, s.EncounterState); Eq(0d, s.EncounterElapsedSeconds); Eq(0, s.Players.Count); });
        Test("snapshot 02: first encounter gets id one", () => { var m = Manager(); m.Accept(Done(1, 5), 2); Eq(1L, Build(m, 1, 3).EncounterId); });
        Test("snapshot 03: next encounter increments id", () => { var m = Manager(); m.Accept(Done(1, 5), 0); m.Update(10); m.Accept(Done(2, 2), 20); Eq(2L, Build(m, 1, 21).EncounterId); });
        Test("snapshot 04: Recovery preserves id", () => { var m = Recovering(); Eq(1L, Build(m, 1, 20).EncounterId); Eq(EncounterState.Recovery, Build(m, 2, 20).EncounterState); });
        Test("snapshot 05: Finished preserves id", () => { var m = Manager(); m.Accept(Done(1, 5), 0); m.Update(10); var s = Build(m, 1, 100); Eq(1L, s.EncounterId); Eq(EncounterState.Finished, s.EncounterState); });
        Test("snapshot 06: totals copied exactly", () => { var m = Manager(); m.Accept(Done(1, 7), 0); m.Accept(Taken(1, 3), 1); var r = Build(m, 1, 2).Players[0]; Eq(7f, r.DamageDone); Eq(3f, r.DamageTaken); });
        Test("snapshot 07: authoritative active-time DPS copied", () => { var m = Manager(); m.Accept(Done(1, 100), 0); Eq(100d / 6d, Build(m, 1, 10).Players[0].Dps); });
        Test("snapshot 08: Recovery elapsed continues while DPS freezes", () => { var m = Recovering(); var s = Build(m, 1, 20); Eq(20d, s.EncounterElapsedSeconds); Eq(5d / 6d, s.Players[0].Dps); });
        Test("snapshot 09: Finished elapsed and active-time DPS freeze", () => { var m = Manager(); m.Accept(Done(1, 100), 0); m.Update(10); var s = Build(m, 1, 999); Eq(10d, s.EncounterElapsedSeconds); Eq(100d / 6d, s.Players[0].Dps); });
        Test("snapshot 10: rows sorted by signed PlayerID", () => { var m = Manager(); m.Accept(Done(5, 1), 0); m.Accept(Done(-7, 1), 1); var s = Build(m, 1, 2); Eq(-7L, s.Players[0].PlayerId); Eq(5L, s.Players[1].PlayerId); });
        Test("snapshot 11: DisplayName preserved", () => { var m = Manager(); m.Accept(Done(1, 1, "Игрок"), 0); Eq("Игрок", Build(m, 1, 1).Players[0].DisplayName); });
        Test("snapshot 12: overlong host name is bounded", () => { var m = Manager(); m.Accept(Done(1, 1, new string('x', 100)), 0); Eq(64, Build(m, 1, 1).Players[0].DisplayName.Length); });
        Test("snapshot 13: zero PlayerID is not emitted", () => { var m = Manager(); m.Accept(Done(0, 1), 0); Eq(0, Build(m, 1, 1).Players.Count); });
        Test("snapshot 14: invalid host numeric cannot emit", () => { var m = Manager(); m.Accept(Done(1, float.NaN), 0); Fails(() => Build(m, 1, 1)); });
        Test("snapshot 15: roundtrip all states", () => { Roundtrip(Build(Manager(), 1, 0)); var a = Manager(); a.Accept(Done(-7, 4, "Юникод"), 0); Roundtrip(Build(a, 2, 3)); Roundtrip(Build(Recovering(), 3, 20)); a.Update(10); Roundtrip(Build(a, 4, 20)); });
        Test("snapshot 16: roundtrip multiple players and empty name", () => { var m = Manager(); m.Accept(Done(2, 1, ""), 0); m.Accept(Done(1, 2, "A"), 1); var s = Roundtrip(Build(m, 1, 2)); Eq(2, s.Players.Count); Eq("", s.Players[1].DisplayName); });
        Test("snapshot 17: max player boundary roundtrips", () => { var rows = new List<CombatSnapshotPlayer>(); for (int i = 1; i <= 64; i++) rows.Add(new CombatSnapshotPlayer(i, "", i, i, i)); Eq(64, Roundtrip(Snapshot(1, rows)).Players.Count); });
        Test("snapshot 18: first and newer accepted", () => { var store = new CombatSnapshotStore(); Eq(SnapshotApplyResult.Accepted, store.Apply(Snapshot(1), 101, 101)); Eq(SnapshotApplyResult.Accepted, store.Apply(Snapshot(2), 101, 101)); Eq(2L, Latest(store).Sequence); });
        Test("snapshot 19: duplicate and stale ignored", () => { var store = new CombatSnapshotStore(); store.Apply(Snapshot(20), 101, 101); Eq(SnapshotApplyResult.Duplicate, store.Apply(Snapshot(20), 101, 101)); Eq(SnapshotApplyResult.Stale, store.Apply(Snapshot(19), 101, 101)); Eq(20L, Latest(store).Sequence); });
        Test("snapshot 20: lost sequence is replaceable", () => { var store = new CombatSnapshotStore(); store.Apply(Snapshot(10), 101, 101); store.Apply(Snapshot(12), 101, 101); Eq(12L, Latest(store).Sequence); });
        Test("snapshot 21: wrong sender rejected", () => { var store = new CombatSnapshotStore(); Eq(SnapshotApplyResult.UnexpectedSender, store.Apply(Snapshot(1), 202, 101)); True(!store.TryGetLatest(out _)); });
        Test("snapshot 22: new epoch requires store reset", () => { var store = new CombatSnapshotStore(); store.Apply(Snapshot(1), 101, 101); var other = new CombatSnapshot(101, Guid.NewGuid(), 2, 0, EncounterState.NoEncounter, 0, Array.Empty<CombatSnapshotPlayer>()); Eq(SnapshotApplyResult.SessionMismatch, store.Apply(other, 101, 101)); store.Clear(); Eq(SnapshotApplyResult.Accepted, store.Apply(other, 101, 101)); });
        Test("snapshot 23: malformed values preserve prior store", () => { var store = new CombatSnapshotStore(); store.Apply(Snapshot(1), 101, 101); var bad = new CombatSnapshot(101, Epoch, 2, 1, EncounterState.Active, -1, Array.Empty<CombatSnapshotPlayer>()); Eq(SnapshotApplyResult.Invalid, store.Apply(bad, 101, 101)); Eq(1L, Latest(store).Sequence); });
        Test("snapshot 24: codec rejects wrong protocol and truncation", () => { byte[] bytes = CombatSnapshotCodec.Encode(Snapshot(1)); bytes[0] = 99; FailsDecode(bytes); Array.Resize(ref bytes, 5); FailsDecode(bytes); });
        Test("snapshot 25: codec rejects malformed state and elapsed", () => { byte[] state = CombatSnapshotCodec.Encode(Snapshot(1)); state[41] = 99; FailsDecode(state); var bad = new CombatSnapshot(101, Epoch, 1, 1, EncounterState.Active, double.NaN, Array.Empty<CombatSnapshotPlayer>()); Fails(() => CombatSnapshotCodec.Encode(bad)); });
        Test("snapshot 26: duplicate and zero rows rejected", () => { var duplicate = new[] { Row(1), Row(1) }; Fails(() => CombatSnapshotCodec.Encode(Snapshot(1, duplicate))); Fails(() => CombatSnapshotCodec.Encode(Snapshot(1, new[] { Row(0) }))); });
        Test("snapshot 27: overlong name and too many rows rejected", () => { Fails(() => CombatSnapshotCodec.Encode(Snapshot(1, new[] { new CombatSnapshotPlayer(1, new string('x', 65), 0, 0, 0) }))); var rows = new List<CombatSnapshotPlayer>(); for (int i = 1; i <= 65; i++) rows.Add(Row(i)); Fails(() => CombatSnapshotCodec.Encode(Snapshot(1, rows))); });
        Test("snapshot 28: invalid player numbers rejected", () => { Fails(() => CombatSnapshotCodec.Encode(Snapshot(1, new[] { new CombatSnapshotPlayer(1, "", float.PositiveInfinity, 0, 0) }))); Fails(() => CombatSnapshotCodec.Encode(Snapshot(1, new[] { new CombatSnapshotPlayer(1, "", 0, double.NaN, -1) }))); });
        Test("snapshot 29: host/client serialized parity", () => { var m = Manager(); m.Accept(Done(-7, 20, "P"), 0); m.Accept(Taken(-7, 3), 2); CombatSnapshot host = Build(m, 7, 4); CombatSnapshot client = Roundtrip(host); Same(host, client); });
        Test("snapshot 30: new encounter contains reset totals", () => { var m = Manager(); m.Accept(Done(1, 500), 0); m.Update(10); m.Accept(Done(1, 20), 20); var s = Build(m, 1, 21); Eq(2L, s.EncounterId); Eq(20f, s.Players[0].DamageDone); });
        return _passed;
    }

    private static EncounterManager Manager() { _event = 0; return new EncounterManager(new CombatStatisticsAggregator(), new EncounterSettings(10, 180)); }
    private static EncounterManager Recovering() { var m = Manager(); m.Accept(Done(1, 5), 0); m.OnPlayerDied(1, 2); m.Update(10); return m; }
    private static CombatSnapshot Build(EncounterManager m, long sequence, double now) => CombatSnapshotBuilder.Build(101, Epoch, sequence, m, now);
    private static CombatSnapshot Snapshot(long sequence, IReadOnlyList<CombatSnapshotPlayer> rows = null) => new CombatSnapshot(101, Epoch, sequence, rows == null ? 0 : 1, rows == null ? EncounterState.NoEncounter : EncounterState.Active, rows == null ? 0 : 1, rows);
    private static CombatSnapshotPlayer Row(long id) => new CombatSnapshotPlayer(id, "", 0, 0, 0);
    private static DamageCommit Done(long player, float damage, string name = "Player") => Commit(false, null, AttackerClass.Player, player, damage, name);
    private static DamageCommit Taken(long player, float damage) => Commit(true, player, AttackerClass.NPC, null, damage, "Player");
    private static DamageCommit Commit(bool victimPlayer, long? victim, AttackerClass attacker, long? attackerPlayer, float damage, string name) => new DamageCommit(new EventId(99, Epoch, ++_event), new DamageFacts(1, 1, victimPlayer, victim, attacker, attackerPlayer, 1, name, name), damage, DateTime.UtcNow.Ticks);
    private static CombatSnapshot Roundtrip(CombatSnapshot s) { var value = CombatSnapshotCodec.Decode(CombatSnapshotCodec.Encode(s)); Same(s, value); return value; }
    private static CombatSnapshot Latest(CombatSnapshotStore store) { True(store.TryGetLatest(out CombatSnapshot s)); return s; }
    private static void Same(CombatSnapshot a, CombatSnapshot b) { Eq(a.HostPeerSessionId, b.HostPeerSessionId); Eq(a.SnapshotEpoch, b.SnapshotEpoch); Eq(a.Sequence, b.Sequence); Eq(a.EncounterId, b.EncounterId); Eq(a.EncounterState, b.EncounterState); Eq(a.EncounterElapsedSeconds, b.EncounterElapsedSeconds); Eq(a.Players.Count, b.Players.Count); for (int i = 0; i < a.Players.Count; i++) { Eq(a.Players[i].PlayerId, b.Players[i].PlayerId); Eq(a.Players[i].DisplayName, b.Players[i].DisplayName); Eq(a.Players[i].DamageDone, b.Players[i].DamageDone); Eq(a.Players[i].Dps, b.Players[i].Dps); Eq(a.Players[i].DamageTaken, b.Players[i].DamageTaken); } }
    private static void FailsDecode(byte[] bytes) => Fails(() => CombatSnapshotCodec.Decode(bytes));
    private static void Fails(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is ArgumentException || e is InvalidOperationException) { return; } throw new Exception("Expected failure"); }
    private static void Test(string name, Action run) { run(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
