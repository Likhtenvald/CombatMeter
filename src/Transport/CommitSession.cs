using System;

namespace DiagnosticDamageProbe.Transport;

// One connection/world lifetime; no static state. Tests drive the same local/remote paths.
internal sealed class CommitSession
{
    internal readonly long Peer;
    internal readonly bool IsHost;
    internal readonly EventSequence Sequence;
    internal readonly CommitAcceptor Acceptor = new CommitAcceptor();
    internal readonly CommitOutbox Outbox = new CommitOutbox();
    private readonly Action<DamageCommit> _send;
    private readonly Action<string, DamageCommit, string> _log;
    private readonly Action<DamageCommit> _accepted;
    private bool _closed;

    internal CommitSession(long peer, Guid epoch, bool isHost, Action<DamageCommit> send,
        Action<string, DamageCommit, string> log, Action<DamageCommit> accepted = null)
    {
        Peer = peer; IsHost = isHost; Sequence = new EventSequence(peer, epoch);
        _send = send; _log = log; _accepted = accepted;
    }

    internal void Observe(DamageFacts facts, float loss, long ticks)
    {
        if (_closed) return;
        if (!IsHost && !Outbox.HasCapacity)
        {
            _log("DamageCommitObservationRejected", null, "PendingCapacity");
            return; // Explicit failure, never silently evict an unacknowledged event.
        }
        var commit = new DamageCommit(Sequence.Next(), facts, loss, ticks);
        if (!IsHost) Outbox.Enqueue(commit);
        _log("DamageCommitCreated", commit, IsHost ? "Local" : "RemoteToHost");
        if (IsHost) ProcessDamageCommit(commit, Peer, CommitOrigin.Local);
    }

    internal Acceptance ProcessDamageCommit(DamageCommit commit, long sender, CommitOrigin origin)
    {
        if (_closed || !IsHost) return Acceptance.InvalidEvent;
        Acceptance result = Acceptor.Process(commit, sender, origin);
        _log(result == Acceptance.Accepted ? "DamageCommitAccepted" : "DamageCommitRejected", commit,
            result == Acceptance.Accepted ? origin.ToString() : result.ToString());
        if (result == Acceptance.Accepted) _accepted?.Invoke(commit);
        return result;
    }

    internal void Pump(double now)
    {
        if (_closed || IsHost) return;
        DamageCommit commit = Outbox.Due(now);
        if (commit == null) return;
        try { _send(commit); }
        catch (Exception ex) { _log("DamageCommitSendDeferred", commit, ex.GetType().Name); }
    }

    internal void Acknowledge(EventId id, Acceptance result)
    {
        if (_closed || IsHost) return;
        DamageCommit head = null;
        foreach (DamageCommit c in Outbox.Pending) { head = c; break; }
        if (!Outbox.Complete(id)) return; // stale/wrong/out-of-order ACK cannot discard another commit
        _log(result == Acceptance.Accepted || result == Acceptance.Duplicate ? "DamageCommitAcknowledged" : "DamageCommitDeliveryFailed",
            head, result.ToString());
    }

    internal void Close(string reason)
    {
        if (_closed) return;
        _closed = true;
        foreach (DamageCommit c in Outbox.Pending) _log("DamageCommitAbandoned", c, reason);
        // The entire session becomes unreachable; no queue/sequence/dedup migrates to another world.
    }
}
