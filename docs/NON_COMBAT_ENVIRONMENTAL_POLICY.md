# Corrective policy: non-combat environmental damage

The previous Damage Taken classifier admitted every player-victim event whose
attacker was not a Player. Consequently Fall could create/refresh an encounter,
add Damage Taken and clear a death recovery ticket through ordinary reengagement.

CombatStatisticsAggregator.Classify now rejects exactly these canonical HitType
bytes before applying the existing PvE rules:

| Valheim HitData.HitType | byte |
| --- | --- |
| Fall | 3 |
| Drowning | 4 |
| Smoke | 9 |

Values were verified by decompiling HitData from the project's referenced
assembly_valheim.dll (SHA-256
96CFC004F7F4A6F30D070BEF39EAFD79C466A137121C4665A2F19FB9C15C6127).
The named private enum documents the correspondence without adding a Unity
reference to the pure statistics layer. ApplyDamagePatch already transports
HitType as its canonical byte. No wire change is required.

This is the only production policy change. Statistics, vanilla encounter acceptance
and cluster endpoint extraction already share Classify. The attribution path for
player victims already excludes attributed player/summon damage; it is unchanged.
No downstream duplicate filters were introduced. Ordinary time-driven Update can
still expire encounters or tickets; ignored events do not refresh them.

This supersedes the broad environmental player-only examples in the 4A/4B/4C
milestone notes only for this explicit three-value set. AttackerClass.None is not
itself grounds for exclusion. Non-excluded unattributed damage retains player-only
cluster behavior. Poisoned=7, Burning=5, Tree=13 and Incinerator=23 are unchanged.

32 new checks bring the suite from 402 to 434. Each excluded type has classification,
empty encounter/cluster, Active timeout/DPS/statistics, Recovery ticket, no-merge,
and actual host NoEncounter publication regression coverage. Positive checks cover
NPC attacks, non-excluded unattributed damage, Tree/Incinerator and Poison/Burning
attribution into both legacy and cluster encounters. Managed HitType doubles now
use the verified game values rather than an unrelated implicit enum ordering.

The 4C snapshot routing/delivery architecture, codec v1, identity mapping and sequence
policy are unchanged. No release metadata or frozen 0.11.2 ZIP is changed.
