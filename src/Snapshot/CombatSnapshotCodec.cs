using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DiagnosticDamageProbe.Encounter;

namespace DiagnosticDamageProbe.Snapshot;

internal static class CombatSnapshotCodec
{
    internal const int MaxBytes = 16 * 1024;

    internal static byte[] Encode(CombatSnapshot snapshot)
    {
        Validate(snapshot);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(CombatSnapshot.ProtocolVersion); writer.Write(snapshot.HostPeerSessionId);
        writer.Write(snapshot.SnapshotEpoch.ToByteArray()); writer.Write(snapshot.Sequence); writer.Write(snapshot.EncounterId);
        writer.Write((byte)snapshot.EncounterState); writer.Write(snapshot.EncounterElapsedSeconds); writer.Write((byte)snapshot.Players.Count);
        foreach (CombatSnapshotPlayer row in snapshot.Players)
        { writer.Write(row.PlayerId); writer.Write(row.DisplayName); writer.Write(row.DamageDone); writer.Write(row.Dps); writer.Write(row.DamageTaken); }
        writer.Flush(); if (stream.Length > MaxBytes) throw new InvalidDataException("PacketSize"); return stream.ToArray();
    }

    internal static CombatSnapshot Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("PacketSize");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadByte() != CombatSnapshot.ProtocolVersion) throw new InvalidDataException("ProtocolVersion");
        long host = reader.ReadInt64(); Guid epoch = new Guid(Exact(reader, 16)); long sequence = reader.ReadInt64();
        long encounterId = reader.ReadInt64(); var state = (EncounterState)reader.ReadByte(); double elapsed = reader.ReadDouble();
        int count = reader.ReadByte(); var rows = new List<CombatSnapshotPlayer>(count);
        for (int i = 0; i < count; i++)
        {
            long playerId = reader.ReadInt64(); string name = reader.ReadString(); float done = reader.ReadSingle();
            double dps = reader.ReadDouble(); float taken = reader.ReadSingle();
            rows.Add(new CombatSnapshotPlayer(playerId, name, done, dps, taken));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("TrailingData");
        var snapshot = new CombatSnapshot(host, epoch, sequence, encounterId, state, elapsed, rows); Validate(snapshot); return snapshot;
    }

    internal static void Validate(CombatSnapshot snapshot)
    {
        if (snapshot == null || snapshot.HostPeerSessionId == 0 || snapshot.SnapshotEpoch == Guid.Empty || snapshot.Sequence <= 0)
            throw new InvalidDataException("InvalidIdentity");
        if (snapshot.EncounterState < EncounterState.NoEncounter || snapshot.EncounterState > EncounterState.Finished ||
            snapshot.EncounterId < 0 || !CombatSnapshotBuilder.FiniteNonnegative(snapshot.EncounterElapsedSeconds))
            throw new InvalidDataException("InvalidEncounter");
        if ((snapshot.EncounterState == EncounterState.NoEncounter) != (snapshot.EncounterId == 0) ||
            (snapshot.EncounterState == EncounterState.NoEncounter && (snapshot.EncounterElapsedSeconds != 0d || snapshot.Players.Count != 0)))
            throw new InvalidDataException("InconsistentEncounter");
        if (snapshot.Players.Count > CombatSnapshotBuilder.MaxPlayers) throw new InvalidDataException("PlayerCount");
        var ids = new HashSet<long>(); long previous = long.MinValue;
        foreach (CombatSnapshotPlayer row in snapshot.Players)
        {
            if (row == null || row.PlayerId == 0 || !ids.Add(row.PlayerId)) throw new InvalidDataException("PlayerIdentity");
            if (row.PlayerId < previous) throw new InvalidDataException("PlayerOrder"); previous = row.PlayerId;
            if (row.DisplayName == null || row.DisplayName.Length > CombatSnapshotBuilder.MaxDisplayNameLength)
                throw new InvalidDataException("DisplayName");
            if (!CombatSnapshotBuilder.FiniteNonnegative(row.DamageDone) || !CombatSnapshotBuilder.FiniteNonnegative(row.Dps) ||
                !CombatSnapshotBuilder.FiniteNonnegative(row.DamageTaken)) throw new InvalidDataException("PlayerNumbers");
        }
    }

    private static byte[] Exact(BinaryReader reader, int count)
    { byte[] value = reader.ReadBytes(count); if (value.Length != count) throw new EndOfStreamException(); return value; }
}
