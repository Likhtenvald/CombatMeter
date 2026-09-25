using System;
using System.Collections.Generic;
using DiagnosticDamageProbe.Attribution;
using DiagnosticDamageProbe.Transport;

namespace DiagnosticDamageProbe.Encounter;

// Host-only domain core, fed already accepted/deduplicated and attributed commits.
// Connectivity only grows during an encounter: no edge expiry, distance or live split.
internal sealed class CombatClusterManager
{
    private readonly Dictionary<long, CombatCluster> _active = new Dictionary<long, CombatCluster>();
    private readonly Dictionary<CombatNode, CombatCluster> _membership = new Dictionary<CombatNode, CombatCluster>();
    private long _nextId;
    internal EncounterSettings Settings { get; }
    internal int ActiveCount => _active.Count;
    internal IEnumerable<CombatCluster> ActiveClusters => _active.Values;
    // Bounded retention, like the legacy manager's last finished encounter.
    internal CombatCluster LastFinished { get; private set; }
    internal CombatClusterManager(EncounterSettings settings = null) { Settings = settings ?? new EncounterSettings(); }
    internal bool TryGet(long id, out CombatCluster cluster) => _active.TryGetValue(id, out cluster);
    internal bool TryGetMembership(CombatNode node, out CombatCluster cluster) => _membership.TryGetValue(node, out cluster);
    // Stable gameplay PlayerID, never a network peer ID. Active membership includes Recovery.
    // Pure lookup: the host updates lifecycle separately; no timeout processing or fallback.
    internal bool TryGetClusterForPlayer(long playerId, out CombatCluster cluster) =>
        TryGetMembership(CombatNode.Player(playerId), out cluster);

    internal CombatCluster Accept(DamageCommit commit, double now, double? eventTime = null) =>
        Accept(new AttributedDamageEvent(commit, null, true), now, eventTime);

    internal CombatCluster Accept(AttributedDamageEvent damage, double now, double? eventTime = null)
    {
        ValidateTime(now); ValidateTime(eventTime ?? now);
        Update(now);
        HashSet<CombatNode> nodes = CombatInteraction.Nodes(damage);
        if (nodes.Count == 0) return null;
        // Stable survivor regardless of hash-set enumeration or attribution portion order.
        var connected = new SortedDictionary<long, CombatCluster>();
        foreach (CombatNode node in nodes)
            if (_membership.TryGetValue(node, out CombatCluster existing)) connected[existing.Id] = existing;
        CombatCluster target = null;
        foreach (CombatCluster source in connected.Values)
        {
            if (target == null) { target = source; continue; }
            target.Encounter.AbsorbDisjoint(source.Encounter);
            foreach (CombatNode member in source.Members) { target.Add(member); _membership[member] = target; }
            _active.Remove(source.Id);
            source.RetireInto(target.Id);
        }
        if (target == null)
        {
            target = new CombatCluster(checked(++_nextId), now, Settings);
            _active.Add(target.Id, target);
        }
        foreach (CombatNode node in nodes) { target.Add(node); _membership[node] = target; }
        // Apply the complete attributed event exactly once, after all component unions.
        target.Encounter.Accept(damage, now, eventTime);
        return target;
    }

    internal void Update(double now)
    {
        ValidateTime(now);
        var finished = new List<long>();
        foreach (CombatCluster cluster in _active.Values)
        {
            cluster.Encounter.Update(now);
            if (cluster.Encounter.State != EncounterState.Finished) continue;
            finished.Add(cluster.Id);
            foreach (CombatNode node in cluster.Members) _membership.Remove(node);
            if (LastFinished == null || cluster.Encounter.EndTime > LastFinished.Encounter.EndTime ||
                (cluster.Encounter.EndTime == LastFinished.Encounter.EndTime && cluster.Id > LastFinished.Id)) LastFinished = cluster;
        }
        foreach (long id in finished) _active.Remove(id);
    }

    internal PlayerDeathResult OnPlayerDied(long playerId, double now, string incarnation = null)
    {
        Update(now);
        if (_membership.TryGetValue(CombatNode.Player(playerId), out CombatCluster cluster))
            return cluster.Encounter.OnPlayerDied(playerId, now, incarnation);
        return _active.Count == 0 ? PlayerDeathResult.NoActiveEncounter : PlayerDeathResult.NoActiveParticipant;
    }

    internal void OnPlayerRespawned(long playerId, double now)
    {
        Update(now);
        if (_membership.TryGetValue(CombatNode.Player(playerId), out CombatCluster cluster))
            cluster.Encounter.OnPlayerRespawned(playerId, now);
    }

    internal void Reset() { _active.Clear(); _membership.Clear(); LastFinished = null; _nextId = 0; }
    private static void ValidateTime(double time)
    { if (double.IsNaN(time) || double.IsInfinity(time) || time < 0d) throw new ArgumentOutOfRangeException(nameof(time)); }
}
