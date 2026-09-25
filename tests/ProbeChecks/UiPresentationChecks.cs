using System;
using System.Collections.Generic;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Snapshot;
using DiagnosticDamageProbe.UI;

internal static class UiPresentationChecks
{
    private static readonly Guid Epoch = new Guid("abcdef01-2345-6789-abcd-ef0123456789");
    private static int _passed;

    internal static int Run()
    {
        Test("ui 01: NoEncounter stays hidden", () => True(!Build(EncounterState.NoEncounter).ShouldShow));
        Test("ui 02: Active shows all rows", () => { var m = Build(EncounterState.Active, Row(1), Row(2)); True(m.ShouldShow); Eq(2, m.Rows.Count); });
        Test("ui 03: Recovery remains visible", () => { var m = Build(EncounterState.Recovery); True(m.ShouldShow); Eq("Recovery", m.StateText); });
        Test("ui 04: Finished remains visible with final rows", () => { var m = Build(EncounterState.Finished, Row(1, 50)); True(m.ShouldShow); Eq("50", m.Rows[0].DamageText); });
        Test("ui 05: damage sort descends with signed ID tie break", () =>
        { var m = Build(EncounterState.Active, Row(10, 100), Row(3, 300), Row(-7, 300)); Eq(-7L, m.Rows[0].PlayerId); Eq(3L, m.Rows[1].PlayerId); Eq(10L, m.Rows[2].PlayerId); });
        Test("ui 06: numbers use invariant display rounding", () =>
        { var m = Build(EncounterState.Active, new CombatSnapshotPlayer(1, "P", 1234.49f, 45.678, 320.6f)); Eq("1234", m.Rows[0].DamageText); Eq("45.7", m.Rows[0].DpsText); Eq("321", m.Rows[0].TakenText); Eq("Time: 41.0s", m.TimeText); });
        Test("ui 07: negative ID fallback is deterministic", () => Eq("Player -451055642", Build(EncounterState.Active, new CombatSnapshotPlayer(-451055642, "", 0, 0, 0)).Rows[0].NameText));
        Test("ui 08: Unicode names survive mapping", () =>
        { var m = Build(EncounterState.Active, Named(1, "Олаф Дурачок"), Named(2, "Nichka"), Named(3, "玩家")); Eq("Олаф Дурачок", m.Rows[0].NameText); Eq("Nichka", m.Rows[1].NameText); Eq("玩家", m.Rows[2].NameText); });
        Test("ui 09: presentation preserves long source name", () => { string name = new string('Ж', 64); Eq(name, Build(EncounterState.Active, Named(1, name)).Rows[0].NameText); });
        Test("ui 10: presenter rebuilds only for new epoch or sequence", () =>
        { var p = new CombatMeterPresenter(); var s = Snapshot(EncounterState.Active, 1, 4, Row(1)); True(p.TryBuild(s, out _)); True(!p.TryBuild(s, out _)); True(p.TryBuild(Snapshot(EncounterState.Active, 1, 5, Row(1)), out _)); });
        Test("ui 11: new encounter replaces prior presentation", () =>
        { var p = new CombatMeterPresenter(); p.TryBuild(Snapshot(EncounterState.Finished, 4, 1, Row(1, 500)), out _); p.TryBuild(Snapshot(EncounterState.Active, 5, 2, Row(2, 20)), out var m); Eq(5L, m.EncounterId); Eq(1, m.Rows.Count); Eq("20", m.Rows[0].DamageText); });
        Test("ui 12: presenter reset allows clean session render", () =>
        { var p = new CombatMeterPresenter(); var s = Snapshot(EncounterState.Active, 1, 1, Row(1)); True(p.TryBuild(s, out _)); p.Reset(); True(p.TryBuild(s, out _)); });
        Test("ui 13: config and local toggle control visibility", () =>
        { var v = new CombatMeterVisibilityState(); True(!v.ShouldShow(false, true)); True(v.ShouldShow(true, true)); v.Toggle(); True(!v.ShouldShow(true, true)); True(!v.ShouldShow(true, false)); v.Toggle(); True(v.ShouldShow(true, true)); });
        return _passed;
    }

    private static CombatMeterViewModel Build(EncounterState state, params CombatSnapshotPlayer[] rows) => CombatMeterPresenter.Build(Snapshot(state, state == EncounterState.NoEncounter ? 0 : 1, 1, rows));
    private static CombatSnapshot Snapshot(EncounterState state, long encounterId, long sequence, params CombatSnapshotPlayer[] rows) =>
        new CombatSnapshot(101, Epoch, sequence, encounterId, state, state == EncounterState.NoEncounter ? 0 : 41, rows);
    private static CombatSnapshotPlayer Row(long id, float damage = 0) => new CombatSnapshotPlayer(id, "P" + id, damage, 0, 0);
    private static CombatSnapshotPlayer Named(long id, string name) => new CombatSnapshotPlayer(id, name, 0, 0, 0);
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
