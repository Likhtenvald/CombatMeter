using System;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;

internal static class RecoveryTicketChecks
{
    private static readonly Guid Epoch = new Guid("50234567-89ab-cdef-0123-456789abcdef");
    private static long _sequence;
    private static int _passed;

    internal static int Run()
    {
        Test("recovery ticket 01: death creates ticket", () => { var m = Active(1); Eq(PlayerDeathResult.Committed, m.OnPlayerDied(1, 5, "1:1")); Ticket(m, 1, 5, 185); });
        Test("recovery ticket 02: duplicate incarnation does not duplicate ticket", () => { var m = Active(1); m.OnPlayerDied(1, 5, "1:1"); Eq(PlayerDeathResult.Duplicate, m.OnPlayerDied(1, 6, "1:1")); Eq(1, m.RecoveryTicketCount); Ticket(m, 1, 5, 185); });
        Test("recovery ticket 03: later death refreshes ticket", () => { var m = Active(1); m.OnPlayerDied(1, 5, "1:1"); m.OnPlayerRespawned(1, 6); Eq(PlayerDeathResult.Committed, m.OnPlayerDied(1, 20, "1:2")); Ticket(m, 1, 20, 200); });
        Test("recovery ticket 04: respawn leaves ticket pending", () => { var m = Active(1); m.OnPlayerDied(1, 5); m.OnPlayerRespawned(1, 6); Eq(1, m.RecoveryTicketCount); });
        Test("recovery ticket 05: own DamageDone clears ticket", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Accept(Done(1), 3); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ticket 06: own DamageTaken clears ticket", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Accept(Taken(1, AttackerClass.NPC), 3); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ticket 07: environment DamageTaken clears ticket", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Accept(Taken(1, AttackerClass.None), 3); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ticket 08: another player's activity preserves ticket", () => { var m = Active(1, 2); m.OnPlayerDied(1, 2); m.Accept(Done(2), 3); Eq(1, m.RecoveryTicketCount); True(m.TryGetRecoveryTicket(1, out _)); });
        Test("recovery ticket 09: soft timeout without ticket finishes", () => { var m = Active(1); m.Update(10); Eq(EncounterState.Finished, m.State); Eq(10d, m.EndTime); });
        Test("recovery ticket 10: soft timeout with ticket enters recovery", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Update(10); Eq(EncounterState.Recovery, m.State); });
        Test("recovery ticket 11: expired ticket cannot cause recovery", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Accept(Done(2), 181); m.Update(182); True(!m.TryGetRecoveryTicket(1, out _)); m.Update(191); Eq(EncounterState.Finished, m.State); });
        Test("recovery ticket 12: recovery expiry exact lower boundary", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Update(10); m.Update(181.999); Eq(EncounterState.Recovery, m.State); True(m.TryGetRecoveryTicket(1, out _)); });
        Test("recovery ticket 13: recovery ends at exact expiry", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Update(10); m.Update(182); Eq(EncounterState.Finished, m.State); Eq(182d, m.EndTime); });
        Test("recovery ticket 14: multiple tickets use last deadline", () => { var m = Active(1, 2); m.OnPlayerDied(1, 2, "1:1"); m.Accept(Done(2), 29); m.OnPlayerDied(2, 30, "2:1"); m.Update(39); m.Update(190); Eq(EncounterState.Recovery, m.State); Eq(1, m.RecoveryTicketCount); m.Update(210); Eq(EncounterState.Finished, m.State); Eq(210d, m.EndTime); });
        Test("recovery ticket 15: one reengages while other remains", () => { var m = Active(1, 2); m.OnPlayerDied(1, 2); m.OnPlayerDied(2, 3); m.Update(10); m.Accept(Done(1), 20); True(!m.TryGetRecoveryTicket(1, out _)); True(m.TryGetRecoveryTicket(2, out _)); });
        Test("recovery ticket 16: relevant event resumes recovery", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Update(10); m.Accept(Done(1), 20); Eq(EncounterState.Active, m.State); Eq(20d, m.LastActivityTime); });
        Test("recovery ticket 17: remaining other ticket returns active to recovery", () => { var m = Active(1, 2); m.OnPlayerDied(1, 2); m.OnPlayerDied(2, 3); m.Update(10); m.Accept(Done(1), 20); m.Update(30); Eq(EncounterState.Recovery, m.State); });
        Test("recovery ticket 18: no remaining ticket finishes after reactivation", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Update(10); m.Accept(Done(1), 20); m.Update(30); Eq(EncounterState.Finished, m.State); });
        Test("recovery ticket 19: negative PlayerID works", () => { var m = Active(-451055642); m.OnPlayerDied(-451055642, 2); True(m.TryGetRecoveryTicket(-451055642, out _)); m.Accept(Done(-451055642), 3); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ticket 20: zero PlayerID death is ignored", () => { var m = Active(1); Eq(PlayerDeathResult.NoActiveParticipant, m.OnPlayerDied(0, 2)); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ticket 21: new encounter has no stale tickets", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Update(182); m.Accept(Done(2), 200); Eq(0, m.RecoveryTicketCount); Eq(200d, m.StartTime); });
        Test("recovery ticket 22: reset clears tickets", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Reset(); Eq(0, m.RecoveryTicketCount); Eq(EncounterState.NoEncounter, m.State); });
        Test("recovery ticket 23: finished state clears tickets", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Update(182); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ticket 24: duration spans recovery", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Update(10); m.Accept(Done(1, 50), 100); Eq(100d, m.Duration(100)); });
        Test("recovery ticket 25: death changes no totals and does not finish", () => { var m = Active(1); float before = Stat(m, 1).DamageDone; m.OnPlayerDied(1, 2); Eq(before, Stat(m, 1).DamageDone); Eq(0f, Stat(m, 1).DamageTaken); Eq(EncounterState.Active, m.State); });
        Test("recovery ticket 26: soft timeout exact lower boundary", () => { var m = Active(1); m.Update(9.999); Eq(EncounterState.Active, m.State); m.Update(10); Eq(EncounterState.Finished, m.State); });
        Test("recovery ticket 27: stale death cannot cause later recovery", () => { var m = Active(1); m.OnPlayerDied(1, 2); m.Accept(Done(1), 20); m.Update(30); Eq(EncounterState.Finished, m.State); });
        Test("recovery ordering 28: pre-death event cannot clear ticket", () => { var m = DeathAt(1, 100.01); m.Accept(Taken(1, AttackerClass.NPC), 100.02, 99); True(m.TryGetRecoveryTicket(1, out _)); });
        Test("recovery ordering 29: killing blow cannot clear ticket", () => { var m = DeathAt(1, 100.01); m.Accept(Taken(1, AttackerClass.NPC), 100.02, 100); True(m.TryGetRecoveryTicket(1, out _)); });
        Test("recovery ordering 30: equal timestamp cannot clear ticket", () => { var m = DeathAt(1, 100.01); m.Accept(Taken(1, AttackerClass.NPC), 100.02, 100.01); True(m.TryGetRecoveryTicket(1, out _)); });
        Test("recovery ordering 31: post-death event clears ticket", () => { var m = DeathAt(1, 100.01); m.Accept(Taken(1, AttackerClass.NPC), 130, 129); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ordering 32: queued pre-death events all preserve ticket", () => { var m = DeathAt(1, 100.01); m.Accept(Taken(1, AttackerClass.NPC), 100.02, 98); m.Accept(Taken(1, AttackerClass.NPC), 100.03, 99); m.Accept(Taken(1, AttackerClass.NPC), 100.04, 100); True(m.TryGetRecoveryTicket(1, out _)); });
        Test("recovery ordering 33: post-respawn DamageTaken clears", () => { var m = DeathAt(1, 100); m.OnPlayerRespawned(1, 120); m.Accept(Taken(1, AttackerClass.NPC), 130, 130); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ordering 34: post-respawn DamageDone clears", () => { var m = DeathAt(1, 100); m.OnPlayerRespawned(1, 120); m.Accept(Done(1), 135, 135); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ordering 35: post-death environment damage clears", () => { var m = DeathAt(1, 100); m.OnPlayerRespawned(1, 120); m.Accept(Taken(1, AttackerClass.None), 130, 130); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ordering 36: other player and old own event preserve ticket", () => { var m = DeathAt(1, 100); m.Accept(Done(2), 105, 105); m.Accept(Taken(1, AttackerClass.NPC), 106, 99); True(m.TryGetRecoveryTicket(1, out _)); });
        Test("recovery ordering 37: second death rejects late first-life event", () => { var m = DeathAt(1, 100); m.OnPlayerRespawned(1, 120); m.Accept(Done(1), 140, 140); for (double t = 149; t < 200; t += 9) m.Accept(Done(2), t, t); m.OnPlayerDied(1, 200, "1:2"); m.Accept(Done(1), 200.01, 150); Ticket(m, 1, 200, 380); });
        Test("recovery ordering 38: negative PlayerID uses same guard", () => { var m = DeathAt(-451055642, 100); m.Accept(Taken(-451055642, AttackerClass.NPC), 101, 99); True(m.TryGetRecoveryTicket(-451055642, out _)); m.Accept(Taken(-451055642, AttackerClass.NPC), 130, 130); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ordering 39: retained ticket reaches Recovery", () => { var m = DeathAt(1, 100.01); m.Accept(Taken(1, AttackerClass.NPC), 101, 100); m.Update(110); Eq(EncounterState.Recovery, m.State); });
        Test("recovery ordering 40: real event resumes Recovery", () => { var m = DeathAt(1, 100.01); m.Update(110); m.Accept(Taken(1, AttackerClass.NPC), 130, 130); Eq(EncounterState.Active, m.State); Eq(0, m.RecoveryTicketCount); });
        Test("recovery ordering 41: soft timeout after guarded clear finishes", () => { var m = DeathAt(1, 100.01); m.Update(110); m.Accept(Done(1), 130, 130); m.Update(140); Eq(EncounterState.Finished, m.State); });
        return _passed;
    }

    private static EncounterManager Active(params long[] players)
    {
        _sequence = 0; var m = new EncounterManager(new CombatStatisticsAggregator(), new EncounterSettings(10, 180));
        foreach (long player in players) m.Accept(Done(player), 0);
        return m;
    }
    private static EncounterManager DeathAt(long player, double deathTime)
    {
        var m = Active(player, 2);
        for (double t = 9; t < deathTime - .01; t += 9) m.Accept(Done(2), t, t);
        m.Accept(Done(2), deathTime - .01, deathTime - .01);
        Eq(PlayerDeathResult.Committed, m.OnPlayerDied(player, deathTime, player + ":death")); return m;
    }
    private static DamageCommit Done(long player, float damage = 1) => Commit(false, null, AttackerClass.Player, player, damage);
    private static DamageCommit Taken(long player, AttackerClass attacker) => Commit(true, player, attacker, null, 1);
    private static DamageCommit Commit(bool victimPlayer, long? victim, AttackerClass attacker, long? attackerPlayer, float damage) =>
        new DamageCommit(new EventId(99, Epoch, ++_sequence),
            new DamageFacts(10, 1, victimPlayer, victim, attacker, attackerPlayer, 1, "Player", "Source"), damage, DateTime.UtcNow.Ticks);
    private static void Ticket(EncounterManager m, long player, double death, double expiry)
    { True(m.TryGetRecoveryTicket(player, out RecoveryTicket t)); Eq(death, t.DeathTime); Eq(expiry, t.ExpiryTime); }
    private static PlayerCombatStatistics Stat(EncounterManager m, long id) { True(m.Statistics.TryGet(id, out PlayerCombatStatistics p)); return p; }
    private static void Test(string name, Action run) { run(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
