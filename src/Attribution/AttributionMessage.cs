using System;
using System.IO;
using System.Globalization;
using DiagnosticDamageProbe.Transport;

namespace DiagnosticDamageProbe.Attribution;

internal enum AttributionMessageKind : byte { Summon = 1, DotPool = 2 }

internal sealed class AttributionMessage
{
    internal readonly EventId Id;
    internal readonly AttributionMessageKind Kind;
    internal readonly long SubjectCreator;
    internal readonly uint SubjectObject;
    internal readonly long? PlayerId;
    internal readonly DotKind DotKind;
    internal readonly float PoolBefore;
    internal readonly float PoolAfter;
    internal readonly AttackerClass SourceClass;
    internal readonly long? SourcePlayerId;
    internal readonly long SourceCreator;
    internal readonly uint SourceObject;
    internal readonly bool PoolUpdateAccepted;
    internal AttributionMessage(EventId id, AttributionMessageKind kind, long creator, uint obj, long? playerId,
        DotKind dotKind = DotKind.Poison, float before = 0, float after = 0, AttackerClass sourceClass = AttackerClass.None,
        long? sourcePlayerId = null, long sourceCreator = 0, uint sourceObject = 0, bool poolUpdateAccepted = true)
    { Id = id; Kind = kind; SubjectCreator = creator; SubjectObject = obj; PlayerId = playerId; DotKind = dotKind;
      PoolBefore = before; PoolAfter = after; SourceClass = sourceClass; SourcePlayerId = sourcePlayerId;
      SourceCreator = sourceCreator; SourceObject = sourceObject; PoolUpdateAccepted = poolUpdateAccepted; }
    internal string SubjectZdoId => SubjectCreator.ToString(CultureInfo.InvariantCulture) + ":" + SubjectObject.ToString(CultureInfo.InvariantCulture);
    internal string SourceZdoId => SourceCreator == 0 || SourceObject == 0 ? "" : SourceCreator.ToString(CultureInfo.InvariantCulture) + ":" + SourceObject.ToString(CultureInfo.InvariantCulture);
}

internal static class AttributionCodec
{
    internal const byte Version = 1;
    internal const int MaxBytes = 256;
    internal static byte[] Encode(AttributionMessage m)
    {
        using var s = new MemoryStream(); using var w = new BinaryWriter(s);
        w.Write(Version); w.Write(m.Id.SourcePeerId); w.Write(m.Id.SourceEpoch.ToByteArray()); w.Write(m.Id.Sequence);
        w.Write((byte)m.Kind); w.Write(m.SubjectCreator); w.Write(m.SubjectObject); WriteOptional(w, m.PlayerId);
        w.Write((byte)m.DotKind); w.Write(m.PoolBefore); w.Write(m.PoolAfter); w.Write((byte)m.SourceClass);
        WriteOptional(w, m.SourcePlayerId); w.Write(m.SourceCreator); w.Write(m.SourceObject); w.Write(m.PoolUpdateAccepted); w.Flush(); return s.ToArray();
    }
    internal static AttributionMessage Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length > MaxBytes) throw new InvalidDataException("PacketSize");
        using var s = new MemoryStream(bytes, false); using var r = new BinaryReader(s);
        if (r.ReadByte() != Version) throw new InvalidDataException("Version");
        var id = new EventId(r.ReadInt64(), new Guid(Exact(r, 16)), r.ReadInt64());
        var m = new AttributionMessage(id, (AttributionMessageKind)r.ReadByte(), r.ReadInt64(), r.ReadUInt32(), ReadOptional(r),
            (DotKind)r.ReadByte(), r.ReadSingle(), r.ReadSingle(), (AttackerClass)r.ReadByte(), ReadOptional(r), r.ReadInt64(), r.ReadUInt32(), ReadBool(r));
        if (s.Position != s.Length) throw new InvalidDataException("Trailing"); return m;
    }
    internal static byte[] EncodeAck(EventId id)
    { using var s = new MemoryStream(); using var w = new BinaryWriter(s); w.Write(Version); w.Write(id.SourcePeerId); w.Write(id.SourceEpoch.ToByteArray()); w.Write(id.Sequence); return s.ToArray(); }
    internal static EventId DecodeAck(byte[] bytes)
    { if (bytes == null || bytes.Length > 64) throw new InvalidDataException(); using var s = new MemoryStream(bytes); using var r = new BinaryReader(s);
      if (r.ReadByte() != Version) throw new InvalidDataException(); var id = new EventId(r.ReadInt64(), new Guid(Exact(r, 16)), r.ReadInt64()); if (!id.IsValid || s.Position != s.Length) throw new InvalidDataException(); return id; }
    private static void WriteOptional(BinaryWriter w, long? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
    private static long? ReadOptional(BinaryReader r) { byte b = r.ReadByte(); if (b > 1) throw new InvalidDataException(); return b == 1 ? r.ReadInt64() : (long?)null; }
    private static bool ReadBool(BinaryReader r) { byte b = r.ReadByte(); if (b > 1) throw new InvalidDataException(); return b == 1; }
    private static byte[] Exact(BinaryReader r, int n) { byte[] b = r.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return b; }
}

