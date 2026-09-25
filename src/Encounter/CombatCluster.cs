using System.Collections.Generic;
using DiagnosticDamageProbe.Statistics;

namespace DiagnosticDamageProbe.Encounter;

internal sealed class CombatCluster
{
    private readonly HashSet<CombatNode> _members = new HashSet<CombatNode>();
    internal long Id { get; }
    internal long? MergedIntoId { get; private set; }
    internal EncounterManager Encounter { get; }
    internal IEnumerable<CombatNode> Members { get { foreach (var node in _members) yield return node; } }
    internal int MemberCount => _members.Count;
    internal bool Contains(CombatNode node) => _members.Contains(node);
    internal void Add(CombatNode node) => _members.Add(node);
    internal void RetireInto(long id) { MergedIntoId = id; _members.Clear(); }
    internal CombatCluster(long id, double now, EncounterSettings settings)
    {
        Id = id;
        Encounter = new EncounterManager(new CombatStatisticsAggregator(), settings);
        Encounter.BeginCluster(id, now);
    }
}
