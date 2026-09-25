using System;
using DiagnosticDamageProbe.UI;

internal static class UiLayoutChecks
{
    private static int _passed;
    private static readonly UiSize Canvas = new UiSize(1920, 1080);
    private static readonly UiSize Window = new UiSize(480, 200);

    internal static int Run()
    {
        Test("layout 01: inside position remains unchanged", () => Point(200, -200, Clamp(200, -200)));
        Test("layout 02: left edge clamps with margin", () => Point(10, -200, Clamp(-500, -200)));
        Test("layout 03: right edge clamps with margin", () => Point(1430, -200, Clamp(3000, -200)));
        Test("layout 04: top edge clamps with margin", () => Point(200, -10, Clamp(200, 500)));
        Test("layout 05: bottom edge clamps with margin", () => Point(200, -870, Clamp(200, -3000)));
        Test("layout 06: smaller resolution reclamps saved position", () =>
        { UiPoint p = CombatMeterLayout.Clamp(new UiPoint(1800, -1200), Window, new UiSize(1280, 720), 1); Point(790, -510, p); });
        Test("layout 07: scale bounds sanitize", () =>
        { Eq(0.5f, CombatMeterLayout.SanitizeScale(0.1f)); Eq(2f, CombatMeterLayout.SanitizeScale(3f)); Eq(1f, CombatMeterLayout.SanitizeScale(float.NaN)); });
        Test("layout 08: width bounds sanitize", () =>
        { Eq(350f, CombatMeterLayout.SanitizeWidth(10f)); Eq(800f, CombatMeterLayout.SanitizeWidth(900f)); Eq(480f, CombatMeterLayout.SanitizeWidth(float.PositiveInfinity)); });
        Test("layout 09: opacity bounds sanitize", () =>
        { Eq(0f, CombatMeterLayout.SanitizeOpacity(-1f)); Eq(1f, CombatMeterLayout.SanitizeOpacity(4f)); Eq(0.65f, CombatMeterLayout.SanitizeOpacity(float.NaN)); });
        Test("layout 10: persisted valid position restores exactly", () =>
        { UiPoint saved = Clamp(321, -456); UiPoint restored = CombatMeterLayout.Clamp(saved, Window, Canvas, 1); Point(saved.X, saved.Y, restored); });
        Test("layout 11: reset returns safe default", () =>
        { UiPoint d = CombatMeterLayout.DefaultPosition(); Point(24, -150, d); Point(24, -150, CombatMeterLayout.Clamp(d, Window, Canvas, 1)); });
        Test("layout 12: scale participates in right and bottom clamp", () =>
        { UiPoint p = CombatMeterLayout.Clamp(new UiPoint(1800, -1000), Window, Canvas, 2); Point(950, -670, p); });
        Test("layout 13: oversized window keeps its header reachable", () =>
        { UiPoint p = CombatMeterLayout.Clamp(new UiPoint(5000, -5000), new UiSize(800, 2000), new UiSize(1280, 720), 2); Point(10, -10, p); });
        return _passed;
    }

    private static UiPoint Clamp(float x, float y) => CombatMeterLayout.Clamp(new UiPoint(x, y), Window, Canvas, 1);
    private static void Point(float x, float y, UiPoint p) { Eq(x, p.X); Eq(y, p.Y); }
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
}
