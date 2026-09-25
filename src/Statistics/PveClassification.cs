namespace DiagnosticDamageProbe.Statistics;

internal enum PveStatisticKind
{
    Ignored,
    DamageDone,
    DamageTaken
}

// The single PvE classification result shared by statistics and encounter logic.
internal readonly struct PveClassification
{
    internal readonly PveStatisticKind Kind;
    internal readonly long PlayerId;
    internal readonly string DisplayName;
    internal bool IsRelevant => Kind != PveStatisticKind.Ignored;

    internal PveClassification(PveStatisticKind kind, long playerId, string displayName)
    {
        Kind = kind;
        PlayerId = playerId;
        DisplayName = displayName ?? "";
    }

    internal static PveClassification Ignored => new PveClassification(PveStatisticKind.Ignored, 0, "");
}
