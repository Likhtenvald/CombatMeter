namespace DiagnosticDamageProbe.Statistics;

// Pure session-scoped state. PlayerID is the identity; DisplayName is metadata only.
internal sealed class PlayerCombatStatistics
{
    internal long PlayerId { get; }
    internal string DisplayName { get; private set; }
    internal float DamageDone { get; private set; }
    internal float DamageTaken { get; private set; }

    internal PlayerCombatStatistics(long playerId, string displayName)
    {
        PlayerId = playerId;
        DisplayName = displayName ?? "";
    }

    internal void AddDamageDone(float amount, string displayName)
    {
        UpdateDisplayName(displayName);
        DamageDone += amount;
    }

    internal void AddDamageTaken(float amount, string displayName)
    {
        UpdateDisplayName(displayName);
        DamageTaken += amount;
    }

    private void UpdateDisplayName(string displayName)
    {
        if (!string.IsNullOrEmpty(displayName)) DisplayName = displayName;
    }
}
