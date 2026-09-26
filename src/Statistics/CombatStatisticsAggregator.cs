using System.Collections.Generic;
using DiagnosticDamageProbe.Transport;

namespace DiagnosticDamageProbe.Statistics;

// Receives only commits that already passed the host CommitAcceptor.
// It performs PvE classification, not validation, deduplication or vanilla damage calculation.
internal sealed class CombatStatisticsAggregator
{
    private readonly Dictionary<long, PlayerCombatStatistics> _players = new Dictionary<long, PlayerCombatStatistics>();
    internal int Count => _players.Count;
    internal IEnumerable<PlayerCombatStatistics> Players => _players.Values;

    internal PveClassification Accept(DamageCommit commit)
    {
        PveClassification classification = Classify(commit);
        Apply(classification, commit.EffectiveHpLoss);
        return classification;
    }

    internal static PveClassification Classify(DamageCommit commit)
    {
        DamageFacts facts = commit.Facts;
        if (IsNonCombatEnvironmentalHit(facts.HitType)) return PveClassification.Ignored;
        if (facts.VictimIsPlayer && facts.Attacker == AttackerClass.Player)
            return PveClassification.Ignored;
        if (!facts.VictimIsPlayer && facts.Attacker == AttackerClass.Player && facts.AttackerPlayerId.HasValue)
            return new PveClassification(PveStatisticKind.DamageDone, facts.AttackerPlayerId.Value, facts.AttackerName);
        if (facts.VictimIsPlayer && facts.VictimPlayerId.HasValue && facts.Attacker != AttackerClass.Player)
            return new PveClassification(PveStatisticKind.DamageTaken, facts.VictimPlayerId.Value, facts.VictimName);
        return PveClassification.Ignored;
    }

    // Canonical HitData.HitType : byte from the referenced Valheim assembly:
    // Fall=3, Drowning=4, Smoke=9. Keep this pure domain boundary free of Unity types.
    // Explicit policy only: None/unresolved, combat DoT, Tree and Incinerator are not excluded.
    private enum NonCombatEnvironmentalHit : byte { Fall = 3, Drowning = 4, Smoke = 9 }
    private static bool IsNonCombatEnvironmentalHit(byte hitType) =>
        hitType == (byte)NonCombatEnvironmentalHit.Fall ||
        hitType == (byte)NonCombatEnvironmentalHit.Drowning ||
        hitType == (byte)NonCombatEnvironmentalHit.Smoke;
    internal void Apply(PveClassification classification, float effectiveHpLoss)
    {
        if (classification.Kind == PveStatisticKind.DamageDone)
            GetOrCreate(classification.PlayerId, classification.DisplayName)
                .AddDamageDone(effectiveHpLoss, classification.DisplayName);
        else if (classification.Kind == PveStatisticKind.DamageTaken)
            GetOrCreate(classification.PlayerId, classification.DisplayName)
                .AddDamageTaken(effectiveHpLoss, classification.DisplayName);
    }

    internal bool TryGet(long playerId, out PlayerCombatStatistics statistics) =>
        _players.TryGetValue(playerId, out statistics);

    internal void MoveDisjointFrom(CombatStatisticsAggregator source)
    {
        foreach (long player in source._players.Keys)
            if (_players.ContainsKey(player)) throw new System.InvalidOperationException("Cluster statistics overlap");
        foreach (var pair in source._players) _players.Add(pair.Key, pair.Value);
        source._players.Clear();
    }
    internal void Reset() => _players.Clear();

    private PlayerCombatStatistics GetOrCreate(long playerId, string displayName)
    {
        if (!_players.TryGetValue(playerId, out PlayerCombatStatistics statistics))
        {
            statistics = new PlayerCombatStatistics(playerId, displayName);
            _players.Add(playerId, statistics);
        }
        return statistics;
    }
}
