using System;
using System.Collections.Generic;
using System.Linq;
using DiagnosticDamageProbe;
using DiagnosticDamageProbe.Attribution;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;

internal static class AttributionChecks
{
    private static readonly Guid Epoch = new Guid("40234567-89ab-cdef-0123-456789abcdef");
    private static long _sequence;
    private static int _passed;

    internal static int Run()
    {
        Test("attribution 01: direct player damage unchanged", () =>
        { var r = Resolver(); var e = r.Resolve(Commit(AttackerClass.Player, 1, 5)); True(e.ApplyVanillaClassification); });
        Test("attribution 02: summon mapping attributes root damage", () =>
        { var r = Resolver(); r.Summons.Register("20:2", 1); Portion(r.Resolve(Commit(AttackerClass.NPC, null, 5, attackerZdo: "20:2")), 1, 5); });
        Test("attribution 03: missing summon mapping remains unattributed", () =>
        { var e = Resolver().Resolve(Commit(AttackerClass.NPC, null, 5, attackerZdo: "20:2")); True(e.ApplyVanillaClassification); Eq(0, e.DamageDone.Count); });
        Test("attribution 04: ownership change cannot alter stable summon mapping", () =>
        { var r = Resolver(); r.Summons.Register("20:2", 1); Portion(r.Resolve(Commit(AttackerClass.NPC, null, 3, attackerZdo: "20:2")), 1, 3); });
        Test("attribution 05: duplicate provenance message is idempotent", () =>
        { var a = new AttributionAcceptor(); var m = SummonMessage(1, 11); Eq("Accepted", a.Accept(m, 11)); Eq("Duplicate", a.Accept(m, 11)); });
        Test("attribution 06: invalid provenance sender rejected", () =>
        { var a = new AttributionAcceptor(); Eq("InvalidSenderOrEvent", a.Accept(SummonMessage(1, 11), 12)); });
        Test("attribution 07: session reset clears summon registry", () =>
        { var r = Resolver(); r.Summons.Register("20:2", 1); r.Reset(); True(!r.Summons.TryResolve("20:2", out _)); });
        Test("attribution 08: poison replacement changes owner", () =>
        { var r = Resolver(); r.ApplyPoolUpdate("10:1", DotKind.Poison, 0, 10, AttackerClass.Player, 1, "", true); Portion(r.Resolve(Dot(DotKind.Poison, 4)), 1, 4); });
        Test("attribution 09: ignored weaker poison keeps owner", () =>
        { var r = Resolver(); r.ApplyPoolUpdate("10:1", DotKind.Poison, 0, 10, AttackerClass.Player, 1, "", true); r.ApplyPoolUpdate("10:1", DotKind.Poison, 10, 10, AttackerClass.Player, 2, "", false); Portion(r.Resolve(Dot(DotKind.Poison, 4)), 1, 4); });
        Test("attribution 10: poison from summon resolves summoner", () =>
        { var r = Resolver(); r.Summons.Register("20:2", 7); r.ApplyPoolUpdate("10:1", DotKind.Poison, 0, 8, AttackerClass.NPC, null, "20:2", true); Portion(r.Resolve(Dot(DotKind.Poison, 3)), 7, 3); });
        Test("attribution 11: anonymous poison replacement clears owner", () =>
        { var r = Resolver(); r.ApplyPoolUpdate("10:1", DotKind.Poison, 0, 8, AttackerClass.Player, 1, "", true); r.ApplyPoolUpdate("10:1", DotKind.Poison, 8, 12, AttackerClass.None, null, "", true); var e = r.Resolve(Dot(DotKind.Poison, 3)); True(e.ApplyVanillaClassification); });
        Test("attribution 12: single-source burning tick goes to one player", () =>
        { var r = Resolver(); Add(r, DotKind.Burning, 1, 10); Portion(r.Resolve(Dot(DotKind.Burning, 4)), 1, 4); });
        Test("attribution 13: two-source burning splits proportionally", () =>
        { var r = Resolver(); Add(r, DotKind.Burning, 1, 60); Add(r, DotKind.Burning, 2, 40, 60); var p = r.Resolve(Dot(DotKind.Burning, 10)).DamageDone; Eq(6f, Find(p, 1)); Eq(4f, Find(p, 2)); });
        Test("attribution 14: repeated burning contributions accumulate", () =>
        { var l = new SharedDotContributionLedger(); l.Add(1, 2); l.Add(1, 3); Eq(5f, l.TotalOutstanding); });
        Test("attribution 15: actual loss smaller than pool is used", () =>
        { var l = new SharedDotContributionLedger(); l.Add(1, 100); Eq(3f, l.Distribute(3).Sum(x => x.Damage)); });
        Test("attribution 16: ledger portions decrease after tick", () =>
        { var l = new SharedDotContributionLedger(); l.Add(1, 10); l.Distribute(4); Eq(6f, l.TotalOutstanding); });
        Test("attribution 17: unattributed contribution participates in denominator", () =>
        { var l = new SharedDotContributionLedger(); l.Add(1, 60); l.Add(null, 40); var p = l.Distribute(10); Eq(6f, Find(p, 1)); Eq(4f, p.Where(x => !x.PlayerId.HasValue).Sum(x => x.Damage)); });
        Test("attribution 18: spirit uses proportional ledger", () =>
        { var r = Resolver(); Add(r, DotKind.Spirit, 1, 25); Add(r, DotKind.Spirit, 2, 75, 25); var p = r.Resolve(Dot(DotKind.Spirit, 8)).DamageDone; Eq(2f, Find(p, 1)); Eq(6f, Find(p, 2)); });
        Test("attribution 19: attributed magic versus player is excluded", () =>
        { var r = Resolver(); r.Summons.Register("20:2", 1); var e = r.Resolve(Commit(AttackerClass.NPC, null, 5, true, "20:2")); True(!e.ApplyVanillaClassification); Eq(0, e.DamageDone.Count); });
        Test("attribution 20: distributed totals equal actual HP loss", () =>
        { var l = new SharedDotContributionLedger(); l.Add(1, 1); l.Add(2, 2); l.Add(null, 3); float total = 0; foreach (float loss in new[] { .1f, .7f, 1.2f }) total += l.Distribute(loss).Sum(x => x.Damage); Near(2f, total); });
        Test("attribution 21: pending summon commit resolves on provenance", () =>
        { double now = 1; var a = Host(() => now); a.Observe(101, NpcFacts(false, "20:2"), 5); Eq(EncounterState.NoEncounter, a.Encounter.State); a.ObserveSummonProvenance(101, 20, 2, 7); Eq(5f, Stat(a, 7).DamageDone); });
        Test("attribution 22: pending timeout leaves event unattributed", () =>
        { double now = 1; var a = Host(() => now); a.Observe(101, NpcFacts(true, "20:2", 9), 5); now = 5; a.Update(); Eq(5f, Stat(a, 9).DamageTaken); True(!a.Encounter.Statistics.TryGet(7, out _)); });
        Test("attribution 23: duplicate accepted commit cannot replay attribution stage", () =>
        { int calls = 0; var s = new CommitSession(99, Epoch, true, _ => { }, (_, _, _) => { }, _ => calls++); var c = Commit(AttackerClass.Player, 1, 5, source: 11); Eq(Acceptance.Accepted, s.ProcessDamageCommit(c, 11, CommitOrigin.Remote)); Eq(Acceptance.Duplicate, s.ProcessDamageCommit(c, 11, CommitOrigin.Remote)); Eq(1, calls); });
        Test("attribution 24: attribution codec roundtrips factual provenance", () =>
        { var m = new AttributionMessage(new EventId(11, Epoch, 4), AttributionMessageKind.DotPool, 10, 1, null, DotKind.Spirit, 3, 8, AttackerClass.NPC, null, 20, 2, true); var d = AttributionCodec.Decode(AttributionCodec.Encode(m)); Eq(m.Id, d.Id); Eq(DotKind.Spirit, d.DotKind); Eq("20:2", d.SourceZdoId); Eq(8f, d.PoolAfter); });
        Test("attribution 25: pending DoT tick resolves on pool update", () =>
        { double now = 1; var a = Host(() => now); a.Observe(101, DotFacts(false, DotKind.Burning), 4); Eq(EncounterState.NoEncounter, a.Encounter.State); a.ObserveDotPool(101, 10, 1, DotKind.Burning, 0, 10, AttackerClass.Player, 7, 0, 0, true); Eq(4f, Stat(a, 7).DamageDone); });
        Test("attribution 26: pending DoT timeout remains unattributed", () =>
        { double now = 1; var a = Host(() => now); a.Observe(101, DotFacts(true, DotKind.Burning, 9), 4); now = 5; a.Update(); Eq(4f, Stat(a, 9).DamageTaken); });
        Test("attribution 27: valid summon registration creates provenance", () =>
        { var a = Host(() => 1); a.ObserveSummonProvenance(101, 20, 2, 7); True(a.Attribution.Summons.TryResolve("20:2", out long player)); Eq(7L, player); });
        Test("attribution 28: zero PlayerID registration is rejected", () =>
        { var a = Host(() => 1); a.ObserveSummonProvenance(101, 20, 2, 0); True(!a.Attribution.Summons.TryResolve("20:2", out _)); });
        Test("attribution 29: unresolved caster cannot invent fallback provenance", () =>
        { var a = Host(() => 1); a.ObserveSummonProvenance(101, 20, 2, 0); True(!a.Attribution.Summons.TryResolve("20:2", out _)); True(!a.Attribution.Summons.TryResolve("101:2", out _)); });
        Test("attribution 30: supported root prefab policy", () =>
        { True(SupportedSummonPolicy.IsSupportedPrefab("staff_greenroots_tentaroot(Clone)")); });
        Test("attribution 31: supported friendly skeleton prefab policy", () =>
        { True(SupportedSummonPolicy.IsSupportedPrefab("Skeleton_Friendly(Clone)")); });
        Test("attribution 32: unsupported SpawnAbility object is ignored by policy", () =>
        { True(!SupportedSummonPolicy.IsSupportedPrefab("projectile_fireball(Clone)")); True(!SupportedSummonPolicy.IsSupportedPrefab(null)); });
        Test("attribution 33: negative PlayerID is valid summon provenance", () =>
        { var a = Host(() => 1); a.ObserveSummonProvenance(101, 20, 2, -7); True(a.Attribution.Summons.TryResolve("20:2", out long player)); Eq(-7L, player); });
        Test("attribution 34: skeleton direct damage uses generic summon mapping", () =>
        { var r = Resolver(); r.Summons.Register("30:3", -7); Portion(r.Resolve(Commit(AttackerClass.NPC, null, 6, attackerZdo: "30:3")), -7, 6); });
        Test("attribution 35: duplicate generic registration remains idempotent", () =>
        { var r = Resolver(); True(r.Summons.Register("30:3", 7)); True(r.Summons.Register("30:3", 7)); Eq(1, r.Summons.Count); });
        Test("attribution 36: sequence one through four accepted in one epoch", () =>
        { var a = new AttributionAcceptor(); for (long i = 1; i <= 4; i++) Eq("Accepted", a.Accept(SummonMessage(i, 11), 11)); });
        Test("attribution 37: provenance outbox completes only matching head", () =>
        { var b = new AttributionOutbox(); var one = SummonMessage(1, 11); var two = SummonMessage(2, 11); True(b.Enqueue(one)); True(b.Enqueue(two)); True(!b.Complete(two.Id, out _)); Eq(2, b.Count); True(b.Complete(one.Id, out var done)); Eq(one.Id, done.Id); Eq(1, b.Count); });
        Test("attribution 38: provenance retry keeps exact EventId", () =>
        { var b = new AttributionOutbox(); var m = SummonMessage(1, 11); b.Enqueue(m); Eq(m.Id, b.Due(0, out _).Id); Eq(m.Id, b.Due(1, out _).Id); });
        Test("attribution 39: provenance retry lifetime is bounded", () =>
        { var b = new AttributionOutbox(); var m = SummonMessage(1, 11); b.Enqueue(m); b.Due(0, out _); Eq((AttributionMessage)null, b.Due(AttributionOutbox.MaxLifetimeSeconds, out var expired)); Eq(m.Id, expired.Id); Eq(0, b.Count); });
        Test("attribution 40: delayed pre-death commit cannot clear recovery ticket", () =>
        {
            double now = 100; var a = Host(() => now);
            a.Observe(101, new DamageFacts(10, 1, false, null, AttackerClass.Player, 7, 1, "Troll", "Player"), 1);
            a.Observe(101, NpcFacts(true, "20:2", 7), 5);
            now = 100.01; Eq("Committed", a.ObservePlayerDeath(7, "7:life").Result);
            a.ObserveSummonProvenance(101, 20, 2, 9);
            True(a.Encounter.TryGetRecoveryTicket(7, out _));
        });
        return _passed;
    }

