using System;
using DiagnosticDamageProbe;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Transport;

internal static class ClusterTransportChecks
{
    private static int _passed;
    internal static int Run()
    {
        Test("host feeds independent clusters and snapshot uses local membership", () =>
        {
            var h = new Host();
            h.Adapter.Observe(101, Facts(1, 10), 5);
            h.Adapter.Observe(101, Facts(2, 20), 7);
            Eq(2, h.Adapter.Clusters.ActiveCount);
            True(h.Adapter.Encounter.Statistics.TryGet(1, out var a)); Eq(5f, a.DamageDone);
            True(h.Adapter.Encounter.Statistics.TryGet(2, out var b)); Eq(7f, b.DamageDone);
            h.Adapter.Update(); True(h.Adapter.SnapshotStore.TryGetLatest(out var snapshot));
            Eq(1, snapshot.Players.Count); Eq(1L, snapshot.Players[0].PlayerId);
            True(h.Adapter.Clusters.TryGetMembership(CombatNode.Player(1), out var cluster));
            True(cluster.Encounter.Statistics.TryGet(1, out var separate));
            True(!ReferenceEquals(a, separate));
            // Legacy diagnostics stay independent, but cannot supply a fallback snapshot.
            h.Adapter.Clusters.Reset(); Eq(2, h.Adapter.Encounter.Statistics.Count);
            h.Now = 1; h.Adapter.Update(); True(h.Adapter.SnapshotStore.TryGetLatest(out snapshot)); Eq(0, snapshot.Players.Count); Eq(EncounterState.NoEncounter, snapshot.EncounterState);
        });
        Test("canonical remote dedup applies bridge once to both paths", () =>
        {
            var h = new Host();
            h.Adapter.Observe(101, Facts(1, 10), 5); h.Adapter.Observe(101, Facts(2, 20), 7);
            var commit = new DamageCommit(new EventId(202, Guid.NewGuid(), 1), Facts(1, 20), 11, DateTime.UtcNow.Ticks);
            h.Rpc.Handlers[DamageCommitTransport.CommitRpc](202, new ZPackage(CommitCodec.Encode(commit)));
            h.Rpc.Handlers[DamageCommitTransport.CommitRpc](202, new ZPackage(CommitCodec.Encode(commit)));
            Eq(1, h.Adapter.Clusters.ActiveCount);
            True(h.Adapter.Clusters.TryGetMembership(CombatNode.Player(1), out var c));
            True(c.Encounter.Statistics.TryGet(1, out var p)); Eq(16f, p.DamageDone);
            True(h.Adapter.Encounter.Statistics.TryGet(1, out p)); Eq(16f, p.DamageDone);
        });
        Test("host death routes to player's cluster and retains duplicate guard", () =>
        {
            var h = new Host(); h.Adapter.Observe(101, Facts(1, 10), 5); h.Adapter.Observe(101, Facts(2, 20), 7);
            h.Now = 1; Eq("Committed", h.Adapter.ObservePlayerDeath(1, "first").Result);
            Eq("Duplicate", h.Adapter.ObservePlayerDeath(1, "first").Result);
            True(h.Adapter.Clusters.TryGetMembership(CombatNode.Player(1), out var a));
            True(h.Adapter.Clusters.TryGetMembership(CombatNode.Player(2), out var b));
            Eq(1, a.Encounter.RecoveryTicketCount); Eq(0, b.Encounter.RecoveryTicketCount);
            h.Now = 21; h.Adapter.Update();
            Eq(EncounterState.Recovery, a.Encounter.State); Eq(EncounterState.Finished, b.Encounter.State);
            Eq(1, h.Adapter.Clusters.ActiveCount);
        });
        Test("client cannot create cluster core or commit host death", () =>
        {
            ZDOMan.instance = new ZDOMan(); ZDOMan.Session = 202;
            ZNet.instance = new ZNet { Server = false, ServerPeer = new ZNetPeer { m_uid = 101 } };
            ZRoutedRpc.instance = new ZRoutedRpc();
            var adapter = new DamageCommitTransport(() => 0); adapter.Bind(ZNet.instance);
            Eq<CombatClusterManager>(null, adapter.Clusters); Eq("NotHost", adapter.ObservePlayerDeath(1).Result);
        });
        Test("world stop clears cluster membership and retained state", () =>
        {
            var h = new Host(); h.Adapter.Observe(101, Facts(1, 10), 5);
            var clusters = h.Adapter.Clusters; h.Adapter.Stop(ZNet.instance);
            Eq<CombatClusterManager>(null, h.Adapter.Clusters); Eq(0, clusters.ActiveCount);
            True(!clusters.TryGetMembership(CombatNode.Player(1), out _));
            True(!h.Adapter.SnapshotStore.TryGetLatest(out _));
        });
        Test("resolved summon attribution feeds summoner membership", () =>
        {
            var h = new Host(); h.Adapter.ObserveSummonProvenance(101, 99, 50, -42);
            var facts = new DamageFacts(99, 10, false, null, AttackerClass.NPC, null, 1, "Troll", "Summon", 99, 50);
            h.Adapter.Observe(101, facts, 8);
            True(h.Adapter.Clusters.TryGetMembership(CombatNode.Player(-42), out var c));
            Eq(2, c.MemberCount); True(c.Contains(CombatNode.Combatant(99, 10)));
            True(!c.Contains(CombatNode.Combatant(99, 50)));
            True(c.Encounter.Statistics.TryGet(-42, out var p)); Eq(8f, p.DamageDone);
            True(h.Adapter.Encounter.Statistics.TryGet(-42, out var legacy)); Eq(8f, legacy.DamageDone);
        });
        return _passed;
    }
    private static DamageFacts Facts(long player, uint npc) => new DamageFacts(99, npc, false, null, AttackerClass.Player, player, 1, "NPC", "Player");
    private sealed class Host
    {
        internal double Now;
        internal readonly DamageCommitTransport Adapter;
        internal readonly ZRoutedRpc Rpc = new ZRoutedRpc();
        internal Host()
        {
            Player.Instances.Clear(); Player.m_localPlayer = new Player { PlayerId = 1 };
            Player.m_localPlayer.View.Zdo.Owner = 101; Player.Instances.Add(Player.m_localPlayer);
            ZDOMan.instance = new ZDOMan(); ZDOMan.Session = 101;
            ZNet.instance = new ZNet { Server = true, SinglePlayer = false };
            ZRoutedRpc.instance = Rpc; Rpc.Send = (_, _, _) => { };
            Adapter = new DamageCommitTransport(() => Now); Adapter.Bind(ZNet.instance);
        }
    }
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS cluster adapter: " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool condition) { if (!condition) throw new Exception("Cluster adapter assertion failed"); }
}
