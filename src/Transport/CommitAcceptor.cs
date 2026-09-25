using System;
using System.Collections.Generic;

namespace DiagnosticDamageProbe.Transport;

internal enum Acceptance : byte { Accepted, Duplicate, TooOld, InvalidEvent, InvalidDamage, InvalidPayload, SourceCapacity, SenderMismatch }

internal sealed class CommitAcceptor
{
    internal const int DefaultWindow = 2048;
    internal const int DefaultSources = 128;
    private readonly int _window;
    private readonly int _sources;
    private readonly Dictionary<(long, Guid), SequenceWindow> _recent = new Dictionary<(long, Guid), SequenceWindow>();
    internal int SourceCount => _recent.Count;
    internal int AllocatedSlots => SourceCount * _window;

    internal CommitAcceptor(int window = DefaultWindow, int sources = DefaultSources)
    {
        if (window <= 0 || sources <= 0) throw new ArgumentOutOfRangeException();
        _window = window; _sources = sources;
    }

    // Canonical acceptance component for BOTH local host and remote delivery.
    // Origin changes diagnostics only. No combat recomputation or current-owner lookup.
    internal Acceptance Process(DamageCommit commit, long sender, CommitOrigin origin)
    {
        if (commit == null || !commit.Id.IsValid) return Acceptance.InvalidEvent;
        if (commit.SourcePeerId != sender) return Acceptance.SenderMismatch;
        float loss = commit.EffectiveHpLoss;
        if (float.IsNaN(loss) || float.IsInfinity(loss) || loss <= 0) return Acceptance.InvalidDamage;
        DamageFacts f = commit.Facts;
        if (f == null || f.VictimCreator == 0 || f.VictimObject == 0 ||
            f.Attacker > AttackerClass.Unresolved || f.VictimPlayerId == 0 || f.AttackerPlayerId == 0 ||
            (!f.VictimIsPlayer && f.VictimPlayerId.HasValue) ||
            (f.Attacker != AttackerClass.Player && f.AttackerPlayerId.HasValue) ||
            ((f.AttackerCreator == 0) != (f.AttackerObject == 0)) ||
            f.DotKind > 3 ||
            commit.TimestampUtcTicks <= 0 || commit.TimestampUtcTicks > DateTime.MaxValue.Ticks)
            return Acceptance.InvalidPayload;

        var key = (commit.SourcePeerId, commit.Id.SourceEpoch);
        if (!_recent.TryGetValue(key, out SequenceWindow recent))
        {
            // Never evict a source's replay watermark while this host session lives.
            if (_recent.Count >= _sources) return Acceptance.SourceCapacity;
            recent = new SequenceWindow(_window);
            _recent.Add(key, recent);
        }
        return recent.Accept(commit.Id.Sequence);
    }

    private sealed class SequenceWindow
    {
        private readonly bool[] _seen;
        private long _highest;
        internal SequenceWindow(int width) { _seen = new bool[width]; }
        internal Acceptance Accept(long sequence)
        {
            if (sequence <= _highest - _seen.Length) return Acceptance.TooOld;
            if (sequence > _highest)
            {
                long advance = sequence - _highest;
                if (advance >= _seen.Length) Array.Clear(_seen, 0, _seen.Length);
                else for (long offset = 1; offset <= advance; offset++) _seen[(_highest + offset) % _seen.Length] = false;
                _highest = sequence;
            }
            int index = (int)(sequence % _seen.Length);
            if (_seen[index]) return Acceptance.Duplicate;
            _seen[index] = true;
            return Acceptance.Accepted;
        }
    }
}
