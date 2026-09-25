using System;
using System.Collections.Generic;

namespace DiagnosticDamageProbe.Attribution;

internal enum DotKind : byte { Poison, Burning, Spirit }

internal sealed class SharedDotContributionLedger
{
    private readonly Dictionary<long, float> _players = new Dictionary<long, float>();
    private float _unattributed;
    internal float TotalOutstanding { get { float total = _unattributed; foreach (float v in _players.Values) total += v; return total; } }

    internal void Add(long? playerId, float delta)
    {
        if (!(delta > 0f)) return;
        if (!playerId.HasValue) { _unattributed += delta; return; }
        _players.TryGetValue(playerId.Value, out float current); _players[playerId.Value] = current + delta;
    }

    internal List<DamagePortion> Distribute(float actualLoss)
    {
        var result = new List<DamagePortion>();
        if (!(actualLoss > 0f)) return result;
        float total = TotalOutstanding;
        if (!(total > 0f)) { result.Add(new DamagePortion(null, actualLoss)); return result; }
        float remaining = actualLoss;
        var keys = new List<long>(_players.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            long id = keys[i]; float outstanding = _players[id];
            float portion = i == keys.Count - 1 && _unattributed <= 0f ? remaining : actualLoss * outstanding / total;
            portion = Math.Max(0f, Math.Min(outstanding, Math.Min(remaining, portion)));
            if (portion > 0f) result.Add(new DamagePortion(id, portion));
            _players[id] = Math.Max(0f, outstanding - portion); remaining -= portion;
        }
        if (remaining > 0f)
        {
            float portion = Math.Min(remaining, actualLoss * _unattributed / total);
            if (portion < remaining && _unattributed <= 0f) portion = remaining;
            if (portion > 0f) { result.Add(new DamagePortion(null, portion)); _unattributed = Math.Max(0f, _unattributed - portion); remaining -= portion; }
        }
        if (remaining > 0f) result.Add(new DamagePortion(null, remaining));
        return result;
    }
}

internal sealed class PoisonAttributionState
{
    internal long? OwnerPlayerId { get; private set; }
    internal void PoolChanged(long? playerId) => OwnerPlayerId = playerId;
}
