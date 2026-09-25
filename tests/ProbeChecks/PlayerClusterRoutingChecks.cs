using System;
using System.Linq;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Transport;

internal static class PlayerClusterRoutingChecks
{
    private static int _passed;
    private static long _sequence;
    private static readonly Guid Epoch = new Guid("70234567-89ab-cdef-0123-456789abcdef");

    internal static int Run()
    {
        Test("unknown player has no cluster and lookup creates nothing", () =>
        {
            var m = New();
            for (int i = 0; i < 10; i++) NoCluster(m, 1);
            Eq(0, m.ActiveCount); Eq<CombatCluster>(null, m.LastFinished);
            Eq(1L, m.Accept(Done(1, 10), 0).Id);
        });
        Test("first combat routes to exact created cluster", () =>
        {
            var m = New(); var c = m.Accept(Done(1, 10), 0);
            Route(m, 1, c);
        });
        Test("environmental player-only cluster routes normally", () =>
        {
            var m = New(); var c = m.Accept(Taken(1), 0);
            Eq(1, c.MemberCount); Route(m, 1, c);
        });
        Test("unattributed poison player-only cluster routes normally", () =>
        {
            var m = New(); var c = m.Accept(Taken(1, true), 0);
            Eq(1, c.MemberCount); Route(m, 1, c);
        });
        Test("independent players never route to each other's cluster", () =>
        {
            var m = New(); var a = m.Accept(Done(1, 10), 0); var b = m.Accept(Done(2, 20), 1);
            True(!ReferenceEquals(a, b)); Route(m, 1, a); Route(m, 2, b); NoCluster(m, 3);
        });
        Test("join through shared combatant is immediately visible", () =>
        {
            var m = New(); var a = m.Accept(Done(1, 10), 0);
            NoCluster(m, 2); m.Accept(Done(2, 10), 1);
            Route(m, 1, a); Route(m, 2, a);
        });
        Test("merge routes every donor and survivor player to lowest ID", () =>
        {
            var m = New(); var a = m.Accept(Done(1, 10), 0); m.Accept(Done(3, 10), 1);
            var b = m.Accept(Done(2, 20), 2); m.Accept(Done(4, 20), 3);
            Route(m, 2, b); Route(m, 4, b); True(a.Id < b.Id);
            m.Accept(Done(4, 10), 4);
            foreach (long player in new long[] { 1, 2, 3, 4 }) Route(m, player, a);
            Eq(a.Id, b.MergedIntoId.Value); True(!m.TryGet(b.Id, out _)); Eq(1, m.ActiveCount);
        });
        Test("successive merges never return a retired intermediate cluster", () =>
        {
            var m = New(); var a = m.Accept(Done(1, 10), 0); var b = m.Accept(Done(2, 20), 1);
            var c = m.Accept(Done(3, 30), 2); m.Accept(Done(3, 20), 3); Route(m, 3, b);
            m.Accept(Done(2, 10), 4);
            foreach (long player in new long[] { 1, 2, 3 }) Route(m, player, a);
            True(!m.TryGet(b.Id, out _)); True(!m.TryGet(c.Id, out _));
        });
        Test("finished encounter has no route even when retained as LastFinished", () =>
        {
            var m = New(); var a = m.Accept(Done(1, 10), 0); m.Update(20);
            Eq(a, m.LastFinished); Eq(EncounterState.Finished, a.Encounter.State); NoCluster(m, 1);
        });
        Test("new encounter routes to new ID after Finished", () =>
        {
            var m = New(); var old = m.Accept(Done(1, 10), 0); m.Update(20); NoCluster(m, 1);
            var next = m.Accept(Done(1, 10), 21); True(old.Id != next.Id); Route(m, 1, next);
        });
        Test("Recovery membership remains routable until actual finish", () =>
        {
            var m = New(); var a = m.Accept(Done(1, 10), 0); m.OnPlayerDied(1, 1, "first");
            m.Update(20); Eq(EncounterState.Recovery, a.Encounter.State); Route(m, 1, a);
            m.Update(181); Eq(EncounterState.Finished, a.Encounter.State); NoCluster(m, 1);
        });
        Test("queries preserve state activity totals tickets and membership", () =>
        {
            var m = New(); var a = m.Accept(Done(1, 10), 0); m.Accept(Taken(1), 2);
            m.OnPlayerDied(1, 3, "death"); m.Update(22);
            var b = m.Accept(Done(2, 20), 23);
            string before = Fingerprint(m);
            for (int i = 0; i < 100; i++) { Route(m, 1, a); Route(m, 2, b); NoCluster(m, 999); }
            Eq(before, Fingerprint(m));
            // A lookup must not disturb the death-incarnation guard either.
            Eq(PlayerDeathResult.Duplicate, m.OnPlayerDied(1, 23, "death"));
        });
        Test("query does not perform lifecycle updates", () =>
        {
            var m = New(); var a = m.Accept(Done(1, 10), 0); m.OnPlayerDied(1, 1);
            m.Update(40); Eq(EncounterState.Recovery, a.Encounter.State);
            m.Settings.Update(20, 30, 6);
            Route(m, 1, a); Eq(EncounterState.Recovery, a.Encounter.State);
            True(a.Encounter.TryGetRecoveryTicket(1, out var ticket)); Eq(181d, ticket.ExpiryTime);
            m.Update(40); NoCluster(m, 1); Eq(EncounterState.Finished, a.Encounter.State);
        });
        Test("names damage rank and query order do not affect routing", () =>
        {
            var m = New(); var a = m.Accept(Done(1, 10, 1, "Same"), 0);
            var b = m.Accept(Done(2, 20, 100, "Same"), 1);
            Route(m, 2, b); Route(m, 1, a);
            m.Accept(Done(1, 10, 1000, "Renamed"), 2);
            Route(m, 1, a); Route(m, 2, b); NoCluster(m, 3);
        });
        Test("creation and enumeration order are not player selection rules", () =>
        {
            foreach (bool reverse in new[] { false, true })
            {
                var m = New(); long first = reverse ? 2 : 1; long second = reverse ? 1 : 2;
                var a = m.Accept(Done(first, 10), 0); var b = m.Accept(Done(second, 20), 1);
                Route(m, second, b); Route(m, first, a); NoCluster(m, 3);
            }
        });
        Test("signed PlayerIDs route including long.MinValue; zero has no route", () =>
        {
            var m = New(); var a = m.Accept(Done(long.MinValue, 10), 0); var b = m.Accept(Done(-42, 20), 1);
            Route(m, long.MinValue, a); Route(m, -42, b); NoCluster(m, 0);
        });
        Test("peer ID and combatant creator are not implicitly PlayerIDs", () =>
        {
            // Commits below have SourcePeerId=777, combatant creator=99, PlayerID=1.
            var m = New(); var a = m.Accept(Done(1, 10), 0);
            Route(m, 1, a); NoCluster(m, 777); NoCluster(m, 99);
        });
        Test("session reset discards routes and queries cannot restore them", () =>
        {
            var m = New(); m.Accept(Done(1, 10), 0); m.Reset();
            for (int i = 0; i < 10; i++) NoCluster(m, 1);
            Eq(0, m.ActiveCount); Eq<CombatCluster>(null, m.LastFinished);
        });
        Test("finishing one cluster preserves another player's route", () =>
        {
            var m = New(); m.Accept(Done(1, 10), 0); var b = m.Accept(Done(2, 20), 10);
            m.Update(20); NoCluster(m, 1); Route(m, 2, b);
        });
        return _passed;
    }

