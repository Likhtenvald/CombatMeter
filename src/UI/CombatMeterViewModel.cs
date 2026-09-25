using System;
using System.Collections.Generic;
using System.Globalization;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Snapshot;

namespace DiagnosticDamageProbe.UI;

internal sealed class CombatMeterRowModel
{
    internal long PlayerId { get; }
    internal uint PlayerColorRgb => PlayerColorPalette.Resolve(PlayerId);
    internal string NameText { get; }
    internal string DamageText { get; }
    internal string PercentText { get; }
    internal string DpsText { get; }
    internal string TakenText { get; }
    internal double DamageContribution { get; }

    internal CombatMeterRowModel(long playerId, string name, string damage, string percent, string dps, string taken, double contribution)
    { PlayerId = playerId; NameText = name; DamageText = damage; PercentText = percent; DpsText = dps; TakenText = taken; DamageContribution = contribution; }
}

internal sealed class CombatMeterViewModel
{
    internal long EncounterId { get; }
    internal string StateText { get; }
    internal string TimeText { get; }
    internal bool ShouldShow { get; }
    internal IReadOnlyList<CombatMeterRowModel> Rows { get; }

    internal CombatMeterViewModel(long encounterId, string state, string time, bool show, List<CombatMeterRowModel> rows)
    { EncounterId = encounterId; StateText = state; TimeText = time; ShouldShow = show; Rows = rows.AsReadOnly(); }
}

internal sealed class CombatMeterPresenter
{
    private Guid _renderedEpoch;
    private long _renderedSequence;
    internal void Reset() { _renderedEpoch = Guid.Empty; _renderedSequence = 0; }

    internal bool TryBuild(CombatSnapshot snapshot, out CombatMeterViewModel model)
    {
        model = null;
        if (snapshot == null) { Reset(); return false; }
        if (snapshot.SnapshotEpoch == _renderedEpoch && snapshot.Sequence == _renderedSequence) return false;
        model = Build(snapshot);
        _renderedEpoch = snapshot.SnapshotEpoch;
        _renderedSequence = snapshot.Sequence;
        return true;
    }

    internal static CombatMeterViewModel Build(CombatSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var source = new List<CombatSnapshotPlayer>(snapshot.Players);
        double totalDamage = 0d;
        foreach (CombatSnapshotPlayer player in source)
            if (FiniteNonnegative(player.DamageDone)) totalDamage += player.DamageDone;
        source.Sort((a, b) =>
        {
            int byDamage = b.DamageDone.CompareTo(a.DamageDone);
            return byDamage != 0 ? byDamage : a.PlayerId.CompareTo(b.PlayerId);
        });
        var rows = new List<CombatMeterRowModel>(source.Count);
        foreach (CombatSnapshotPlayer player in source)
        {
            double damage = FiniteNonnegative(player.DamageDone) ? player.DamageDone : 0d;
            double dps = FiniteNonnegative(player.Dps) ? player.Dps : 0d;
            double taken = FiniteNonnegative(player.DamageTaken) ? player.DamageTaken : 0d;
            double contribution = totalDamage > 0d ? damage / totalDamage : 0d;
            if (!FiniteNonnegative(contribution)) contribution = 0d;
            contribution = Math.Max(0d, Math.Min(1d, contribution));
            string name = string.IsNullOrEmpty(player.DisplayName)
                ? "Player " + player.PlayerId.ToString(CultureInfo.InvariantCulture)
                : player.DisplayName;
            rows.Add(new CombatMeterRowModel(player.PlayerId, name,
                damage.ToString("0", CultureInfo.InvariantCulture),
                (contribution * 100d).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                dps.ToString("0.0", CultureInfo.InvariantCulture),
                taken.ToString("0", CultureInfo.InvariantCulture), contribution));
        }
        return new CombatMeterViewModel(snapshot.EncounterId, snapshot.EncounterState.ToString(),
            "Time: " + snapshot.EncounterElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s",
            snapshot.EncounterState != EncounterState.NoEncounter, rows);
    }
    private static bool FiniteNonnegative(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;
}

internal sealed class CombatMeterVisibilityState
{
    internal bool UserHidden { get; private set; }
    internal void Toggle() => UserHidden = !UserHidden;
    internal bool ShouldShow(bool enabled, bool dataShouldShow) => enabled && !UserHidden && dataShouldShow;
}

internal sealed class CombatMeterEditModeState
{
    internal bool IsEditing { get; private set; }
    internal bool IsDragging { get; private set; }
    internal bool Enter() { if (IsEditing) return false; IsEditing = true; return true; }
    internal bool Exit() { if (!IsEditing) return false; IsEditing = false; IsDragging = false; return true; }
    internal bool BeginDrag() { if (!IsEditing || IsDragging) return false; IsDragging = true; return true; }
    internal bool EndDrag() { if (!IsDragging) return false; IsDragging = false; return true; }
    internal bool ShouldShow(bool enabled, bool userHidden, bool snapshotShouldShow) =>
        enabled && (IsEditing || (!userHidden && snapshotShouldShow));
    internal bool AcceptVisibilityToggle => !IsEditing;
}

internal static class CombatMeterPreview
{
    internal static CombatMeterViewModel Build()
    {
        var rows = new List<CombatMeterRowModel>
        {
            new CombatMeterRowModel(1, "Preview Player 1", "1000", "66.7%", "50.0", "200", 2d / 3d),
            new CombatMeterRowModel(2, "Preview Player 2", "500", "33.3%", "25.0", "350", 1d / 3d)
        };
        return new CombatMeterViewModel(0, "Edit Preview", "Time: 0.0s", true, rows);
    }
}
