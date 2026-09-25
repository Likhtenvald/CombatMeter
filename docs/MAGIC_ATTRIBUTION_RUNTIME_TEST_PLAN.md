# Magic attribution runtime test plan

Install version `0.7.2` and keep `EnableMagicAttributionDiagnosticLogging = true`.

1. **Fire:** hit one durable target once with fire. Preserve `MagicDotApplied`, every `MagicDotTickObserved`, and the
   direct `DamageProbeEvent`. Confirm a stable effect identity, source on application, and no source on ticks.
2. **Two fire casters:** player A applies fire, then player B applies fire before expiry. Compare
   `DamagePoolBefore/After`, `SameEffectInstance`, both source PlayerIDs, and subsequent tick amounts. This requires two
   players for a real answer.
3. **Poison:** repeat with a controlled poison source. Apply a weaker and then stronger second dose to confirm when
   `DamagePoolChanged` is false or true.
4. **Spirit:** apply spirit damage and verify that ticks are logged as `StatusEffectType=Spirit` despite
   `HitType=Burning`.
5. **Staff of the Wild:** cast once, let the root attack, and correlate `MagicSummonCastObserved`,
   `MagicSummonSpawnObserved`, and `MagicSummonObserved` by caster/root ZDOIDs. Compare owner, runtime follow PlayerID,
   and stored follow name on listen host and casting client.
6. **Friendly skeleton:** summon one `Skeleton_Friendly` and let it attack a durable NPC. Confirm
   `SummonProvenanceRegistered` locally (or `SummonProvenanceAccepted` on the host) and direct damage attribution to
   the summoning PlayerID.

Do not infer attribution from proximity or name. Preserve both logs if the root owner differs from the listen host.

For production attribution, also verify `SummonProvenanceAccepted`, `MagicDamageAttributed`,
`PoisonOwnerChanged`, `DotContributionAdded`, and `DotTickDistributed`. After root ownership migration, its mapping
must still resolve to the original PlayerID. For every distributed tick, sum logged portions and compare with
`EffectiveHpLoss`.
