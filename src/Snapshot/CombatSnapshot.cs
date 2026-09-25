using System;
using System.Collections.Generic;
using DiagnosticDamageProbe.Encounter;

namespace DiagnosticDamageProbe.Snapshot;

internal sealed class CombatSnapshotPlayer
{
    internal long PlayerId { get; }
    internal string DisplayName { get; }
    internal float DamageDone { get; }
    internal double Dps { get; }
    internal float DamageTaken { get; }
    internal CombatSnapshotPlayer(long playerId, string displayName, float damageDone, double dps, float damageTaken)
    { PlayerId = playerId; DisplayName = displayName ?? ""; DamageDone = damageDone; Dps = dps; DamageTaken = damageTaken; }
}

internal sealed class CombatSnapshot
{
    internal const byte ProtocolVersion = 1;
    internal readonly long HostPeerSessionId;
    internal readonly Guid SnapshotEpoch;
    internal readonly long Sequence;
    internal readonly long EncounterId;
    internal readonly EncounterState EncounterState;
    internal readonly double EncounterElapsedSeconds;
    internal readonly IReadOnlyList<CombatSnapshotPlayer> Players;

    internal CombatSnapshot(long hostPeerSessionId, Guid epoch, long sequence, long encounterId,
        EncounterState state, double elapsed, IReadOnlyList<CombatSnapshotPlayer> players)
    { HostPeerSessionId = hostPeerSessionId; SnapshotEpoch = epoch; Sequence = sequence; EncounterId = encounterId;
      EncounterState = state; EncounterElapsedSeconds = elapsed;
      Players = players == null ? Array.Empty<CombatSnapshotPlayer>() : new List<CombatSnapshotPlayer>(players).AsReadOnly(); }
}
