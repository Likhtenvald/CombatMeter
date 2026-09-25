using System;
using System.IO;
using System.Text;

namespace DiagnosticDamageProbe.Transport;

internal static class CommitCodec
{
    internal const byte Version = 2;
    internal const int MaxPacketBytes = 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static byte[] Encode(DamageCommit c)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Utf8, true);
        w.Write(Version); WriteId(w, c.Id);
        DamageFacts f = c.Facts;
        w.Write(f.VictimCreator); w.Write(f.VictimObject); w.Write(f.VictimIsPlayer);
        WriteOptional(w, f.VictimPlayerId); w.Write((byte)f.Attacker); WriteOptional(w, f.AttackerPlayerId);
        w.Write(f.HitType); w.Write(c.EffectiveHpLoss); w.Write(c.TimestampUtcTicks);
        w.Write(f.AttackerCreator); w.Write(f.AttackerObject);
        w.Write(f.DotKind);
        WriteName(w, f.VictimName); WriteName(w, f.AttackerName);
        w.Flush(); return stream.ToArray();
    }

    internal static DamageCommit Decode(byte[] bytes)
    {
        using var stream = Open(bytes);
        using var r = new BinaryReader(stream, Utf8, true);
        CheckVersion(r); EventId id = ReadId(r);
        long victimCreator = r.ReadInt64(); uint victimObject = r.ReadUInt32(); bool isPlayer = ReadBool(r);
        long? victimPlayer = ReadOptional(r); var attacker = (AttackerClass)r.ReadByte(); long? attackerPlayer = ReadOptional(r);
        byte hitType = r.ReadByte(); float loss = r.ReadSingle(); long ticks = r.ReadInt64();
        long attackerCreator = r.ReadInt64(); uint attackerObject = r.ReadUInt32();
        byte dotKind = r.ReadByte();
        string victimName = ReadName(r); string attackerName = ReadName(r);
        End(stream);
        return new DamageCommit(id, new DamageFacts(victimCreator, victimObject, isPlayer, victimPlayer,
            attacker, attackerPlayer, hitType, victimName, attackerName, attackerCreator, attackerObject, dotKind), loss, ticks);
    }

    internal static byte[] EncodeAck(EventId id, Acceptance result)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(Version); WriteId(w, id); w.Write((byte)result); w.Flush(); return stream.ToArray();
    }
    internal static (EventId Id, Acceptance Result) DecodeAck(byte[] bytes)
    {
        using var stream = Open(bytes);
        using var r = new BinaryReader(stream);
        CheckVersion(r); EventId id = ReadId(r); var result = (Acceptance)r.ReadByte();
        if (!id.IsValid || result > Acceptance.SenderMismatch) throw new InvalidDataException("InvalidAck");
        End(stream); return (id, result);
    }
    private static void WriteId(BinaryWriter w, EventId id)
    { w.Write(id.SourcePeerId); w.Write(id.SourceEpoch.ToByteArray()); w.Write(id.Sequence); }
    private static EventId ReadId(BinaryReader r) => new EventId(r.ReadInt64(), new Guid(Exact(r, 16)), r.ReadInt64());
    private static void WriteOptional(BinaryWriter w, long? value) { w.Write(value.HasValue); if (value.HasValue) w.Write(value.Value); }
    private static long? ReadOptional(BinaryReader r) => ReadBool(r) ? r.ReadInt64() : (long?)null;
    private static bool ReadBool(BinaryReader r)
    { byte b = r.ReadByte(); if (b > 1) throw new InvalidDataException("InvalidBoolean"); return b == 1; }
    private static void WriteName(BinaryWriter w, string value)
    { byte[] bytes = Utf8.GetBytes(value); w.Write((ushort)bytes.Length); w.Write(bytes); }
    private static string ReadName(BinaryReader r)
    {
        int size = r.ReadUInt16(); if (size > 384) throw new InvalidDataException("NameTooLong");
        string name = Utf8.GetString(Exact(r, size));
        if (name.Length > 96) throw new InvalidDataException("NameTooLong");
        return name;
    }
    private static byte[] Exact(BinaryReader r, int count)
    { byte[] b = r.ReadBytes(count); if (b.Length != count) throw new EndOfStreamException(); return b; }
    private static MemoryStream Open(byte[] bytes)
    { if (bytes == null || bytes.Length > MaxPacketBytes) throw new InvalidDataException("PacketSize"); return new MemoryStream(bytes, false); }
    private static void CheckVersion(BinaryReader r)
    { if (r.ReadByte() != Version) throw new InvalidDataException("ProtocolVersion"); }
    private static void End(MemoryStream s)
    { if (s.Position != s.Length) throw new InvalidDataException("TrailingBytes"); }
}
