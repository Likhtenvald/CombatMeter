using System;
using DiagnosticDamageProbe;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Transport;

internal static class LifecycleIntegrationChecks
{
    private static readonly Guid Epoch = new Guid("30234567-89ab-cdef-0123-456789abcdef");
    private static long _sequence;
    private static int _passed;

    internal static int Run()
    {
        Test("lifecycle integration 01: host local death commits once", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(101, 5);
            Eq("Committed", adapter.ObservePlayerDeath(101).Result);
            True(adapter.Encounter.TryGetParticipant(101, out EncounterParticipant p) && !p.IsAlive);
        });
        Test("lifecycle integration 02: remote player death observed by host commits", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(202, 5);
            Eq("Committed", adapter.ObservePlayerDeath(202).Result);
            True(adapter.Encounter.TryGetParticipant(202, out EncounterParticipant p) && !p.IsAlive);
            True(adapter.Encounter.TryGetRecoveryTicket(202, out _));
        });
        Test("lifecycle integration 03: client callback cannot mutate encounter", () =>
        {
            DamageCommitTransport adapter = Client();
            Eq("NotHost", adapter.ObservePlayerDeath(202).Result);
            Eq<EncounterManager>(null, adapter.Encounter);
        });
        Test("lifecycle integration 04: repeated death is idempotent", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(101, 5);
            Eq("Committed", adapter.ObservePlayerDeath(101, "101:1").Result);
            Eq("Duplicate", adapter.ObservePlayerDeath(101, "101:1").Result);
        });
        Test("lifecycle integration 05: death preserves damage totals", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(101, 7);
            adapter.ObservePlayerDeath(101);
            True(adapter.Encounter.Statistics.TryGet(101, out var p)); Eq(7f, p.DamageDone);
        });
        Test("lifecycle integration 06: continuing PvE remains active", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(101, 5); EncounterManager m = adapter.Encounter;
            adapter.ObservePlayerDeath(101); m.Accept(Done(202, 2), m.LastActivityTime + 1);
            Eq(EncounterState.Active, m.State);
        });
        Test("lifecycle integration 07: death and soft timeout enter recovery", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(101, 5); EncounterManager m = adapter.Encounter;
            adapter.ObservePlayerDeath(101); m.Update(m.LastActivityTime + m.SoftTimeout);
            Eq(EncounterState.Recovery, m.State);
        });
        Test("lifecycle integration 08: PvE after recovery resumes active", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(101, 5); EncounterManager m = adapter.Encounter;
            adapter.ObservePlayerDeath(101); m.Update(m.LastActivityTime + m.SoftTimeout);
            m.Accept(Done(101, 2), m.LastActivityTime + m.SoftTimeout + 1); Eq(EncounterState.Active, m.State);
        });
        Test("lifecycle integration 09: respawn metadata alone does not activate", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(101, 5); EncounterManager m = adapter.Encounter;
            adapter.ObservePlayerDeath(101); double recoveryAt = m.LastActivityTime + m.SoftTimeout;
            m.Update(recoveryAt); m.OnPlayerRespawned(101, recoveryAt + 1); Eq(EncounterState.Recovery, m.State);
        });
        Test("lifecycle integration 10: invalid ID creates no participant", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(101, 5); int before = adapter.Encounter.Statistics.Count;
            Eq("MissingPlayerID", adapter.ObservePlayerDeath(0).Result); Eq(before, adapter.Encounter.Statistics.Count);
            True(!adapter.Encounter.TryGetParticipant(0, out _));
        });
        Test("lifecycle integration 11: nonparticipant death is ignored", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(101, 5);
            Eq("NoActiveParticipant", adapter.ObservePlayerDeath(202).Result);
            True(!adapter.Encounter.TryGetParticipant(202, out _));
        });
        Test("lifecycle integration 12: new runtime incarnation permits a later death", () =>
        {
            DamageCommitTransport adapter = HostWithParticipant(101, 5);
            Eq("Committed", adapter.ObservePlayerDeath(101, "101:1").Result);
            Eq("Committed", adapter.ObservePlayerDeath(101, "101:2").Result);
            Eq("Duplicate", adapter.ObservePlayerDeath(101, "101:2").Result);
        });
        return _passed;
    }

    private static DamageCommitTransport HostWithParticipant(long playerId, float damage)
    {
        _sequence = 0;
        ZDOMan.Session = 101;
        ZDOMan.instance = new ZDOMan();
        ZNet.instance = new ZNet { Server = true, Dedicated = false, SinglePlayer = false };
        ZRoutedRpc.instance = new ZRoutedRpc();
        var adapter = new DamageCommitTransport();
        adapter.Bind(ZNet.instance);
        adapter.Observe(101,
            new DamageFacts(111, 3, false, null, AttackerClass.Player, playerId, 1, "Troll", "Player"), damage);
        return adapter;
    }

    private static DamageCommitTransport Client()
    {
        ZDOMan.Session = 202;
        ZDOMan.instance = new ZDOMan();
        ZNet.instance = new ZNet { Server = false, Dedicated = false, SinglePlayer = false,
            ServerPeer = new ZNetPeer { m_uid = 101 } };
        ZRoutedRpc.instance = new ZRoutedRpc();
        var adapter = new DamageCommitTransport();
        adapter.Bind(ZNet.instance);
        return adapter;
    }

    private static DamageCommit Done(long playerId, float damage) => new DamageCommit(
        new EventId(101, Epoch, ++_sequence),
        new DamageFacts(111, 3, false, null, AttackerClass.Player, playerId, 1, "Troll", "Player"),
        damage, DateTime.UtcNow.Ticks);

    private static void Test(string name, Action run) { run(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
