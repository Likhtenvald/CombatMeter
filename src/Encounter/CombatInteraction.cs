using System;
using System.Collections.Generic;
using DiagnosticDamageProbe.Attribution;
using DiagnosticDamageProbe.Statistics;
using DiagnosticDamageProbe.Transport;

namespace DiagnosticDamageProbe.Encounter;

// Player identity and PvE object identity are separate namespaces. No peer ownership or position.
internal readonly struct CombatNode : IEquatable<CombatNode>
{
    internal bool IsPlayer { get; }
    internal long Id { get; }
    internal uint ObjectId { get; }
    private CombatNode(bool player, long id, uint objectId) { IsPlayer = player; Id = id; ObjectId = objectId; }
    internal static CombatNode Player(long id) => new CombatNode(true, id, 0);
    internal static CombatNode Combatant(long creator, uint objectId) => new CombatNode(false, creator, objectId);
    public bool Equals(CombatNode other) => IsPlayer == other.IsPlayer && Id == other.Id && ObjectId == other.ObjectId;
    public override bool Equals(object other) => other is CombatNode node && Equals(node);
    public override int GetHashCode() => Id.GetHashCode() ^ ObjectId.GetHashCode() ^ IsPlayer.GetHashCode();
}

internal static class CombatInteraction
{
    // Classification remains the existing policy. World objects never reach this boundary:
    // production commits originate in Character.ApplyDamage, not destructible-object hooks.
    internal static HashSet<CombatNode> Nodes(AttributedDamageEvent damage)
    {
        var nodes = new HashSet<CombatNode>();
        DamageFacts facts = damage.Commit.Facts;
        if (damage.ApplyVanillaClassification)
        {
            PveClassification classification = CombatStatisticsAggregator.Classify(damage.Commit);
            if (!classification.IsRelevant) return nodes;
            nodes.Add(CombatNode.Player(classification.PlayerId));
            if (classification.Kind == PveStatisticKind.DamageDone)
                nodes.Add(CombatNode.Combatant(facts.VictimCreator, facts.VictimObject));
            else if (facts.AttackerCreator != 0 && facts.AttackerObject != 0)
                nodes.Add(CombatNode.Combatant(facts.AttackerCreator, facts.AttackerObject));
            // Missing attacker identity: player-only membership, no fabricated node/edge.
        }
        else if (!facts.VictimIsPlayer)
        {
            foreach (DamagePortion portion in damage.DamageDone)
                if (portion.PlayerId.HasValue && portion.Damage > 0f)
                    nodes.Add(CombatNode.Player(portion.PlayerId.Value));
            if (nodes.Count > 0) nodes.Add(CombatNode.Combatant(facts.VictimCreator, facts.VictimObject));
        }
        return nodes;
    }
}
