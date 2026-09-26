using System;
using System.Linq;
using DiagnosticDamageProbe;
using DiagnosticDamageProbe.Attribution;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Snapshot;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;

internal static class EnvironmentalPolicyChecks
{
    private static int _passed;
    private static long _sequence;
    private static readonly Guid Epoch = new Guid("80234567-89ab-cdef-0123-456789abcdef");

    internal static int Run()
    {
        foreach (HitData.HitType hit in new[] { HitData.HitType.Fall, HitData.HitType.Drowning, HitData.HitType.Smoke })
        {
            Test(hit + ": classification ignores damage with no statistics row", () =>
            {
                var stats = new CombatStatisticsAggregator();
                Eq(PveStatisticKind.Ignored, CombatStatisticsAggregator.Classify(Environment(hit)).Kind);
                Eq(PveStatisticKind.Ignored, stats.Accept(Environment(hit)).Kind); Eq(0, stats.Count);
            });
            Test(hit + ": cannot create encounter or participant", () =>
            {
                var e = Encounter(); True(!e.Accept(Environment(hit), 1));
                Eq(EncounterState.NoEncounter, e.State); Eq(0, e.Statistics.Count); Eq(0, e.Participants.Count());
                True(!e.TryGetParticipant(1, out _)); Eq(0d, e.LastActivityTime);
            });
            Test(hit + ": attributed input path cannot create player-only cluster", () =>
            {
                var m = new CombatClusterManager(); var attributed = new MagicAttributionResolver().Resolve(Environment(hit));
                True(attributed.ApplyVanillaClassification); Eq<CombatCluster>(null, m.Accept(attributed, 1));
                Eq(0, m.ActiveCount); True(!m.TryGetMembership(CombatNode.Player(1), out _));
                True(!m.TryGetClusterForPlayer(1, out _)); Eq<CombatCluster>(null, m.LastFinished);
                var e = Encounter(); True(!e.Accept(attributed, 1)); Eq(EncounterState.NoEncounter, e.State);
            });
            Test(hit + ": Active activity statistics participants and DPS are unchanged", () =>
            {
                var e = Encounter(); e.Accept(Done(), 0); double dps = e.Dps(1, 6);
                True(!e.Accept(Environment(hit), 10)); True(!e.Accept(Environment(hit, 2), 11));
                Eq(0d, e.LastActivityTime); Eq(1, e.Statistics.Count); Eq(1, e.Participants.Count());
                True(e.Statistics.TryGet(1, out var stats)); Eq(6f, stats.DamageDone); Eq(0f, stats.DamageTaken);
                Eq(dps, e.Dps(1, 11)); e.Update(20); Eq(EncounterState.Finished, e.State); Eq(20d, e.EndTime);
            });
            Test(hit + ": Recovery cannot resume or alter death ticket/deadline", () =>
            {
                var e = Encounter(); e.Accept(Done(), 0); e.OnPlayerDied(1, 1, "life"); e.Update(20);
                Eq(EncounterState.Recovery, e.State); True(e.TryGetRecoveryTicket(1, out var ticket));
                double dps = e.Dps(1, 20); True(!e.Accept(Environment(hit), 21));
                Eq(EncounterState.Recovery, e.State); Eq(0d, e.LastActivityTime);
                True(e.TryGetRecoveryTicket(1, out var after)); True(ReferenceEquals(ticket, after));
                Eq(1d, after.DeathTime); Eq(181d, after.ExpiryTime); Eq(1, e.RecoveryTicketCount);
                True(e.Statistics.TryGet(1, out var stats)); Eq(0f, stats.DamageTaken); Eq(6f, stats.DamageDone); Eq(dps, e.Dps(1, 21));
                e.Update(181); Eq(EncounterState.Finished, e.State); Eq(181d, e.EndTime);
            });
            Test(hit + ": cluster Recovery is unchanged and new player is not joined", () =>
            {
                var m = new CombatClusterManager(); var c = m.Accept(Done(), 0); m.OnPlayerDied(1, 1); m.Update(20);
                c.Encounter.TryGetRecoveryTicket(1, out var ticket);
                Eq<CombatCluster>(null, m.Accept(Environment(hit), 21)); Eq<CombatCluster>(null, m.Accept(Environment(hit, 2, 10), 22));
                Eq(EncounterState.Recovery, c.Encounter.State); Eq(0d, c.Encounter.LastActivityTime);
                True(c.Encounter.TryGetRecoveryTicket(1, out var after)); True(ReferenceEquals(ticket, after)); Eq(181d, after.ExpiryTime);
                Eq(2, c.MemberCount); True(!m.TryGetClusterForPlayer(2, out _)); Eq(1, c.Encounter.Statistics.Count);
                Eq(0f, c.Encounter.Statistics.Players.Single().DamageTaken);
            });
            Test(hit + ": cannot bridge clusters or extend their soft timeout", () =>
            {
                var m = new CombatClusterManager(); var a = m.Accept(Done(), 0); var b = m.Accept(Done(2, 20), 2);
                // Even with a real NPC identity, the explicit hit-type policy excludes this event.
                Eq<CombatCluster>(null, m.Accept(Environment(hit, 1, 20), 10)); Eq(2, m.ActiveCount);
                True(m.TryGetClusterForPlayer(1, out var routed)); Eq(a, routed); True(m.TryGetClusterForPlayer(2, out routed)); Eq(b, routed);
                Eq(0d, a.Encounter.LastActivityTime); Eq(2d, b.Encounter.LastActivityTime);
                m.Update(20); True(!m.TryGetClusterForPlayer(1, out _)); True(m.TryGetClusterForPlayer(2, out _));
                m.Update(22); Eq(0, m.ActiveCount);
            });
            Test(hit + ": actual host snapshot remains empty before/after combat timeout", () =>
            {
                double now = 0; var adapter = Host(() => now);
                adapter.Observe(101, Environment(hit).Facts, 3); adapter.Update(); Empty(adapter);
                Eq(EncounterState.NoEncounter, adapter.Encounter.State); Eq(0, adapter.Clusters.ActiveCount);
                now = 1; adapter.Observe(101, Done().Facts, 6); adapter.Update();
                True(adapter.SnapshotStore.TryGetLatest(out var active)); Eq(EncounterState.Active, active.EncounterState);
                now = 19; adapter.Observe(101, Environment(hit).Facts, 3);
                now = 22; adapter.Update(); Empty(adapter);
                now = 23; adapter.Observe(101, Environment(hit).Facts, 3); adapter.Update(); Empty(adapter);
                Eq(0, adapter.Clusters.ActiveCount); adapter.Dispose();
            });
        }
        foreach (HitData.HitType hit in new[] { HitData.HitType.EnemyHit, HitData.HitType.Poisoned, HitData.HitType.Burning,
            HitData.HitType.Undefined, HitData.HitType.Tree, HitData.HitType.Incinerator })
        {
            Test(hit + ": non-excluded Damage Taken preserves baseline behavior", () =>
            {
                var damage = hit == HitData.HitType.EnemyHit ? Environment(hit, 1, 10) : Environment(hit);
                Eq(PveStatisticKind.DamageTaken, CombatStatisticsAggregator.Classify(damage).Kind);
                var e = Encounter(); True(e.Accept(damage, 0)); Eq(3f, e.Statistics.Players.Single().DamageTaken);
                var m = new CombatClusterManager(); var c = m.Accept(damage, 0); True(c != null);
                Eq(3f, c.Encounter.Statistics.Players.Single().DamageTaken); True(m.TryGetClusterForPlayer(1, out _));
                m.OnPlayerDied(1, 1); m.Update(20); m.Accept(damage, 21);
                Eq(EncounterState.Active, c.Encounter.State); Eq(0, c.Encounter.RecoveryTicketCount);
            });
        }
        foreach (var dot in new[] { DotKind.Poison, DotKind.Burning })
        {
            Test(dot + ": attribution still reaches encounter and cluster Damage Done", () =>
            {
                var resolver = new MagicAttributionResolver();
                resolver.ApplyPoolUpdate("99:10", dot, 0, 10, AttackerClass.Player, 1, "");
                var hit = dot == DotKind.Poison ? HitData.HitType.Poisoned : HitData.HitType.Burning;
                var facts = new DamageFacts(99, 10, false, null, AttackerClass.None, null, (byte)hit, "NPC", "", dotKind: (byte)((int)dot + 1));
                var attributed = resolver.Resolve(Commit(facts)); True(!attributed.ApplyVanillaClassification);
                Eq(1L, attributed.DamageDone.Single().PlayerId.Value); Eq(3f, attributed.DamageDone.Single().Damage);
                var e = Encounter(); True(e.Accept(attributed, 0)); Eq(3f, e.Statistics.Players.Single().DamageDone);
                var m = new CombatClusterManager(); var c = m.Accept(attributed, 0); Eq(3f, c.Encounter.Statistics.Players.Single().DamageDone);
            });
        }
        return _passed;
    }
    private static EncounterManager Encounter() => new EncounterManager(new CombatStatisticsAggregator());
    private static DamageCommit Done(long player = 1, uint npc = 10) => Commit(new DamageFacts(99, npc, false, null,
        AttackerClass.Player, player, (byte)HitData.HitType.PlayerHit, "NPC", "Player"), 6);
    private static DamageCommit Environment(HitData.HitType hit, long player = 1, uint npc = 0) => Commit(new DamageFacts(99, 100, true,
        player, npc == 0 ? AttackerClass.None : AttackerClass.NPC, null, (byte)hit, "Player", "", npc == 0 ? 0 : 99, npc));
    private static DamageCommit Commit(DamageFacts facts, float loss = 3) => new DamageCommit(new EventId(101, Epoch, ++_sequence), facts, loss, DateTime.UtcNow.Ticks);
    private static DamageCommitTransport Host(Func<double> now)
    {
        Player.Instances.Clear(); Player.m_localPlayer = new Player { PlayerId = 1 }; Player.m_localPlayer.View.Zdo.Owner = 101;
        Player.Instances.Add(Player.m_localPlayer); ZDOMan.instance = new ZDOMan(); ZDOMan.Session = 101;
        ZNet.instance = new ZNet { Server = true, SinglePlayer = true }; ZRoutedRpc.instance = new ZRoutedRpc();
        var adapter = new DamageCommitTransport(now); adapter.Bind(ZNet.instance); return adapter;
    }
    private static void Empty(DamageCommitTransport adapter)
    { True(adapter.SnapshotStore.TryGetLatest(out var snapshot)); Eq(EncounterState.NoEncounter, snapshot.EncounterState); Eq(0L, snapshot.EncounterId); Eq(0, snapshot.Players.Count); }
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS environmental policy: " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Environmental policy assertion failed"); }
}