    private static string Fingerprint(CombatClusterManager m) => string.Join(";", m.ActiveClusters.OrderBy(c => c.Id).Select(c =>
        $"{c.Id}/{c.Encounter.State}/{c.Encounter.StartTime}/{c.Encounter.LastActivityTime}/{c.Encounter.EndTime}/" +
        string.Join(",", c.Members.Select(n => $"{n.IsPlayer}:{n.Id}:{n.ObjectId}").OrderBy(n => n)) + "/" +
        string.Join(",", c.Encounter.Statistics.Players.OrderBy(p => p.PlayerId).Select(p => $"{p.PlayerId}:{p.DisplayName}:{p.DamageDone}:{p.DamageTaken}")) + "/" +
        string.Join(",", c.Encounter.Participants.OrderBy(p => p.PlayerId).Select(p =>
            $"{p.PlayerId}:{p.IsAlive}:" + (c.Encounter.TryGetRecoveryTicket(p.PlayerId, out var t) ? $"{t.DeathTime}:{t.ExpiryTime}" : "none"))))) + $"/last={m.LastFinished?.Id}";
    private static CombatClusterManager New() => new CombatClusterManager();
    private static DamageCommit Done(long player, uint npc, float loss = 3, string name = "Player") => Commit(
        new DamageFacts(99, npc, false, null, AttackerClass.Player, player, 1, "NPC", name), loss);
    private static DamageCommit Taken(long player, bool poison = false) => Commit(
        new DamageFacts(99, 100, true, player, poison ? AttackerClass.Unresolved : AttackerClass.None, null, 1, "Player", "", dotKind: poison ? (byte)1 : (byte)0), 3);
    private static DamageCommit Commit(DamageFacts facts, float loss) => new DamageCommit(new EventId(777, Epoch, ++_sequence), facts, loss, 1);
    private static void Route(CombatClusterManager m, long player, CombatCluster expected)
    { True(m.TryGetClusterForPlayer(player, out var actual)); True(ReferenceEquals(expected, actual)); }
    private static void NoCluster(CombatClusterManager m, long player)
    { True(!m.TryGetClusterForPlayer(player, out var actual)); Eq<CombatCluster>(null, actual); }
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS player routing: " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool condition) { if (!condition) throw new Exception("Player routing assertion failed"); }
}