    private static MagicAttributionResolver Resolver() { _sequence = 0; return new MagicAttributionResolver(); }
    private static void Add(MagicAttributionResolver r, DotKind kind, long player, float delta, float before = 0) => r.ApplyPoolUpdate("10:1", kind, before, before + delta, AttackerClass.Player, player, "", true);
    private static DamageCommit Dot(DotKind kind, float loss) => Commit(AttackerClass.None, null, loss, dot: kind);
    private static DamageCommit Commit(AttackerClass attacker, long? player, float loss, bool victimPlayer = false,
        string attackerZdo = "", DotKind? dot = null, long source = 99)
    {
        Split(attackerZdo, out long ac, out uint ao);
        return new DamageCommit(new EventId(source, Epoch, ++_sequence),
            new DamageFacts(10, 1, victimPlayer, victimPlayer ? 9 : (long?)null, attacker, player, 1, "Victim", "Source", ac, ao,
                dot.HasValue ? (byte)((int)dot.Value + 1) : (byte)0), loss, DateTime.UtcNow.Ticks);
    }
    private static DamageFacts NpcFacts(bool victimPlayer, string attacker, long victimId = 0)
    { Split(attacker, out long ac, out uint ao); return new DamageFacts(10, 1, victimPlayer, victimPlayer ? victimId : (long?)null, AttackerClass.NPC, null, 1, "Victim", "Root", ac, ao); }
    private static DamageFacts DotFacts(bool victimPlayer, DotKind kind, long victimId = 0) =>
        new DamageFacts(10, 1, victimPlayer, victimPlayer ? victimId : (long?)null, AttackerClass.None, null, 1,
            "Victim", "", 0, 0, (byte)((int)kind + 1));
    private static AttributionMessage SummonMessage(long seq, long peer) => new AttributionMessage(new EventId(peer, Epoch, seq), AttributionMessageKind.Summon, 20, 2, 7);
    private static void Split(string value, out long creator, out uint obj) { creator = 0; obj = 0; if (string.IsNullOrEmpty(value)) return; string[] p = value.Split(':'); creator = long.Parse(p[0]); obj = uint.Parse(p[1]); }
    private static void Portion(AttributedDamageEvent e, long id, float damage) { Eq(1, e.DamageDone.Count); Eq((long?)id, e.DamageDone[0].PlayerId); Eq(damage, e.DamageDone[0].Damage); }
    private static float Find(List<DamagePortion> p, long id) => p.Where(x => x.PlayerId == id).Sum(x => x.Damage);
    private static DamageCommitTransport Host(Func<double> clock)
    { ZDOMan.Session = 101; ZDOMan.instance = new ZDOMan(); ZNet.instance = new ZNet { Server = true, SinglePlayer = false }; ZRoutedRpc.instance = new ZRoutedRpc(); var a = new DamageCommitTransport(clock); a.Bind(ZNet.instance); return a; }
    private static PlayerCombatStatistics Stat(DamageCommitTransport a, long id) { if (!a.Encounter.Statistics.TryGet(id, out var p)) throw new Exception("Missing stat"); return p; }
    private static void Test(string name, Action run) { run(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Near(float expected, float actual) { if (Math.Abs(expected - actual) > .00001f) throw new Exception($"Expected {expected}, got {actual}"); }
}
