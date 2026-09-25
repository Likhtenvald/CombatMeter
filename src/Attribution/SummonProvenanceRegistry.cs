using System.Collections.Generic;

namespace DiagnosticDamageProbe.Attribution;

internal sealed class SummonProvenanceRegistry
{
    private readonly int _capacity;
    private readonly Dictionary<string, long> _owners = new Dictionary<string, long>();
    private readonly Queue<string> _order = new Queue<string>();
    internal int Count => _owners.Count;

    internal SummonProvenanceRegistry(int capacity = 512) { _capacity = capacity; }
    internal bool Register(string summonZdoId, long playerId)
    {
        if (string.IsNullOrEmpty(summonZdoId) || playerId == 0) return false;
        if (_owners.TryGetValue(summonZdoId, out long existing)) return existing == playerId;
        while (_owners.Count >= _capacity) _owners.Remove(_order.Dequeue());
        _owners.Add(summonZdoId, playerId); _order.Enqueue(summonZdoId); return true;
    }
    internal bool TryResolve(string summonZdoId, out long playerId) => _owners.TryGetValue(summonZdoId ?? "", out playerId);
    internal void Reset() { _owners.Clear(); _order.Clear(); }
}
