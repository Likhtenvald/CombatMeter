using System;
using System.Globalization;

namespace DiagnosticDamageProbe.Transport;

internal enum AttackerClass : byte { None, Player, NPC, Unresolved }
internal enum CommitOrigin { Local, Remote }

internal readonly struct EventId : IEquatable<EventId>
{
    internal readonly long SourcePeerId;
    internal readonly Guid SourceEpoch;
    internal readonly long Sequence;
    internal EventId(long sourcePeerId, Guid sourceEpoch, long sequence)
    { SourcePeerId = sourcePeerId; SourceEpoch = sourceEpoch; Sequence = sequence; }
    internal bool IsValid => SourcePeerId != 0 && SourceEpoch != Guid.Empty && Sequence > 0;
    public bool Equals(EventId other) => SourcePeerId == other.SourcePeerId && SourceEpoch == other.SourceEpoch && Sequence == other.Sequence;
    public override bool Equals(object other) => other is EventId id && Equals(id);
    public override int GetHashCode() => SourcePeerId.GetHashCode() ^ SourceEpoch.GetHashCode() ^ Sequence.GetHashCode();
    public override string ToString() => SourcePeerId.ToString(CultureInfo.InvariantCulture) + "/" + SourceEpoch.ToString("N") + "/" + Sequence.ToString(CultureInfo.InvariantCulture);
}

// Values only. No Unity, HitData, ZDOID registry or runtime object references.
internal sealed class DamageFacts
{
    internal readonly long VictimCreator;
    internal readonly uint VictimObject;
    internal readonly bool VictimIsPlayer;
    internal readonly long? VictimPlayerId;
    internal readonly AttackerClass Attacker;
    internal readonly long? AttackerPlayerId;
    internal readonly long AttackerCreator;
    internal readonly uint AttackerObject;
    internal readonly byte DotKind;
    internal readonly byte HitType;
    internal readonly string VictimName;
    internal readonly string AttackerName;
    internal DamageFacts(long victimCreator, uint victimObject, bool victimIsPlayer, long? victimPlayerId,
        AttackerClass attacker, long? attackerPlayerId, byte hitType, string victimName, string attackerName,
        long attackerCreator = 0, uint attackerObject = 0, byte dotKind = 0)
    {
        VictimCreator = victimCreator; VictimObject = victimObject; VictimIsPlayer = victimIsPlayer;
        VictimPlayerId = victimPlayerId; Attacker = attacker; AttackerPlayerId = attackerPlayerId;
        AttackerCreator = attackerCreator; AttackerObject = attackerObject;
        DotKind = dotKind;
        HitType = hitType; VictimName = ShortName(victimName); AttackerName = ShortName(attackerName);
    }
    internal string VictimZdoId => VictimCreator.ToString(CultureInfo.InvariantCulture) + ":" + VictimObject.ToString(CultureInfo.InvariantCulture);
    internal string AttackerZdoId => AttackerCreator == 0 || AttackerObject == 0 ? "" :
        AttackerCreator.ToString(CultureInfo.InvariantCulture) + ":" + AttackerObject.ToString(CultureInfo.InvariantCulture);
    private static string ShortName(string name)
    {
        if (name == null) return "";
        int length = Math.Min(name.Length, 96);
        if (length > 0 && char.IsHighSurrogate(name[length - 1])) length--;
        return name.Substring(0, length);
    }
}

internal sealed class DamageCommit
{
    internal readonly EventId Id;
    internal readonly DamageFacts Facts;
    internal readonly float EffectiveHpLoss;
    internal readonly long TimestampUtcTicks;
    internal long SourcePeerId => Id.SourcePeerId;
    internal DamageCommit(EventId id, DamageFacts facts, float loss, long timestampUtcTicks)
    { Id = id; Facts = facts; EffectiveHpLoss = loss; TimestampUtcTicks = timestampUtcTicks; }
}

internal sealed class EventSequence
{
    internal readonly long Peer;
    internal readonly Guid Epoch;
    private long _sequence;
    internal EventSequence(long peer, Guid epoch)
    {
        if (peer == 0 || epoch == Guid.Empty) throw new ArgumentException("Invalid source session");
        Peer = peer; Epoch = epoch;
    }
    // Called on the Unity main thread. Overflow fails; identity never wraps/reuses.
    internal EventId Next() => new EventId(Peer, Epoch, checked(++_sequence));
}
