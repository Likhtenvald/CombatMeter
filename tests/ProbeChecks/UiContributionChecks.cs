using System;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Snapshot;
using DiagnosticDamageProbe.UI;

internal static class UiContributionChecks
{
    private static readonly Guid Epoch = new Guid("10203040-5060-7080-90a0-b0c0d0e0f001");
    private static int _passed;

    internal static int Run()
    {
        Test("contribution 01: 60 30 10 use total party damage", () =>
        { var m = Build(Row(1, 60), Row(2, 30), Row(3, 10)); RowEquals(m.Rows[0], .6, "60.0%"); RowEquals(m.Rows[1], .3, "30.0%"); RowEquals(m.Rows[2], .1, "10.0%"); });
        Test("contribution 02: equal damage splits evenly", () =>
        { var m = Build(Row(1, 100), Row(2, 100)); RowEquals(m.Rows[0], .5, "50.0%"); RowEquals(m.Rows[1], .5, "50.0%"); });
        Test("contribution 03: one player owns all contribution", () => RowEquals(Build(Row(1, 100)).Rows[0], 1, "100.0%"));
        Test("contribution 04: all zero is finite zero", () =>
        { var m = Build(Row(1, 0), Row(2, 0)); foreach (var r in m.Rows) { RowEquals(r, 0, "0.0%"); True(!double.IsNaN(r.DamageContribution)); } });
        Test("contribution 05: mixed zero gives 100 and zero", () =>
        { var m = Build(Row(1, 100), Row(2, 0)); RowEquals(m.Rows[0], 1, "100.0%"); RowEquals(m.Rows[1], 0, "0.0%"); });
        Test("contribution 06: decimal shares format independently", () =>
        { var m = Build(Row(1, 1), Row(2, 2)); Eq("66.7%", m.Rows[0].PercentText); Near(2d / 3d, m.Rows[0].DamageContribution); Eq("33.3%", m.Rows[1].PercentText); Near(1d / 3d, m.Rows[1].DamageContribution); });
        Test("contribution 07: display rounding is not forced to 100", () =>
        { var m = Build(Row(1, 1), Row(2, 1), Row(3, 1)); Eq("33.3%", m.Rows[0].PercentText); Eq("33.3%", m.Rows[1].PercentText); Eq("33.3%", m.Rows[2].PercentText); });
        Test("contribution 08: sorting and shares remain attached", () =>
        { var m = Build(Row(10, 100), Row(20, 300), Row(30, 200)); Eq(20L, m.Rows[0].PlayerId); Eq("50.0%", m.Rows[0].PercentText); Eq(30L, m.Rows[1].PlayerId); Eq("33.3%", m.Rows[1].PercentText); Eq(10L, m.Rows[2].PlayerId); Eq("16.7%", m.Rows[2].PercentText); });
        Test("contribution 09: signed ID tie break is preserved", () =>
        { var m = Build(Row(-451055642, 100), Row(2725965180, 100)); Eq(-451055642L, m.Rows[0].PlayerId); Eq("50.0%", m.Rows[0].PercentText); Eq(2725965180L, m.Rows[1].PlayerId); });
        Test("contribution 10: recovery DPS does not affect share", () =>
        { var a = new CombatSnapshotPlayer(1, "A", 75, 100, 0); var b = new CombatSnapshotPlayer(2, "B", 25, 1, 0); var m = Build(EncounterState.Recovery, a, b); Eq("75.0%", m.Rows[0].PercentText); Eq("25.0%", m.Rows[1].PercentText); });
        Test("contribution 11: new encounter uses only new snapshot", () =>
        { var old = Build(Row(1, 900), Row(2, 100)); var next = Build(Row(2, 20)); Eq("90.0%", old.Rows[0].PercentText); Eq(1, next.Rows.Count); Eq("100.0%", next.Rows[0].PercentText); });
        Test("contribution 12: invalid presentation input sanitizes locally", () =>
        { var m = Build(new CombatSnapshotPlayer(1, "A", float.NaN, double.PositiveInfinity, -1), Row(2, 10)); Eq("0", m.Rows[1].DamageText); Eq("0.0", m.Rows[1].DpsText); Eq("0", m.Rows[1].TakenText); RowEquals(m.Rows[1], 0, "0.0%"); });
        Test("contribution 13: source snapshot remains immutable", () =>
        { var row = Row(1, 42); var snapshot = Snapshot(EncounterState.Active, row); CombatMeterPresenter.Build(snapshot); Eq(42f, row.DamageDone); Eq(42f, snapshot.Players[0].DamageDone); });
        return _passed;
    }

    private static CombatMeterViewModel Build(params CombatSnapshotPlayer[] rows) => Build(EncounterState.Active, rows);
    private static CombatMeterViewModel Build(EncounterState state, params CombatSnapshotPlayer[] rows) => CombatMeterPresenter.Build(Snapshot(state, rows));
    private static CombatSnapshot Snapshot(EncounterState state, params CombatSnapshotPlayer[] rows) => new CombatSnapshot(101, Epoch, 1, 1, state, 10, rows);
    private static CombatSnapshotPlayer Row(long id, float damage) => new CombatSnapshotPlayer(id, "P" + id, damage, 0, 0);
    private static void RowEquals(CombatMeterRowModel row, double contribution, string text) { Near(contribution, row.DamageContribution); Eq(text, row.PercentText); }
    private static void Near(double expected, double actual) { if (Math.Abs(expected - actual) > 0.000001) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
