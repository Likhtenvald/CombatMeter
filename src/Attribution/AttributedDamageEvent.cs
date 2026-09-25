using System.Collections.Generic;
using DiagnosticDamageProbe.Transport;

namespace DiagnosticDamageProbe.Attribution;

internal readonly struct DamagePortion
{
    internal readonly long? PlayerId;
    internal readonly float Damage;
    internal DamagePortion(long? playerId, float damage) { PlayerId = playerId; Damage = damage; }
}

internal sealed class AttributedDamageEvent
{
    internal readonly DamageCommit Commit;
    internal readonly List<DamagePortion> DamageDone;
    internal readonly bool ApplyVanillaClassification;
    internal AttributedDamageEvent(DamageCommit commit, List<DamagePortion> damageDone, bool applyVanilla)
    { Commit = commit; DamageDone = damageDone ?? new List<DamagePortion>(); ApplyVanillaClassification = applyVanilla; }
}
