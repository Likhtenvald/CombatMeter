using System;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;

internal static class StatisticsChecks
{
    private static readonly Guid Epoch = new Guid("10234567-89ab-cdef-0123-456789abcdef");
    private static long _sequence;
    private static int _passed;

    internal static int Run()
    {
        Test("statistics: player to NPC is Damage Done only", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(false, null, AttackerClass.Player, 10, 12.5f, "Greydwarf", "Alice"));
            PlayerCombatStatistics alice = Get(a, 10);
            Eq(12.5f, alice.DamageDone); Eq(0f, alice.DamageTaken); Eq("Alice", alice.DisplayName);
        });
        Test("statistics: NPC to player is Damage Taken only", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(true, 10, AttackerClass.NPC, null, 3.25f, "Alice", "Troll"));
            PlayerCombatStatistics alice = Get(a, 10);
            Eq(0f, alice.DamageDone); Eq(3.25f, alice.DamageTaken);
        });
        Test("statistics: environment to player is Damage Taken", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(true, 10, AttackerClass.None, null, 4f, "Alice", ""));
            Eq(4f, Get(a, 10).DamageTaken);
        });
        Test("statistics: unattributed poison remains victim Damage Taken", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(true, 10, AttackerClass.Unresolved, null, 1.75f, "Alice", "", 7));
            Eq(1.75f, Get(a, 10).DamageTaken); Eq(1, a.Count);
        });
        Test("statistics: player versus player is wholly ignored", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(true, 20, AttackerClass.Player, 10, 8f, "Bob", "Alice"));
            Eq(0, a.Count);
        });
        Test("statistics: player self damage is wholly ignored", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(true, 10, AttackerClass.Player, 10, 8f, "Alice", "Alice"));
            Eq(0, a.Count);
        });
        Test("statistics: distinct accepted events sum exact float deltas", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(false, null, AttackerClass.Player, 10, 0.1f, "Boar", "Alice"));
            a.Accept(Commit(false, null, AttackerClass.Player, 10, 0.2f, "Boar", "Alice"));
            Eq(0.1f + 0.2f, Get(a, 10).DamageDone);
        });
        Test("statistics: multiple players remain independent", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(false, null, AttackerClass.Player, 10, 2f, "Troll", "Alice"));
            a.Accept(Commit(false, null, AttackerClass.Player, 20, 5f, "Troll", "Bob"));
            Eq(2f, Get(a, 10).DamageDone); Eq(5f, Get(a, 20).DamageDone); Eq(2, a.Count);
        });
        Test("statistics: identical names with different IDs remain separate", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(false, null, AttackerClass.Player, 10, 2f, "Troll", "Same"));
            a.Accept(Commit(false, null, AttackerClass.Player, 20, 5f, "Troll", "Same"));
            Eq(2f, Get(a, 10).DamageDone); Eq(5f, Get(a, 20).DamageDone); Eq(2, a.Count);
        });
        Test("statistics: same ID with changed name remains one player", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(false, null, AttackerClass.Player, 10, 2f, "Troll", "Old"));
            a.Accept(Commit(false, null, AttackerClass.Player, 10, 5f, "Troll", "New"));
            Eq(7f, Get(a, 10).DamageDone); Eq("New", Get(a, 10).DisplayName); Eq(1, a.Count);
        });
        Test("statistics: explicit reset clears session totals", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(false, null, AttackerClass.Player, 10, 2f, "Troll", "Alice"));
            a.Reset(); Eq(0, a.Count); True(!a.TryGet(10, out _));
        });
        Test("statistics: missing required player identity is ignored", () =>
        {
            var a = NewAggregator();
            a.Accept(Commit(false, null, AttackerClass.Player, null, 2f, "Troll", "Alice"));
            a.Accept(Commit(true, null, AttackerClass.NPC, null, 2f, "Alice", "Troll"));
            Eq(0, a.Count);
        });
        Test("statistics integration: duplicate commit changes totals once", () =>
        {
            var a = NewAggregator();
            var session = new CommitSession(99, Epoch, true, _ => { }, (_, _, _) => { }, c => a.Accept(c));
            DamageCommit c = Commit(false, null, AttackerClass.Player, 10, 6f, "Troll", "Alice", sourcePeer: 11);
            Eq(Acceptance.Accepted, session.ProcessDamageCommit(c, 11, CommitOrigin.Remote));
            Eq(Acceptance.Duplicate, session.ProcessDamageCommit(c, 11, CommitOrigin.Remote));
            Eq(6f, Get(a, 10).DamageDone);
        });
        Test("statistics integration: validation rejection never reaches aggregator", () =>
        {
            var a = NewAggregator();
            var session = new CommitSession(99, Epoch, true, _ => { }, (_, _, _) => { }, c => a.Accept(c));
            DamageCommit invalid = Commit(false, null, AttackerClass.Player, 10, float.NaN,
                "Troll", "Alice", sourcePeer: 11);
            Eq(Acceptance.InvalidDamage, session.ProcessDamageCommit(invalid, 11, CommitOrigin.Remote));
            Eq(0, a.Count);
        });
        Test("statistics integration: local and remote accepted paths share aggregator", () =>
        {
            var a = NewAggregator();
            var session = new CommitSession(99, Epoch, true, _ => { }, (_, _, _) => { }, c => a.Accept(c));
            session.Observe(Facts(false, null, AttackerClass.Player, 10, "Troll", "Alice"), 2f, DateTime.UtcNow.Ticks);
            DamageCommit remote = Commit(true, 20, AttackerClass.NPC, null, 3f, "Bob", "Troll", sourcePeer: 11);
            Eq(Acceptance.Accepted, session.ProcessDamageCommit(remote, 11, CommitOrigin.Remote));
            Eq(2f, Get(a, 10).DamageDone); Eq(3f, Get(a, 20).DamageTaken);
        });
        return _passed;
    }

    private static CombatStatisticsAggregator NewAggregator()
    {
        _sequence = 0;
        return new CombatStatisticsAggregator();
    }

    private static DamageCommit Commit(bool victimPlayer, long? victimId, AttackerClass attacker,
        long? attackerId, float loss, string victimName, string attackerName, byte hitType = 1, long sourcePeer = 11) =>
        new DamageCommit(new EventId(sourcePeer, Epoch, ++_sequence),
            Facts(victimPlayer, victimId, attacker, attackerId, victimName, attackerName, hitType),
            loss, DateTime.UtcNow.Ticks);

    private static DamageFacts Facts(bool victimPlayer, long? victimId, AttackerClass attacker,
        long? attackerId, string victimName, string attackerName, byte hitType = 1) =>
        new DamageFacts(111, 3, victimPlayer, victimId, attacker, attackerId, hitType, victimName, attackerName);

    private static PlayerCombatStatistics Get(CombatStatisticsAggregator a, long id)
    {
        if (!a.TryGet(id, out PlayerCombatStatistics value)) throw new Exception("Missing player " + id);
        return value;
    }

    private static void Test(string name, Action run) { run(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
