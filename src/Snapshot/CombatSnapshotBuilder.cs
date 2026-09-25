using System;
using System.Collections.Generic;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Statistics;

namespace DiagnosticDamageProbe.Snapshot;

internal static class CombatSnapshotBuilder
{
    internal const int MaxPlayers = 64;
    internal const int MaxDisplayNameLength = 64;

    internal static CombatSnapshot Build(long hostPeer, Guid epoch, long sequence, EncounterManager encounter, double now)
    {
        if (hostPeer == 0 || epoch == Guid.Empty || sequence <= 0 || encounter == null) throw new ArgumentException("Invalid snapshot identity");
        double elapsed = encounter.Duration(now);
        if (!FiniteNonnegative(elapsed)) throw new InvalidOperationException("Invalid elapsed");
        var rows = new List<CombatSnapshotPlayer>();
        if (encounter.State != EncounterState.NoEncounter)
        {
            foreach (PlayerCombatStatistics player in encounter.Statistics.Players)
            {
                if (player.PlayerId == 0) continue;
                double dps = encounter.Dps(player.PlayerId, now);
                if (!FiniteNonnegative(player.DamageDone) || !FiniteNonnegative(player.DamageTaken) || !FiniteNonnegative(dps))
                    throw new InvalidOperationException("Invalid combat total");
                rows.Add(new CombatSnapshotPlayer(player.PlayerId, LimitName(player.DisplayName), player.DamageDone, dps, player.DamageTaken));
            }
        }
        if (rows.Count > MaxPlayers) throw new InvalidOperationException("Too many players");
        rows.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
        return new CombatSnapshot(hostPeer, epoch, sequence, encounter.EncounterId, encounter.State, elapsed, rows);
    }

    private static string LimitName(string value)
    {
        value ??= ""; int length = Math.Min(value.Length, MaxDisplayNameLength);
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value.Substring(0, length);
    }
    internal static bool FiniteNonnegative(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;
}
