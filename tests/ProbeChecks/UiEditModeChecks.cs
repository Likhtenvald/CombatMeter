using System;
using DiagnosticDamageProbe.UI;

internal static class UiEditModeChecks
{
    private static int _passed;

    internal static int Run()
    {
        Test("edit 01: visible preference survives enter and toggle exit", () =>
        { var s = new CombatMeterEditModeState(); True(s.ShouldShow(true, false, true)); True(s.Enter()); True(s.ShouldShow(true, false, true)); True(s.Exit()); True(s.ShouldShow(true, false, true)); });
        Test("edit 02: hidden meter is temporarily forced visible", () =>
        { var s = new CombatMeterEditModeState(); True(!s.ShouldShow(true, true, true)); s.Enter(); True(s.ShouldShow(true, true, true)); s.Exit(); True(!s.ShouldShow(true, true, true)); });
        Test("edit 03: NoEncounter preview is forced visible", () =>
        { var s = new CombatMeterEditModeState(); True(!s.ShouldShow(true, false, false)); s.Enter(); True(s.ShouldShow(true, false, false)); s.Exit(); True(!s.ShouldShow(true, false, false)); });
        Test("edit 04: drag cannot begin in normal mode", () =>
        { var s = new CombatMeterEditModeState(); True(!s.BeginDrag()); True(!s.IsDragging); });
        Test("edit 05: drag begins only while editing", () =>
        { var s = new CombatMeterEditModeState(); s.Enter(); True(s.BeginDrag()); True(s.IsDragging); True(s.EndDrag()); True(!s.IsDragging); });
        Test("edit 06: exit during drag ends drag", () =>
        { var s = new CombatMeterEditModeState(); s.Enter(); s.BeginDrag(); True(s.Exit()); True(!s.IsEditing); True(!s.IsDragging); });
        Test("edit 07: reset does not alter edit state", () =>
        { var s = new CombatMeterEditModeState(); s.Enter(); UiPoint p = CombatMeterLayout.DefaultPosition(); Eq(24f, p.X); Eq(-150f, p.Y); True(s.IsEditing); });
        Test("edit 08: F8 visibility toggle is rejected while editing", () =>
        { var s = new CombatMeterEditModeState(); True(s.AcceptVisibilityToggle); s.Enter(); True(!s.AcceptVisibilityToggle); s.Exit(); True(s.AcceptVisibilityToggle); });
        Test("edit 09: lifecycle or modal exit returns normal state", () =>
        { var lifecycle = new CombatMeterEditModeState(); lifecycle.Enter(); lifecycle.Exit(); True(!lifecycle.IsEditing); var modal = new CombatMeterEditModeState(); modal.Enter(); modal.Exit(); True(!modal.IsEditing); });
        Test("edit 10: preview is isolated deterministic presentation", () =>
        { CombatMeterViewModel p = CombatMeterPreview.Build(); True(p.ShouldShow); Eq(0L, p.EncounterId); Eq("Edit Preview", p.StateText); Eq(2, p.Rows.Count); Eq("Preview Player 1", p.Rows[0].NameText); Eq("66.7%", p.Rows[0].PercentText); });
        Test("edit 11: duplicate enter and exit are idempotent", () =>
        { var s = new CombatMeterEditModeState(); True(s.Enter()); True(!s.Enter()); True(s.Exit()); True(!s.Exit()); });
        return _passed;
    }

    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
