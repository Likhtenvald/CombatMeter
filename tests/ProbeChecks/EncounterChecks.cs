using System;
using System.Linq;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;

internal static class EncounterChecks
{
    private static readonly Guid Epoch = new Guid("20234567-89ab-cdef-0123-456789abcdef");
    private static long _sequence;
    private static int _passed;

    internal static int Run()
    {
        Test("encounter 01: first PvE event starts encounter", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 2); Eq(EncounterState.Active, m.State); Eq(2d, m.StartTime); });
        Test("encounter 02: second PvE event updates activity", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 2); m.Accept(Done(1, 2), 7); Eq(7d, m.LastActivityTime); });
        Test("encounter 03: PvP does not start encounter", () =>
        { var m = Manager(); True(!m.Accept(Pvp(1, 2, 5), 2)); Eq(EncounterState.NoEncounter, m.State); });
        Test("encounter 04: PvP does not prolong encounter", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 0); m.Accept(Pvp(1, 2, 5), 9); Eq(0d, m.LastActivityTime); m.Update(10); Eq(EncounterState.Finished, m.State); });
        Test("encounter 05: duplicate accepted pipeline cannot update encounter twice", () =>
        {
            var m = Manager(); double now = 2;
            var s = Session(m, () => now); DamageCommit c = Done(1, 5, source: 11);
            Eq(Acceptance.Accepted, s.ProcessDamageCommit(c, 11, CommitOrigin.Remote));
            now = 8; Eq(Acceptance.Duplicate, s.ProcessDamageCommit(c, 11, CommitOrigin.Remote));
            Eq(2d, m.LastActivityTime); Eq(5f, Stat(m, 1).DamageDone);
        });
        Test("encounter 06: validation rejection has no effect", () =>
        {
            var m = Manager(); var s = Session(m, () => 2); DamageCommit c = Done(1, float.NaN, source: 11);
            Eq(Acceptance.InvalidDamage, s.ProcessDamageCommit(c, 11, CommitOrigin.Remote)); Eq(EncounterState.NoEncounter, m.State);
        });
        Test("encounter 07: ordinary inactivity finishes active encounter", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 3); m.Update(13); Eq(EncounterState.Finished, m.State); });
        Test("encounter 08: finished encounter preserves statistics", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 3); m.Update(13); Eq(5f, Stat(m, 1).DamageDone); });
        Test("encounter 09: finished encounter preserves final duration", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 3); m.Update(99); Eq(10d, m.Duration(120)); Eq(13d, m.EndTime); });
        Test("encounter 10: next PvE event resets and starts a new encounter", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 3); m.Update(13); m.Accept(Done(2, 7), 20); Eq(EncounterState.Active, m.State); Eq(20d, m.StartTime); Eq(1, m.Statistics.Count); Eq(7f, Stat(m, 2).DamageDone); });
        Test("encounter 11: death with continuing combat remains active", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 0); m.OnPlayerDied(1, 2); m.Accept(Done(2, 4), 9); m.Update(15); Eq(EncounterState.Active, m.State); });
        Test("encounter 12: death does not clear player totals", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 0); m.OnPlayerDied(1, 2); Eq(5f, Stat(m, 1).DamageDone); });
        Test("encounter 13: returning player's damage continues totals", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 0); m.OnPlayerDied(1, 2); m.OnPlayerRespawned(1, 3); m.Accept(Done(1, 4), 20); Eq(9f, Stat(m, 1).DamageDone); Eq(0d, m.StartTime); });
        Test("encounter 14: death plus soft timeout enters recovery", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 0); m.OnPlayerDied(1, 2); m.Update(10); Eq(EncounterState.Recovery, m.State); });
        Test("encounter 15: party wipe allows recovery", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 0); m.Accept(Done(2, 5), 1); m.OnPlayerDied(1, 2); m.OnPlayerDied(2, 3); m.Update(11); Eq(EncounterState.Recovery, m.State); });
        Test("encounter 16: PvE event inside recovery returns to active", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 0); m.OnPlayerDied(1, 2); m.Update(10); m.Accept(Done(1, 2), 100); Eq(EncounterState.Active, m.State); Eq(100d, m.LastActivityTime); });
        Test("encounter 17: recovery resume preserves start and statistics", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 4); m.OnPlayerDied(1, 5); m.Update(14); m.Accept(Done(1, 2), 100); Eq(4d, m.StartTime); Eq(7f, Stat(m, 1).DamageDone); });
        Test("encounter 18: recovery time is part of duration", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 0); m.OnPlayerDied(1, 1); m.Update(10); Eq(100d, m.Duration(100)); });
        Test("encounter 19: respawn alone does not resume recovery", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 0); m.OnPlayerDied(1, 1); m.Update(10); m.OnPlayerRespawned(1, 20); Eq(EncounterState.Recovery, m.State); });
        Test("encounter 20: recovery timeout finishes encounter", () =>
        { var m = Manager(); m.Accept(Done(1, 5), 4); m.OnPlayerDied(1, 5); m.Update(14); Eq(EncounterState.Recovery, m.State); m.Update(185); Eq(EncounterState.Finished, m.State); Eq(185d, m.EndTime); });
        Test("encounter 21: players use independent active durations", () =>
        { var m = Manager(); m.Accept(Done(1, 10), 0); m.Accept(Done(2, 20), 5); Eq(6d, 10 / m.Dps(1, 10)); Eq(5d, 20 / m.Dps(2, 10)); });
        Test("encounter 22: DPS is damage divided by active duration", () =>
        { var m = Manager(); m.Accept(Done(1, 50), 10); Eq(50d / 6d, m.Dps(1, 20)); });
        Test("encounter 23: death and runback are excluded from DPS denominator", () =>
        { var m = Manager(); m.Accept(Done(1, 50), 0); m.OnPlayerDied(1, 2); m.Update(10); m.Accept(Done(1, 50), 100); Eq(6d, 100 / m.Dps(1, 100)); });
        Test("encounter 24: late joiner starts at zero active duration", () =>
        { var m = Manager(); m.Accept(Done(1, 10), 0); m.Accept(Done(1, 1), 9); m.Accept(Done(1, 1), 18); m.Accept(Done(2, 50), 20); Eq(0d, m.Dps(2, 20)); Eq(0d, m.StartTime); });
        Test("encounter 25: zero duration DPS is finite zero", () =>
        { var m = Manager(); m.Accept(Done(1, 10), 5); Eq(0d, m.Dps(1, 5)); True(!double.IsNaN(m.Dps(1, 5))); });
        Test("encounter 26: participant identity uses PlayerID", () =>
        { var m = Manager(); m.Accept(Done(1, 2, "Same"), 0); m.Accept(Done(2, 3, "Same"), 1); Eq(2, m.Participants.Count()); });
        Test("encounter 27: name change does not create participant", () =>
        { var m = Manager(); m.Accept(Done(1, 2, "Old"), 0); m.Accept(Done(1, 3, "New"), 1); Eq(1, m.Participants.Count()); Eq("New", Stat(m, 1).DisplayName); });
        Test("encounter 28: new encounter clears old totals", () =>
        { var m = Manager(); m.Accept(Done(1, 9), 0); m.Update(10); m.Accept(Done(2, 3), 20); True(!m.Statistics.TryGet(1, out _)); Eq(3f, Stat(m, 2).DamageDone); });
        Test("encounter 29: host session reset clears all state", () =>
        { var m = Manager(); m.Accept(Done(1, 9), 0); m.OnPlayerDied(1, 1); m.Reset(); Eq(EncounterState.NoEncounter, m.State); Eq(0, m.Statistics.Count); Eq(0, m.Participants.Count()); });
        Test("encounter 30: local and remote accepted commits share one chain", () =>
        {
            var m = Manager(); double now = 1; var s = Session(m, () => now);
            s.Observe(Facts(false, null, AttackerClass.Player, 1, "Troll", "Alice"), 2, DateTime.UtcNow.Ticks);
            now = 3; Eq(Acceptance.Accepted, s.ProcessDamageCommit(Taken(2, 4, source: 11), 11, CommitOrigin.Remote));
            Eq(2f, Stat(m, 1).DamageDone); Eq(4f, Stat(m, 2).DamageTaken); Eq(3d, m.LastActivityTime);
        });
        return _passed;
    }

    private static EncounterManager Manager() { _sequence = 0; return new EncounterManager(new CombatStatisticsAggregator(), new EncounterSettings(10, 180)); }
    private static CommitSession Session(EncounterManager manager, Func<double> now) =>
        new CommitSession(99, Epoch, true, _ => { }, (_, _, _) => { }, c => manager.Accept(c, now()));
    private static DamageCommit Done(long player, float damage, string name = "Player", long source = 99) =>
        Commit(false, null, AttackerClass.Player, player, damage, "Target", name, source);
    private static DamageCommit Taken(long player, float damage, long source = 99) =>
        Commit(true, player, AttackerClass.NPC, null, damage, "Player", "Troll", source);
    private static DamageCommit Pvp(long attacker, long victim, float damage) =>
        Commit(true, victim, AttackerClass.Player, attacker, damage, "Victim", "Attacker", 99);
    private static DamageCommit Commit(bool victimPlayer, long? victimId, AttackerClass attacker, long? attackerId,
        float loss, string victimName, string attackerName, long source) =>
        new DamageCommit(new EventId(source, Epoch, ++_sequence),
            Facts(victimPlayer, victimId, attacker, attackerId, victimName, attackerName), loss, DateTime.UtcNow.Ticks);
    private static DamageFacts Facts(bool victimPlayer, long? victimId, AttackerClass attacker, long? attackerId,
        string victimName, string attackerName) => new DamageFacts(111, 3, victimPlayer, victimId, attacker, attackerId, 1, victimName, attackerName);
    private static PlayerCombatStatistics Stat(EncounterManager m, long id)
    { if (!m.Statistics.TryGet(id, out PlayerCombatStatistics value)) throw new Exception("Missing player " + id); return value; }
    private static void Test(string name, Action run) { run(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
