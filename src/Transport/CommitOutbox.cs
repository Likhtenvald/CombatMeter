using System;
using System.Collections.Generic;

namespace DiagnosticDamageProbe.Transport;

// Stop-and-wait: don't advance the source sequence on wire past an unacknowledged head.
// This prevents lost ACKs from pushing that head out of the host's recent-sequence window.
internal sealed class CommitOutbox
{
    internal const int DefaultCapacity = 1024;
    private readonly int _capacity;
    private readonly Queue<DamageCommit> _pending = new Queue<DamageCommit>();
    private double _lastSend = double.NegativeInfinity;
    internal int Count => _pending.Count;
    internal bool HasCapacity => Count < _capacity;
    internal CommitOutbox(int capacity = DefaultCapacity)
    { if (capacity <= 0) throw new ArgumentOutOfRangeException(); _capacity = capacity; }
    internal bool Enqueue(DamageCommit commit)
    { if (!HasCapacity) return false; _pending.Enqueue(commit); return true; }
    internal DamageCommit Due(double monotonicSeconds)
    {
        if (Count == 0 || monotonicSeconds - _lastSend < 1.0) return null;
        _lastSend = monotonicSeconds;
        return _pending.Peek();
    }
    internal bool Complete(EventId id)
    {
        if (Count == 0 || !_pending.Peek().Id.Equals(id)) return false;
        _pending.Dequeue(); _lastSend = double.NegativeInfinity; return true;
    }
    internal IEnumerable<DamageCommit> Pending => _pending;
}
