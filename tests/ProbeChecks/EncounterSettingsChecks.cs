using System;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;

internal static class EncounterSettingsChecks
{
    private static readonly Guid Epoch = new Guid("90123456-789a-bcde-f012-3456789abcde");
    private static int _passed;
    private static long _sequence;

    internal static int Run()
    {
        Test("settings 01: defaults are 20 and 180 seconds", () =>
        { var s = new EncounterSettings(); Eq(20d, s.SoftTimeoutSeconds); Eq(180d, s.RecoveryTimeoutSeconds); Eq(6d, s.DpsIdleTimeoutSeconds); });
        Test("settings 02: documented bounds clamp", () =>
        { var s = new EncounterSettings(1, 10); Eq(5d, s.SoftTimeoutSeconds); Eq(30d, s.RecoveryTimeoutSeconds); s.Update(100, 1000); Eq(60d, s.SoftTimeoutSeconds); Eq(600d, s.RecoveryTimeoutSeconds); });
        Test("settings 03: invalid values fall back to defaults", () =>
        { var s = new EncounterSettings(double.NaN, double.PositiveInfinity); Eq(20d, s.SoftTimeoutSeconds); Eq(180d, s.RecoveryTimeoutSeconds); s.Update(0, -1); Eq(20d, s.SoftTimeoutSeconds); Eq(180d, s.RecoveryTimeoutSeconds); });
        Test("settings 04: default timeout minus epsilon stays Active", () =>
        { var m = Active(new EncounterSettings()); m.Update(19.999); Eq(EncounterState.Active, m.State); });
        Test("settings 05: default timeout fires at exact boundary", () =>
        { var m = Active(new EncounterSettings()); m.Update(20); Eq(EncounterState.Finished, m.State); Eq(20d, m.EndTime); });
        Test("settings 06: default timeout plus epsilon is Finished", () =>
        { var m = Active(new EncounterSettings()); m.Update(20.001); Eq(EncounterState.Finished, m.State); });
        Test("settings 07: pending ticket enters Recovery at boundary", () =>
        { var m = Active(new EncounterSettings()); m.OnPlayerDied(1, 1); m.Update(20); Eq(EncounterState.Recovery, m.State); });
        Test("settings 08: custom short timeout uses 5 second boundary", () =>
        { var m = Active(new EncounterSettings(5, 180)); m.Update(4.999); Eq(EncounterState.Active, m.State); m.Update(5); Eq(EncounterState.Finished, m.State); });
        Test("settings 09: custom long timeout remains Active after 20 seconds", () =>
        { var m = Active(new EncounterSettings(60, 180)); m.Update(20); Eq(EncounterState.Active, m.State); m.Update(60); Eq(EncounterState.Finished, m.State); });
        Test("settings 10: custom recovery expiry has epsilon exact and plus boundaries", () =>
        { var m = Active(new EncounterSettings(5, 30)); m.OnPlayerDied(1, 1); m.Update(5); m.Update(30.999); Eq(EncounterState.Recovery, m.State); m.Update(31); Eq(EncounterState.Finished, m.State); Eq(31d, m.EndTime); });
        Test("settings 11: live soft change applies on next evaluation", () =>
        { var s = new EncounterSettings(20, 180); var m = Active(s); m.Update(15); Eq(EncounterState.Active, m.State); s.Update(10, 180); m.Update(15); Eq(EncounterState.Finished, m.State); Eq(10d, m.EndTime); });
        Test("settings 12: live recovery change updates an existing ticket", () =>
        { var s = new EncounterSettings(20, 180); var m = Active(s); m.OnPlayerDied(1, 1); m.Update(20); Eq(EncounterState.Recovery, m.State); s.Update(20, 30); m.Update(30.999); Eq(EncounterState.Recovery, m.State); m.Update(31); Eq(EncounterState.Finished, m.State); Eq(31d, m.EndTime); });
        return _passed;
    }

    private static EncounterManager Active(EncounterSettings settings)
    { _sequence = 0; var m = new EncounterManager(new CombatStatisticsAggregator(), settings); m.Accept(Done(), 0); return m; }
    private static DamageCommit Done() => new DamageCommit(new EventId(99, Epoch, ++_sequence),
        new DamageFacts(1, 1, false, null, AttackerClass.Player, 1, 1, "Target", "Player"), 1, DateTime.UtcNow.Ticks);
    private static void Test(string name, Action run) { run(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
}
