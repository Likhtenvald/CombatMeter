using System;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Snapshot;
using DiagnosticDamageProbe.UI;

internal static class PlayerColorChecks
{
    private static int _passed;
    private static readonly Guid Epoch = new Guid("10203040-5060-7080-90a0-b0c0d0e0f001");
    internal static int Run()
    {
        Test("same ID resolves repeatedly", () => { for (int i = 0; i < 100; i++) Eq(0xFFAD5Cu, PlayerColorPalette.Resolve(42)); });
        Test("positive ID", () => Eq(2, PlayerColorPalette.ResolveIndex(2725965180)));
        Test("negative ID", () => Eq(1, PlayerColorPalette.ResolveIndex(-451055642)));
        Test("minimum signed ID", () => Eq(7, PlayerColorPalette.ResolveIndex(long.MinValue)));
        Test("fixed independently calculated vectors", () =>
        {
            long[] ids = { 1, 2, 3, 42, 2725965180, -451055642, -1, long.MinValue, long.MaxValue };
            int[] indices = { 5, 10, 9, 1, 2, 1, 8, 7, 3 };
            for (int i = 0; i < ids.Length; i++) Eq(indices[i], PlayerColorPalette.ResolveIndex(ids[i]));
        });
        Test("call order is irrelevant", () =>
        {
            long[] ids = { 42, -1, long.MinValue, 3 };
            var colors = Array.ConvertAll(ids, PlayerColorPalette.Resolve);
            for (int i = ids.Length - 1; i >= 0; i--) Eq(colors[i], PlayerColorPalette.Resolve(ids[i]));
        });
        Test("display name does not affect row color", () => Eq(Build(1, Row(42, "Alice")).Rows[0].PlayerColorRgb, Build(1, Row(42, "Bob")).Rows[0].PlayerColorRgb));
        Test("indices stay in bounds across signed bit patterns", () =>
        {
            ulong bits = 0;
            for (int i = 0; i < 100000; i++)
            {
                int index = PlayerColorPalette.ResolveIndex(unchecked((long)bits));
                if (index < 0 || index >= PlayerColorPalette.Count) throw new Exception("Index out of bounds");
                bits = unchecked(bits + 0x9E3779B97F4A7C15UL);
            }
        });
        Test("presented rows resolve color from their PlayerID", () =>
        {
            var model = Build(1, Row(1, "same", 10), Row(-1, "same", 20));
            Eq(-1L, model.Rows[0].PlayerId); Eq(0xB4D66Bu, model.Rows[0].PlayerColorRgb);
            Eq(1L, model.Rows[1].PlayerId); Eq(0xF07888u, model.Rows[1].PlayerColorRgb);
        });
        Test("snapshot sequence and HUD rebuild preserve color", () =>
        {
            var presenter = new CombatMeterPresenter();
            presenter.TryBuild(Snapshot(1, Row(42, "A")), out var first);
            presenter.TryBuild(Snapshot(2, Row(42, "A")), out var next);
            Eq(first.Rows[0].PlayerColorRgb, next.Rows[0].PlayerColorRgb);
            presenter.Reset(); presenter.TryBuild(Snapshot(2, Row(42, "A")), out var rebuilt);
            Eq(first.Rows[0].PlayerColorRgb, rebuilt.Rows[0].PlayerColorRgb);
        });
        Test("row order and damage changes preserve color", () =>
        {
            var first = Build(1, Row(1, "A", 20), Row(2, "B", 10));
            var next = Build(2, Row(2, "B", 30), Row(1, "A", 20));
            Eq(first.Rows[0].PlayerColorRgb, next.Rows[1].PlayerColorRgb);
            Eq(first.Rows[1].PlayerColorRgb, next.Rows[0].PlayerColorRgb);
        });
        Test("palette has 12 distinct RGB entries without alpha", () =>
        {
            Eq(12, PlayerColorPalette.Count);
            var colors = new System.Collections.Generic.HashSet<uint>();
            for (long id = 1; id < 1000; id++)
            {
                uint rgb = PlayerColorPalette.Resolve(id);
                if (rgb > 0xFFFFFF) throw new Exception("Unexpected alpha");
                colors.Add(rgb);
            }
            Eq(12, colors.Count);
        });
        return _passed;
    }
    private static CombatSnapshotPlayer Row(long id, string name, float damage = 10) => new CombatSnapshotPlayer(id, name, damage, 0, 0);
    private static CombatSnapshot Snapshot(long sequence, params CombatSnapshotPlayer[] rows) => new CombatSnapshot(101, Epoch, sequence, 1, EncounterState.Active, 10, rows);
    private static CombatMeterViewModel Build(long sequence, params CombatSnapshotPlayer[] rows) => CombatMeterPresenter.Build(Snapshot(sequence, rows));
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS player colors: " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
}
