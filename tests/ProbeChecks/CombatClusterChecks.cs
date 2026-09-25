using System;
using System.Collections.Generic;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;
using DiagnosticDamageProbe.Attribution;

internal static class CombatClusterChecks
{
    private static int _passed;
    private static long _sequence;
    private static readonly Guid Epoch = new Guid("60234567-89ab-cdef-0123-456789abcdef");
    internal static int Run()
    {
        Test("first PvE interaction creates one cluster", () =>
        { var m = New(); var c = m.Accept(Done(1, 10), 1); Eq(1, m.ActiveCount); Eq(2, c.MemberCount); Eq(EncounterState.Active, c.Encounter.State); Eq(1d, c.Encounter.StartTime); });
        Test("independent interactions create independent clusters", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 1); var b = m.Accept(Done(2, 20), 2); Eq(2, m.ActiveCount); True(a.Id != b.Id); });
        Test("shared combatant joins players", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 1); Eq(a, m.Accept(Done(2, 10), 2)); Eq(1, m.ActiveCount); Eq(3, a.MemberCount); });
        Test("existing player joins another combatant", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 1); Eq(a, m.Accept(Done(1, 20), 2)); True(a.Contains(Npc(20))); Eq(3, a.MemberCount); });
        Test("incoming NPC damage connects the same combatant", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 1); Eq(a, m.Accept(Taken(2, 10), 2)); Eq(1, m.ActiveCount); Eq(3f, Stats(a, 2).DamageTaken); });
        Test("bridge merges two components with stable survivor", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 1); var b = m.Accept(Done(2, 20), 2); Eq(a, m.Accept(Done(2, 10), 3)); Eq(1, m.ActiveCount); Eq(a.Id, b.MergedIntoId.Value); True(!m.TryGet(b.Id, out _)); });
        Test("merge preserves totals and counts bridge exactly once", () =>
        { var m = New(); var a = m.Accept(Done(1, 10, 5), 1); m.Accept(Taken(1, 10, 2), 2); m.Accept(Done(2, 20, 7), 3); m.Accept(Taken(2, 20, 4), 4); m.Accept(Done(1, 20, 11), 5); Eq(16f, Stats(a, 1).DamageDone); Eq(2f, Stats(a, 1).DamageTaken); Eq(7f, Stats(a, 2).DamageDone); Eq(4f, Stats(a, 2).DamageTaken); });
        Test("merge keeps earliest start and bridge activity", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 2); m.Accept(Done(2, 20), 5); m.Accept(Done(2, 10), 9); Eq(2d, a.Encounter.StartTime); Eq(9d, a.Encounter.LastActivityTime); Eq(7d, a.Encounter.Duration(9)); });
        Test("every membership is remapped after merge", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 1); m.Accept(Done(2, 20), 2); m.Accept(Done(1, 20), 3); foreach (var n in new[] { Player(1), Player(2), Npc(10), Npc(20) }) { True(m.TryGetMembership(n, out var c)); Eq(a, c); } Eq(4, a.MemberCount); });
        Test("death and Recovery are isolated", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 0); m.OnPlayerDied(1, 1, "one"); var b = m.Accept(Done(2, 20), 10); m.Update(20); Eq(EncounterState.Recovery, a.Encounter.State); Eq(EncounterState.Active, b.Encounter.State); Eq(0, b.Encounter.RecoveryTicketCount); Eq(10d, b.Encounter.LastActivityTime); });
        Test("soft timeout finishes only inactive cluster", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 0); var b = m.Accept(Done(2, 20, 7), 10); m.Update(20); Eq(EncounterState.Finished, a.Encounter.State); Eq(1, m.ActiveCount); Eq(EncounterState.Active, b.Encounter.State); Eq(7f, Stats(b, 2).DamageDone); Eq(a, m.LastFinished); });
        Test("finished membership is released and next fight is clean", () =>
        { var m = New(); var a = m.Accept(Done(1, 10, 9), 0); m.Update(20); True(!m.TryGetMembership(Player(1), out _)); True(!m.TryGetMembership(Npc(10), out _)); var b = m.Accept(Done(1, 10, 2), 21); True(a.Id != b.Id); Eq(2f, Stats(b, 1).DamageDone); Eq(9f, Stats(a, 1).DamageDone); });
        Test("ignored PvP and NPC damage create no clusters", () =>
        { var m = New(); Eq<CombatCluster>(null, m.Accept(Commit(true, 1, 10, AttackerClass.Player, 2, 20, 3), 0)); Eq<CombatCluster>(null, m.Accept(Commit(false, null, 10, AttackerClass.NPC, null, 20, 3), 0)); Eq(0, m.ActiveCount); });
        Test("merged component never splits when old combatant goes idle", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 0); m.Accept(Done(2, 20), 1); m.Accept(Done(1, 20), 2); for (int t = 10; t <= 100; t += 10) { m.Accept(Done(2, 20), t); m.Accept(Done(1, 20), t); m.Update(t); } Eq(1, m.ActiveCount); True(a.Contains(Npc(10))); Eq(4, a.MemberCount); });
        Test("environment damage creates player-only cluster with statistics", () =>
        { var m = New(); var a = m.Accept(Taken(1, 0, 4), 0); Eq(1, a.MemberCount); True(a.Contains(Player(1))); Eq(4f, Stats(a, 1).DamageTaken); Eq(0f, Stats(a, 1).DamageDone); });
        Test("unattributed poison creates player-only cluster", () =>
        { var m = New(); var a = m.Accept(Commit(true, 1, 100, AttackerClass.Unresolved, null, 0, 2, 1), 0); Eq(1, a.MemberCount); Eq(2f, Stats(a, 1).DamageTaken); });
        Test("repeat unattributed damage reuses cluster and refreshes activity", () =>
        { var m = New(); var a = m.Accept(Taken(1, 0, 4), 0); Eq(a, m.Accept(Taken(1, 0, 2), 15)); Eq(6f, Stats(a, 1).DamageTaken); Eq(15d, a.Encounter.LastActivityTime); m.Update(20); Eq(1, m.ActiveCount); });
        Test("normal PvE joins player-only without losing Damage Taken", () =>
        { var m = New(); var a = m.Accept(Taken(1, 0, 4), 0); Eq(a, m.Accept(Done(1, 10, 7), 1)); Eq(2, a.MemberCount); Eq(4f, Stats(a, 1).DamageTaken); Eq(7f, Stats(a, 1).DamageDone); });
        Test("player-only merges with existing component", () =>
        { var m = New(); var a = m.Accept(Taken(1, 0, 4), 0); m.Accept(Done(2, 20), 1); Eq(a, m.Accept(Done(1, 20), 2)); Eq(1, m.ActiveCount); Eq(3, a.MemberCount); Eq(4f, Stats(a, 1).DamageTaken); });
        Test("player-only finishes at existing soft timeout boundary", () =>
        { var m = New(); var a = m.Accept(Taken(1, 0), 0); m.Update(19.99); Eq(1, m.ActiveCount); m.Update(20); Eq(0, m.ActiveCount); Eq(20d, a.Encounter.EndTime); });
        Test("player-only Recovery and reengagement retain semantics", () =>
        { var m = New(); var a = m.Accept(Taken(1, 0), 0); m.OnPlayerDied(1, 1); m.Update(20); Eq(EncounterState.Recovery, a.Encounter.State); m.Accept(Taken(1, 0), 21); Eq(EncounterState.Active, a.Encounter.State); Eq(0, a.Encounter.RecoveryTicketCount); });
        Test("Recovery finishes independently on ticket expiry", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 0); m.OnPlayerDied(1, 1); m.Update(20); var b = m.Accept(Done(2, 20), 175); m.Update(181); Eq(EncounterState.Finished, a.Encounter.State); Eq(181d, a.Encounter.EndTime); Eq(EncounterState.Active, b.Encounter.State); Eq(1, m.ActiveCount); });
        Test("merge transfers pending recovery tickets and death dedup", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 0); var b = m.Accept(Done(2, 20), 1); m.OnPlayerDied(2, 2, "two"); b.Encounter.TryGetRecoveryTicket(2, out var ticket); m.Accept(Done(1, 20), 3); True(a.Encounter.TryGetRecoveryTicket(2, out var moved)); True(ReferenceEquals(ticket, moved)); Eq(182d, moved.ExpiryTime); Eq(PlayerDeathResult.Duplicate, m.OnPlayerDied(2, 4, "two")); });
        Test("merge Active and Recovery retains unrelated ticket", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 0); m.OnPlayerDied(1, 1); m.Update(20); m.Accept(Done(2, 20), 21); m.Accept(Done(2, 10), 22); Eq(EncounterState.Active, a.Encounter.State); True(a.Encounter.TryGetRecoveryTicket(1, out var ticket)); Eq(181d, ticket.ExpiryTime); Eq(22d, a.Encounter.LastActivityTime); });
        Test("merge two Recovery clusters clears only reengaging ticket", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 0); m.Accept(Done(2, 20), 1); m.OnPlayerDied(1, 2); m.OnPlayerDied(2, 3); m.Update(21); Eq(EncounterState.Recovery, a.Encounter.State); m.Accept(Done(1, 20), 22); Eq(EncounterState.Active, a.Encounter.State); True(!a.Encounter.TryGetRecoveryTicket(1, out _)); True(a.Encounter.TryGetRecoveryTicket(2, out var t)); Eq(183d, t.ExpiryTime); });
        Test("delayed pre-death bridge does not clear Recovery tickets", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 0); m.Accept(Done(2, 20), 1); m.OnPlayerDied(1, 2); m.OnPlayerDied(2, 3); m.Update(21); m.Accept(Done(1, 20), 22, 1); Eq(1, m.ActiveCount); Eq(EncounterState.Recovery, a.Encounter.State); Eq(2, a.Encounter.RecoveryTicketCount); Eq(6f, Stats(a, 1).DamageDone); });
        Test("merge preserves both players DPS intervals", () =>
        { var m = New(); var a = m.Accept(Done(1, 10, 12), 0); var b = m.Accept(Done(2, 20, 18), 1); double before = b.Encounter.ActiveDpsTime(2, 3); m.Accept(Taken(1, 20, 2), 3); Near(before, a.Encounter.ActiveDpsTime(2, 3)); Near(3, a.Encounter.ActiveDpsTime(1, 3)); Near(9, a.Encounter.Dps(2, 3)); });
        Test("merge preserves frozen Recovery DPS without artificial activity", () =>
        { var m = New(); var a = m.Accept(Done(1, 10, 12), 0); m.OnPlayerDied(1, 1); m.Update(20); double frozen = a.Encounter.ActiveDpsTime(1, 20); m.Accept(Done(2, 20), 21); m.Accept(Done(2, 10), 22); Near(frozen, a.Encounter.ActiveDpsTime(1, 23)); });
        Test("attributed multi-player bridge merges three clusters once", () =>
        { var m = New(); var a = m.Accept(Done(1, 10, 5), 0); m.Accept(Done(2, 20, 7), 1); m.Accept(Done(3, 30, 9), 2); var damage = new AttributedDamageEvent(Commit(false, null, 30, AttackerClass.None, null, 0, 12), new List<DamagePortion> { new DamagePortion(1, 4), new DamagePortion(2, 6), new DamagePortion(null, 2) }, false); Eq(a, m.Accept(damage, 3)); Eq(1, m.ActiveCount); Eq(9f, Stats(a, 1).DamageDone); Eq(13f, Stats(a, 2).DamageDone); Eq(9f, Stats(a, 3).DamageDone); Eq(6, a.MemberCount); });
        Test("attributed PvP and wholly unattributed portions are ignored", () =>
        { var m = New(); Eq<CombatCluster>(null, m.Accept(new AttributedDamageEvent(Taken(1, 10), new List<DamagePortion> { new DamagePortion(2, 3) }, false), 0)); Eq<CombatCluster>(null, m.Accept(new AttributedDamageEvent(Done(1, 10), new List<DamagePortion> { new DamagePortion(null, 3) }, false), 0)); Eq(0, m.ActiveCount); });
        Test("player and combatant ID namespaces cannot collide", () =>
        { var m = New(); var a = m.Accept(Done(99, 10), 0); var b = m.Accept(Done(-1, 99), 1); Eq(2, m.ActiveCount); True(!ReferenceEquals(a, b)); });
        Test("combatant creator is part of identity", () =>
        { var m = New(); m.Accept(Done(1, 10), 0); var facts = new DamageFacts(100, 10, false, null, AttackerClass.Player, 2, 1, "NPC", "P2"); m.Accept(new DamageCommit(new EventId(1, Epoch, ++_sequence), facts, 3, 1), 1); Eq(2, m.ActiveCount); });
        Test("respawn metadata alone cannot resume Recovery", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 0); m.OnPlayerDied(1, 1); m.Update(20); m.OnPlayerRespawned(1, 21); Eq(EncounterState.Recovery, a.Encounter.State); True(a.Encounter.TryGetParticipant(1, out var p) && p.IsAlive); Eq(1, a.Encounter.RecoveryTicketCount); });
        Test("settings changes apply independently to existing clusters", () =>
        { var m = New(); var a = m.Accept(Done(1, 10), 0); var b = m.Accept(Done(2, 20), 8); m.Settings.Update(10, 30, 3); m.Update(10); Eq(EncounterState.Finished, a.Encounter.State); Eq(EncounterState.Active, b.Encounter.State); });
        Test("session reset clears active and retained finished state", () =>
        { var m = New(); m.Accept(Done(1, 10), 0); m.Update(20); m.Accept(Done(2, 20), 21); m.Reset(); Eq(0, m.ActiveCount); Eq<CombatCluster>(null, m.LastFinished); True(!m.TryGetMembership(Player(2), out _)); });
        Test("finished history is bounded to most recent encounter", () =>
        { var m = New(); for (int i = 0; i < 100; i++) { var a = m.Accept(Done(1, 10), i * 21); m.Update(i * 21 + 20); Eq(a, m.LastFinished); Eq(0, m.ActiveCount); } });
        Test("invalid time cannot create partial membership", () =>
        { var m = New(); try { m.Accept(Done(1, 10), 1, double.NaN); throw new Exception("Expected time rejection"); } catch (ArgumentOutOfRangeException) { } Eq(0, m.ActiveCount); });
        Test("merge retains compacted DPS history exactly", () =>
        {
            var m = New(); var a = m.Accept(Done(1, 10), 0); var b = m.Accept(Done(2, 20), 0);
            for (int i = 1; i <= 140; i++) { m.Accept(Done(1, 10), i * 7); m.Accept(Done(2, 20), i * 7); }
            double before = b.Encounter.ActiveDpsTime(2, 981);
            m.Accept(Taken(1, 20), 981);
            Near(before, a.Encounter.ActiveDpsTime(2, 981)); Eq(423f, Stats(a, 2).DamageDone);
            Near(Stats(a, 2).DamageDone / before, a.Encounter.Dps(2, 981));
        });
        Test("single component matches legacy lifecycle and DPS throughout replay", () =>
        {
            var m = New(); var legacy = new EncounterManager(new CombatStatisticsAggregator()); CombatCluster cluster = null;
            void Compare(double now)
            {
                Eq(legacy.State, cluster.Encounter.State); Near(legacy.StartTime, cluster.Encounter.StartTime);
                Near(legacy.LastActivityTime, cluster.Encounter.LastActivityTime); Near(legacy.EndTime, cluster.Encounter.EndTime);
                Eq(legacy.RecoveryTicketCount, cluster.Encounter.RecoveryTicketCount);
                True(legacy.Statistics.TryGet(1, out var original)); var actual = Stats(cluster, 1);
                Eq(original.DamageDone, actual.DamageDone); Eq(original.DamageTaken, actual.DamageTaken);
                Near(legacy.ActiveDpsTime(1, now), cluster.Encounter.ActiveDpsTime(1, now)); Near(legacy.Dps(1, now), cluster.Encounter.Dps(1, now));
            }
            void Hit(DamageCommit damage, double now, double? eventTime = null)
            { legacy.Accept(damage, now, eventTime); cluster = m.Accept(damage, now, eventTime); Compare(now); }
            void Tick(double now) { legacy.Update(now); m.Update(now); Compare(now); }
            Hit(Done(1, 10), 0); Hit(Taken(1, 0), 4);
            legacy.OnPlayerDied(1, 5); m.OnPlayerDied(1, 5); Tick(24);
            Hit(Done(1, 10), 25, 4);
            legacy.OnPlayerRespawned(1, 26); m.OnPlayerRespawned(1, 26); Compare(26);
            Hit(Done(1, 10), 27); Tick(47); Hit(Done(1, 10), 48);
        });
        Test("two player-only clusters stay separate until a real bridge", () =>
        {
            var m = New(); var a = m.Accept(Taken(long.MinValue, 0), 0); var b = m.Accept(Taken(-1, 0), 1);
            Eq(2, m.ActiveCount); Eq(1, a.MemberCount); Eq(1, b.MemberCount);
            m.Accept(Done(long.MinValue, 10), 2); m.Accept(Done(-1, 10), 3);
            Eq(1, m.ActiveCount); Eq(3, a.MemberCount); Eq(3f, Stats(a, long.MinValue).DamageTaken); Eq(3f, Stats(a, -1).DamageTaken);
        });
        return _passed;
    }
    private static CombatClusterManager New() => new CombatClusterManager();
    private static CombatNode Player(long id) => CombatNode.Player(id);
    private static CombatNode Npc(uint id) => CombatNode.Combatant(99, id);
    private static PlayerCombatStatistics Stats(CombatCluster c, long id) { True(c.Encounter.Statistics.TryGet(id, out var p)); return p; }
    private static DamageCommit Done(long id, uint npc, float loss = 3) => Commit(false, null, npc, AttackerClass.Player, id, 0, loss);
    private static DamageCommit Taken(long id, uint npc, float loss = 3) => Commit(true, id, 100, npc == 0 ? AttackerClass.None : AttackerClass.NPC, null, npc, loss);
    private static DamageCommit Commit(bool playerVictim, long? victimPlayer, uint victimObject, AttackerClass attacker, long? attackerPlayer, uint attackerObject, float loss, byte dot = 0) =>
        new DamageCommit(new EventId(1, Epoch, ++_sequence), new DamageFacts(99, victimObject, playerVictim, victimPlayer, attacker, attackerPlayer, 1, "Victim", "Attacker", attackerObject == 0 ? 0 : 99, attackerObject, dot), loss, 1);
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS cluster: " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool condition) { if (!condition) throw new Exception("Cluster assertion failed"); }
    private static void Near(double expected, double actual) { if (Math.Abs(expected - actual) > 0.000001) throw new Exception($"Expected {expected}, got {actual}"); }
}