internal sealed class AttributionAcceptor
{
    private readonly System.Collections.Generic.Dictionary<(long, Guid), Window> _sources = new System.Collections.Generic.Dictionary<(long, Guid), Window>();
    internal string Accept(AttributionMessage m, long sender)
    {
        if (m == null || !m.Id.IsValid || m.Id.SourcePeerId != sender) return "InvalidSenderOrEvent";
        if (m.SubjectCreator == 0 || m.SubjectObject == 0 || m.Kind < AttributionMessageKind.Summon || m.Kind > AttributionMessageKind.DotPool) return "InvalidPayload";
        if (m.Kind == AttributionMessageKind.Summon && (!m.PlayerId.HasValue || m.PlayerId.Value == 0)) return "InvalidPayload";
        if (m.Kind == AttributionMessageKind.DotPool && (m.DotKind > DotKind.Spirit || float.IsNaN(m.PoolBefore) || float.IsNaN(m.PoolAfter) || float.IsInfinity(m.PoolBefore) || float.IsInfinity(m.PoolAfter) || m.PoolAfter < 0 || m.PoolBefore < 0)) return "InvalidPayload";
        if ((m.SourceCreator == 0) != (m.SourceObject == 0) || m.SourcePlayerId == 0 || m.SourceClass > AttackerClass.Unresolved ||
            (m.SourceClass != AttackerClass.Player && m.SourcePlayerId.HasValue) ||
            (m.Kind == AttributionMessageKind.DotPool && m.SourceClass == AttackerClass.Player && !m.SourcePlayerId.HasValue)) return "InvalidPayload";
        var key = (m.Id.SourcePeerId, m.Id.SourceEpoch);
        if (!_sources.TryGetValue(key, out Window window))
        { if (_sources.Count >= 128) return "SourceCapacity"; _sources.Add(key, window = new Window()); }
        return window.Accept(m.Id.Sequence);
    }
    internal void Reset() => _sources.Clear();
    private sealed class Window
    {
        private readonly bool[] _seen = new bool[2048]; private long _highest;
        internal string Accept(long sequence)
        {
            if (sequence <= _highest - _seen.Length) return "TooOld";
            if (sequence > _highest)
            {
                long advance = sequence - _highest;
                if (advance >= _seen.Length) Array.Clear(_seen, 0, _seen.Length);
                else for (long n = 1; n <= advance; n++) _seen[(_highest + n) % _seen.Length] = false;
                _highest = sequence;
            }
            int index = (int)(sequence % _seen.Length); if (_seen[index]) return "Duplicate";
            _seen[index] = true; return "Accepted";
        }
    }
}

internal sealed class AttributionOutbox
{
    private sealed class Entry
    {
        internal readonly AttributionMessage Message;
        internal double FirstSent = -1d;
        internal Entry(AttributionMessage message) { Message = message; }
    }
    private readonly System.Collections.Generic.Queue<Entry> _queue = new System.Collections.Generic.Queue<Entry>();
    private double _next;
    internal const double RetryIntervalSeconds = 1d;
    internal const double MaxLifetimeSeconds = 30d;
    internal int Count => _queue.Count;
    internal bool Enqueue(AttributionMessage m) { if (_queue.Count >= 256) return false; _queue.Enqueue(new Entry(m)); return true; }
    internal AttributionMessage Due(double now, out AttributionMessage expired)
    {
        expired = null;
        if (_queue.Count == 0) return null;
        Entry head = _queue.Peek();
        if (head.FirstSent >= 0d && now - head.FirstSent >= MaxLifetimeSeconds)
        { expired = head.Message; _queue.Dequeue(); _next = 0d; return null; }
        if (now < _next) return null;
        if (head.FirstSent < 0d) head.FirstSent = now;
        _next = now + RetryIntervalSeconds;
        return head.Message;
    }
    internal bool Complete(EventId id, out AttributionMessage completed)
    {
        completed = null;
        if (_queue.Count == 0 || !_queue.Peek().Message.Id.Equals(id)) return false;
        completed = _queue.Dequeue().Message; _next = 0d; return true;
    }
    internal void Reset() { _queue.Clear(); _next = 0; }
}
