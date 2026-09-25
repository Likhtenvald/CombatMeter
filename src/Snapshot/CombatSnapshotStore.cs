using System;
using System.IO;

namespace DiagnosticDamageProbe.Snapshot;

internal enum SnapshotApplyResult { Accepted, Duplicate, Stale, UnexpectedSender, SessionMismatch, Invalid }

internal sealed class CombatSnapshotStore
{
    private CombatSnapshot _latest;
    internal bool TryGetLatest(out CombatSnapshot snapshot) { snapshot = _latest; return snapshot != null; }

    internal SnapshotApplyResult Apply(CombatSnapshot snapshot, long actualSender, long expectedHost)
    {
        if (actualSender == 0 || actualSender != expectedHost || snapshot == null || snapshot.HostPeerSessionId != actualSender)
            return SnapshotApplyResult.UnexpectedSender;
        try { CombatSnapshotCodec.Validate(snapshot); } catch (InvalidDataException) { return SnapshotApplyResult.Invalid; }
        if (_latest != null)
        {
            if (_latest.SnapshotEpoch != snapshot.SnapshotEpoch) return SnapshotApplyResult.SessionMismatch;
            if (snapshot.Sequence == _latest.Sequence) return SnapshotApplyResult.Duplicate;
            if (snapshot.Sequence < _latest.Sequence) return SnapshotApplyResult.Stale;
        }
        _latest = snapshot; return SnapshotApplyResult.Accepted;
    }

    internal void Clear() => _latest = null;
}
