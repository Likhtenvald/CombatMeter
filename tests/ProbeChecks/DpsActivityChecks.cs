using System;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;

internal static class DpsActivityChecks
{
    private static readonly Guid Epoch = new Guid("78123456-789a-bcde-f012-3456789abcde");
    private static int _passed; private static long _sequence;

    internal static int Run()
    {
        Test("dps 01: single interval is clipped to now and timeout", () =>
        { var t = new PlayerDpsActivityTracker(); t.Add(0, 6); Eq(2d, t.ActiveTime(2, 6, true)); Eq(6d, t.ActiveTime(6, 6, true)); Eq(6d, t.ActiveTime(10, 6, true)); });
        Test("dps 02: overlapping intervals form one union", () =>
        { var t = Tracker(0, 4, 8); Eq(10d, t.ActiveTime(10, 6, true)); Eq(14d, t.ActiveTime(20, 6, true)); });
        Test("dps 03: separated bursts exclude idle gap", () =>
        { var t = Tracker(0, 2, 20); Eq(14d, t.ActiveTime(30, 6, true)); });
        Test("dps 04: out of order insertion is factual-time deterministic", () =>
        { var a = Tracker(0, 4, 8); var b = Tracker(8, 0, 4); Eq(a.ActiveTime(20, 6, true), b.ActiveTime(20, 6, true)); });
        Test("dps 05: duplicate timestamp does not multiply time", () =>
        { var t = Tracker(3, 3); Eq(6d, t.ActiveTime(20, 6, true)); });
        Test("dps 06: first event has zero duration and finite zero DPS", () =>
        { var m = Manager(); m.Accept(Done(1, 100), 5, 5); Eq(0d, m.ActiveDpsTime(1, 5)); Eq(0d, m.Dps(1, 5)); True(!double.IsNaN(m.Dps(1, 5))); });
        Test("dps 07: one burst stops denominator at idle timeout", () =>
        { var m = Manager(); m.Accept(Done(1, 600), 0, 0); Eq(1d, m.ActiveDpsTime(1, 1)); Eq(600d, m.Dps(1, 1)); Eq(3d, m.ActiveDpsTime(1, 3)); Eq(200d, m.Dps(1, 3)); Eq(6d, m.ActiveDpsTime(1, 10)); Eq(100d, m.Dps(1, 10)); });
        Test("dps 08: damage taken creates no activity", () =>
        { var m = Manager(); m.Accept(Taken(2, 500), 0, 0); Eq(0d, m.ActiveDpsTime(2, 10)); Eq(0d, m.Dps(2, 10)); });
        Test("dps 09: players have independent windows and signed IDs", () =>
        { var m = Manager(); m.Accept(Done(-451055642, 10), 0, 0); m.Accept(Done(-451055642, 10), 4, 4); m.Accept(Done(-451055642, 10), 8, 8); m.Accept(Done(2, 10), 8, 0); Eq(10d, m.ActiveDpsTime(-451055642, 10)); Eq(6d, m.ActiveDpsTime(2, 10)); });
        Test("dps 10: recovery freezes and later offensive event starts a new interval", () =>
        { var m = Manager(5, 30, 10); m.Accept(Done(1, 10), 0, 0); m.OnPlayerDied(1, 1); m.Update(5); Eq(5d, m.ActiveDpsTime(1, 20)); m.Accept(Done(1, 10), 20, 20); Eq(7d, m.ActiveDpsTime(1, 22)); });
        Test("dps 11: finished freezes at canonical finish", () =>
        { var m = Manager(5, 180, 10); m.Accept(Done(1, 10), 0, 0); m.Update(8); Eq(5d, m.ActiveDpsTime(1, 100)); Eq(2d, m.Dps(1, 100)); });
        Test("dps 12: new encounter clears old activity", () =>
        { var m = Manager(5, 180, 6); m.Accept(Done(1, 10), 0, 0); m.Update(5); m.Accept(Done(2, 10), 10, 10); Eq(0d, m.ActiveDpsTime(1, 12)); Eq(2d, m.ActiveDpsTime(2, 12)); });
        Test("dps 13: live reduction closes current window earlier", () =>
        { var s = new EncounterSettings(20, 180, 10); var m = Manager(s); m.Accept(Done(1, 10), 10, 10); s.Update(20, 180, 4); Eq(4d, m.ActiveDpsTime(1, 15)); });
        Test("dps 14: live increase extends a still-current window", () =>
        { var s = new EncounterSettings(20, 180, 4); var m = Manager(s); m.Accept(Done(1, 10), 10, 10); m.ActiveDpsTime(1, 13); s.Update(20, 180, 8); Eq(5d, m.ActiveDpsTime(1, 15)); });
        Test("dps 15: closed historical window is not rewritten", () =>
        { var s = new EncounterSettings(20, 180, 4); var m = Manager(s); m.Accept(Done(1, 10), 0, 0); Eq(4d, m.ActiveDpsTime(1, 5)); s.Update(20, 180, 8); Eq(4d, m.ActiveDpsTime(1, 10)); });
        Test("dps 16: compacted representation remains bounded", () =>
        { var t = new PlayerDpsActivityTracker(); for (int i = 0; i < 400; i++) { t.Add(i * 10, 1); t.ActiveTime(i * 10 + 2, 1, true); } True(t.RetainedClosedIntervalCount <= PlayerDpsActivityTracker.MaxRetainedClosedIntervals); Eq(400d, t.ActiveTime(5000, 1, true)); });
        Test("dps 17: custom DPS timeout bounds clamp", () =>
        { var low = new EncounterSettings(20, 180, .1); var high = new EncounterSettings(20, 180, 99); Eq(1d, low.DpsIdleTimeoutSeconds); Eq(20d, high.DpsIdleTimeoutSeconds); });
        return _passed;
    }

    private static PlayerDpsActivityTracker Tracker(params double[] events) { var t = new PlayerDpsActivityTracker(); foreach (double e in events) t.Add(e, 6); return t; }
    private static EncounterManager Manager(double soft = 20, double recovery = 180, double dps = 6) => Manager(new EncounterSettings(soft, recovery, dps));
    private static EncounterManager Manager(EncounterSettings settings) { _sequence = 0; return new EncounterManager(new CombatStatisticsAggregator(), settings); }
    private static DamageCommit Done(long player, float damage) => Commit(false, null, AttackerClass.Player, player, damage);
    private static DamageCommit Taken(long player, float damage) => Commit(true, player, AttackerClass.NPC, null, damage);
    private static DamageCommit Commit(bool victimPlayer, long? victimId, AttackerClass attacker, long? attackerId, float damage) =>
        new DamageCommit(new EventId(99, Epoch, ++_sequence), new DamageFacts(1, 1, victimPlayer, victimId, attacker, attackerId, 1, "Victim", "Attacker"), damage, DateTime.UtcNow.Ticks);
    private static void Test(string name, Action run) { run(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
