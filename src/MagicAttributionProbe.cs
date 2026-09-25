using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using DiagnosticDamageProbe.Attribution;
using DiagnosticDamageProbe.Transport;

namespace DiagnosticDamageProbe;

internal static class MagicAttributionProbe
{
    internal sealed class RpcContext
    {
        internal readonly Character Attacker;
        internal RpcContext(Character attacker) { Attacker = attacker; }
    }

    internal sealed class DotState
    {
        internal readonly string Type;
        internal readonly float Damage;
        internal readonly StatusEffect Before;
        internal readonly float PoolBefore;
        internal readonly RpcContext Context;
        internal DotState(string type, float damage, StatusEffect before, float poolBefore, RpcContext context)
        { Type = type; Damage = damage; Before = before; PoolBefore = poolBefore; Context = context; }
    }

    [ThreadStatic] private static Stack<RpcContext> _rpcStack;
    private static readonly FieldInfo FireLeft = typeof(SE_Burning).GetField("m_fireDamageLeft", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo SpiritLeft = typeof(SE_Burning).GetField("m_spiritDamageLeft", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo PoisonLeft = typeof(SE_Poison).GetField("m_damageLeft", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo SpawnOwner = typeof(SpawnAbility).GetField("m_owner", BindingFlags.Instance | BindingFlags.NonPublic);
    private sealed class PendingSummon
    {
        internal readonly GameObject Summon;
        internal readonly SpawnAbility Ability;
        internal readonly Character Caster;
        internal readonly long PlayerId;
        internal readonly float Expires;
        internal PendingSummon(GameObject summon, SpawnAbility ability, Character caster, long playerId)
        { Summon = summon; Ability = ability; Caster = caster; PlayerId = playerId; Expires = Time.realtimeSinceStartup + 10f; }
    }
    private static readonly List<PendingSummon> PendingSummons = new List<PendingSummon>();

    internal static RpcContext BeginRpc(HitData hit)
    {
        var context = new RpcContext(hit?.GetAttacker());
        (_rpcStack ??= new Stack<RpcContext>()).Push(context);
        return context;
    }

    internal static void EndRpc(RpcContext context)
    {
        if (_rpcStack == null || _rpcStack.Count == 0) return;
        if (ReferenceEquals(_rpcStack.Peek(), context)) _rpcStack.Pop();
        else _rpcStack.Clear();
    }

    internal static DotState BeginDot(Character victim, string type, float damage)
    {
        if (damage <= 0f) return null;
        StatusEffect before = GetEffect(victim, type);
        RpcContext context = _rpcStack != null && _rpcStack.Count > 0 ? _rpcStack.Peek() : null;
        return new DotState(type, damage, before, Pool(before, type), context);
    }

    internal static void EndDot(Character victim, DotState state)
    {
        if (state == null) return;
        try
        {
            StatusEffect after = GetEffect(victim, state.Type);
            if (!after) return;
            float poolAfter = Pool(after, state.Type);
            bool updateAccepted = state.Type == "Poisoned" ? state.Damage >= state.PoolBefore : poolAfter > state.PoolBefore;
            Character attacker = state.Context?.Attacker;
            Player player = attacker as Player;
            ZNetView victimView = victim.GetComponent<ZNetView>();
            ZDO victimZdo = victimView && victimView.IsValid() ? victimView.GetZDO() : null;
            ZDOID attackerId = attacker ? attacker.GetZDOID() : ZDOID.None;
            if (victimZdo != null)
                Plugin.PublishDotPool(victimZdo.m_uid, state.Type == "Poisoned" ? DotKind.Poison :
                    state.Type == "Spirit" ? DotKind.Spirit : DotKind.Burning, state.PoolBefore, poolAfter,
                    player ? AttackerClass.Player : attacker ? AttackerClass.NPC : AttackerClass.None,
                    player ? (long?)player.GetPlayerID() : null, attackerId, updateAccepted);
            if (!Plugin.MagicLoggingEnabled) return;
            string marker = state.Before ? "MagicDotUpdated" : "MagicDotApplied";
            var fields = CharacterFields(victim)
                .Text("StatusEffectType", state.Type).Number("DamageAmount", state.Damage)
                .Text("EffectInstanceIdentity", after.GetInstanceID().ToString(CultureInfo.InvariantCulture))
                .Number("DamagePoolBefore", state.PoolBefore).Number("DamagePoolAfter", poolAfter)
                .Flag("DamagePoolChanged", poolAfter != state.PoolBefore)
                .Text("SourceAttackerZDOID", attacker ? attacker.GetZDOID().ToString() : null)
                .Text("SourcePlayerID", player ? Nonzero(player.GetPlayerID()) : null)
                .Text("SourcePlayerName", player ? player.GetPlayerName() : null)
                .Flag("SameEffectInstance", state.Before && ReferenceEquals(state.Before, after));
            Plugin.ProbeLog?.LogInfo(marker + " " + fields);
        }
        catch (Exception ex) { Plugin.Warn($"MagicAttributionProbeFailed phase=DotApply exception={ex.GetType().Name}"); }
    }

    internal static void ObserveDotTick(Character victim, HitData hit)
    {
        if (!Plugin.MagicLoggingEnabled || !victim || hit == null) return;
        string type = hit.m_hitType == HitData.HitType.Burning ?
            (hit.m_damage.m_spirit > 0f && hit.m_damage.m_fire <= 0f ? "Spirit" : "Burning") :
            hit.m_hitType == HitData.HitType.Poisoned ? "Poisoned" : null;
        if (type == null) return;
        try
        {
            StatusEffect effect = GetEffect(victim, type);
            Plugin.ProbeLog?.LogInfo("MagicDotTickObserved " + CharacterFields(victim)
                .Text("StatusEffectType", type).Number("DamageAmount", hit.GetTotalDamage())
                .Text("EffectInstanceIdentity", effect ? effect.GetInstanceID().ToString(CultureInfo.InvariantCulture) : null)
                .Text("SourceAttackerZDOID", hit.m_attacker.IsNone() ? null : hit.m_attacker.ToString())
                .Text("SourcePlayerID", null).Text("SourcePlayerName", null));
        }
        catch (Exception ex) { Plugin.Warn($"MagicAttributionProbeFailed phase=DotTick exception={ex.GetType().Name}"); }
    }

    internal static void ObserveSpawnAbility(SpawnAbility ability, Character owner)
    {
        if (!Plugin.MagicLoggingEnabled) return;
        Plugin.MagicLog("MagicSummonRuntimeSetupEntered hook=SpawnAbility.Setup ability=" + (ability ? ability.name : "null"));
        if (!ability || !owner) return;
        try
        {
            Player player = owner as Player;
            string prefabs = "";
            if (ability.m_spawnPrefab != null)
                for (int i = 0; i < ability.m_spawnPrefab.Length; i++)
                    prefabs += (i == 0 ? "" : ",") + (ability.m_spawnPrefab[i] ? ability.m_spawnPrefab[i].name : "null");
            if (!IsRootText(ability.name + " " + prefabs)) return;
            Plugin.ProbeLog?.LogInfo("MagicSummonCastObserved " + new LogFields()
                .Text("Timestamp", Timestamp()).Text("AbilityObjectName", ability.name)
                .Text("ConfiguredSpawnPrefabs", prefabs).Text("CasterZDOID", owner.GetZDOID().ToString())
                .Text("CasterPlayerID", player ? Nonzero(player.GetPlayerID()) : null)
                .Text("CasterPlayerName", player ? player.GetPlayerName() : null));
        }
        catch (Exception ex) { Plugin.Warn($"MagicAttributionProbeFailed phase=SummonCast exception={ex.GetType().Name}"); }
    }

    internal static void ObserveSpawnCreated(GameObject summon, SpawnAbility ability)
    {
        if (Plugin.MagicLoggingEnabled)
            Plugin.MagicLog("MagicSummonRuntimeSpawnEntered hook=SpawnAbility.Spawn.MoveNext ability=" +
                (ability ? ability.name : "null") + " spawned=" + (summon ? summon.name : "null"));
        try
        {
            Character caster = SpawnOwner?.GetValue(ability) as Character;
            if (Plugin.MagicLoggingEnabled)
                Plugin.MagicLog("MagicSummonRuntimeSpawnCreated " + RuntimeFields("SpawnAbility.Spawn.MoveNext", ability, summon, caster));
            if (!summon || !SupportedSummonPolicy.IsSupportedPrefab(summon.name))
            { Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=SpawnedObjectUnsupported spawned=" + (summon ? summon.name : "null")); return; }

            Plugin.MagicLog("SummonProvenanceRegistrationAttempt hook=SpawnAbility.Spawn.MoveNext summon=" + summon.name);
            if (!caster) { Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=CasterUnavailable"); return; }
            Player player = caster as Player;
            if (!player) { Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=CasterNotPlayer"); return; }
            long playerId = player.GetPlayerID();
            if (playerId == 0L) { Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=PlayerIdZero"); return; }

            ZNetView view = summon.GetComponent<ZNetView>();
            if (view && view.IsValid() && view.GetZDO() != null)
            {
                Plugin.MagicLog("MagicSummonZdoReadyObserved " + RuntimeFields("SpawnAbility.Spawn.MoveNext", ability, summon, caster));
                RegisterSummon(view.GetZDO(), playerId, summon);
            }
            else
            {
                Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=SummonZdoUnavailable delayed=true summonInstance=" + summon.GetInstanceID());
                if (PendingSummons.Count >= 64) PendingSummons.RemoveAt(0);
                PendingSummons.Add(new PendingSummon(summon, ability, caster, playerId));
            }
        }
        catch (Exception ex) { Plugin.Warn($"MagicAttributionProbeFailed phase=RuntimeSpawn exception={ex.GetType().Name}"); }
    }

    internal static void UpdatePendingRoots()
    {
        for (int i = PendingSummons.Count - 1; i >= 0; i--)
        {
            PendingSummon pending = PendingSummons[i];
            if (!pending.Summon || Time.realtimeSinceStartup >= pending.Expires)
            {
                Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=SummonZdoUnavailable expired=true summonInstance=" +
                    (pending.Summon ? pending.Summon.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "null"));
                PendingSummons.RemoveAt(i);
                continue;
            }
            ZNetView view = pending.Summon.GetComponent<ZNetView>();
            if (view && view.IsValid() && view.GetZDO() != null)
            {
                Plugin.MagicLog("MagicSummonZdoReadyObserved " + RuntimeFields("Plugin.Update", pending.Ability, pending.Summon, pending.Caster));
                RegisterSummon(view.GetZDO(), pending.PlayerId, pending.Summon);
                PendingSummons.RemoveAt(i);
            }
        }
    }

    internal static void ResetRuntimeDiscovery() => PendingSummons.Clear();

    private static void RegisterSummon(ZDO zdo, long playerId, GameObject summon)
    {
        if (zdo == null || zdo.m_uid.IsNone())
        { Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=SummonZdoUnavailable"); return; }
        if (Plugin.Transport == null)
        { Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=TransportUnavailable"); return; }
        if (Plugin.PublishSummonProvenance(zdo.m_uid, playerId))
            Plugin.MagicLog("SummonProvenanceRegistered summon=" + zdo.m_uid + " player=" + playerId +
                " prefab=" + (summon ? summon.name : "null"));
        else Plugin.MagicLog("SummonProvenanceRegistrationFailed reason=TransportUnavailable summon=" + zdo.m_uid);
    }

    internal static void ObserveSummonCommand(Tameable tameable, Humanoid user)
    {
        if (Plugin.MagicLoggingEnabled)
            Plugin.MagicLog("MagicSummonCommandEntered hook=Tameable.Command root=" + (tameable ? tameable.name : "null"));
        if (!tameable)
        {
            return;
        }
        try
        {
            Character summon = tameable.GetComponent<Character>();
            if (!IsSummonedRoot(summon)) return;
            Plugin.MagicLog("MagicSummonCommandObserved " + RuntimeFields("Tameable.Command", null, tameable.gameObject, user));
            Player caster = user as Player;
            ZNetView view = tameable.GetComponent<ZNetView>();
            ZDO zdo = view && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || zdo.m_uid.IsNone()) return;
            if (!user) return;
            ZDOID casterZdoId = user.GetZDOID();
            if (casterZdoId.IsNone() || !caster) return;
            long playerId = caster.GetPlayerID();
            if (playerId == 0) return;
            if (!Plugin.MagicLoggingEnabled) return;
            Plugin.ProbeLog?.LogInfo("MagicSummonSpawnObserved " + SummonFields(summon)
                .Text("CommandCasterZDOID", casterZdoId.ToString())
                .Text("ResolvedCasterPlayerID", caster ? Nonzero(caster.GetPlayerID()) : null)
                .Text("ResolvedCasterPlayerName", caster ? caster.GetPlayerName() : null)
                .Text("StoredFollowName", zdo == null ? null : zdo.GetString(ZDOVars.s_follow)));
        }
        catch (Exception ex) { Plugin.Warn($"MagicAttributionProbeFailed phase=SummonSpawn exception={ex.GetType().Name}"); }
    }

    internal static void ObserveSummonRpcCommand(Tameable tameable, long sender, ZDOID casterZdoId, bool message)
    {
        if (!Plugin.MagicLoggingEnabled) return;
        Plugin.MagicLog("MagicSummonRpcCommandEntered hook=Tameable.RPC_Command root=" + (tameable ? tameable.name : "null"));
        if (!tameable) return;
        Character summon = tameable.GetComponent<Character>();
        if (!IsSummonedRoot(summon)) return;
        Plugin.MagicLog("MagicSummonRpcCommandObserved " + RuntimeFields("Tameable.RPC_Command", null, tameable.gameObject, ResolveCharacter(casterZdoId))
            .Text("RpcSender", sender.ToString(CultureInfo.InvariantCulture)).Text("CommandCasterZDOID", casterZdoId.ToString()).Flag("Message", message));
    }

    internal static void ObserveFollowTarget(MonsterAI ai, GameObject target)
    {
        if (!Plugin.MagicLoggingEnabled) return;
        Plugin.MagicLog("MagicSummonFollowTargetEntered hook=MonsterAI.SetFollowTarget ai=" + (ai ? ai.name : "null"));
        if (!ai) return;
        Character summon = ai.GetComponent<Character>();
        if (!IsSummonedRoot(summon)) return;
        Plugin.MagicLog("MagicSummonFollowTargetObserved " + RuntimeFields("MonsterAI.SetFollowTarget", null, ai.gameObject,
            target ? target.GetComponent<Character>() : null));
    }

    internal static void ObserveSummonHit(Character attacker)
    {
        if (!Plugin.MagicLoggingEnabled || !IsSummonedRoot(attacker)) return;
        try
        {
            MonsterAI ai = attacker.GetComponent<MonsterAI>();
            GameObject followObject = ai ? ai.GetFollowTarget() : null;
            Player followPlayer = followObject ? followObject.GetComponent<Player>() : null;
            ZNetView view = attacker.GetComponent<ZNetView>();
            ZDO zdo = view && view.IsValid() ? view.GetZDO() : null;
            Plugin.ProbeLog?.LogInfo("MagicSummonObserved " + SummonFields(attacker)
                .Text("RuntimeFollowTargetZDOID", followPlayer ? followPlayer.GetZDOID().ToString() : null)
                .Text("RuntimeFollowPlayerID", followPlayer ? Nonzero(followPlayer.GetPlayerID()) : null)
                .Text("RuntimeFollowPlayerName", followPlayer ? followPlayer.GetPlayerName() : null)
                .Text("StoredFollowName", zdo == null ? null : zdo.GetString(ZDOVars.s_follow)));
        }
        catch (Exception ex) { Plugin.Warn($"MagicAttributionProbeFailed phase=SummonHit exception={ex.GetType().Name}"); }
    }

    private static StatusEffect GetEffect(Character victim, string type)
    {
        if (!victim) return null;
        int hash = type == "Poisoned" ? SEMan.s_statusEffectPoison : type == "Spirit" ? SEMan.s_statusEffectSpirit : SEMan.s_statusEffectBurning;
        return victim.GetSEMan().GetStatusEffect(hash);
    }

    private static float Pool(StatusEffect effect, string type)
    {
        if (!effect) return 0f;
        FieldInfo field = type == "Poisoned" ? PoisonLeft : type == "Spirit" ? SpiritLeft : FireLeft;
        return field == null ? 0f : (float)field.GetValue(effect);
    }

    private static LogFields CharacterFields(Character character)
    {
        ZNetView view = character ? character.GetComponent<ZNetView>() : null;
        ZDO zdo = view && view.IsValid() ? view.GetZDO() : null;
        return new LogFields().Text("Timestamp", Timestamp()).Text("VictimZDOID", zdo == null ? null : zdo.m_uid.ToString())
            .Text("VictimName", character ? character.m_name : null);
    }

    private static LogFields SummonFields(Character summon)
    {
        ZNetView view = summon ? summon.GetComponent<ZNetView>() : null;
        ZDO zdo = view && view.IsValid() ? view.GetZDO() : null;
        return new LogFields().Text("Timestamp", Timestamp()).Text("SummonZDOID", zdo == null ? null : zdo.m_uid.ToString())
            .Text("SummonObjectName", summon ? summon.name : null)
            .Text("SummonPrefabName", summon ? Utils.GetPrefabName(summon.gameObject) : null)
            .Text("CurrentOwnerPeerId", zdo == null ? null : zdo.GetOwner().ToString(CultureInfo.InvariantCulture))
            .Flag("IsOwner", view && view.IsValid() && view.IsOwner()).Text("CharacterName", summon ? summon.m_name : null)
            .Text("Faction", summon ? summon.GetFaction().ToString() : null);
    }

    private static LogFields RuntimeFields(string hook, SpawnAbility ability, GameObject root, Character caster)
    {
        ZNetView view = root ? root.GetComponent<ZNetView>() : null;
        ZDO zdo = view && view.IsValid() ? view.GetZDO() : null;
        Player player = caster as Player;
        return new LogFields().Text("HookName", hook).Text("AbilityObjectName", ability ? ability.name : null)
            .Text("SummonInstance", root ? root.GetInstanceID().ToString(CultureInfo.InvariantCulture) : null)
            .Text("SummonObjectName", root ? root.name : null).Text("SummonZDOID", zdo == null ? null : zdo.m_uid.ToString())
            .Flag("SummonZNetViewPresent", view).Flag("SummonZNetViewValid", view && view.IsValid()).Flag("SummonZdoPresent", zdo != null)
            .Text("CasterObjectName", caster ? caster.name : null).Text("CasterZDOID", caster ? caster.GetZDOID().ToString() : null)
            .Text("CasterPlayerID", player ? Nonzero(player.GetPlayerID()) : null).Text("CasterPlayerName", player ? player.GetPlayerName() : null)
            .Text("LocalPeerId", ZDOMan.instance == null ? null : ZDOMan.GetSessionID().ToString(CultureInfo.InvariantCulture))
            .Flag("IsHost", ZNet.instance && ZNet.instance.IsServer()).Flag("IsSinglePlayer", ZNet.instance && ZNet.IsSinglePlayer)
            .Text("SummonCurrentOwnerPeerId", zdo == null ? null : zdo.GetOwner().ToString(CultureInfo.InvariantCulture))
            .Flag("SummonIsOwner", view && view.IsValid() && view.IsOwner());
    }

    private static Character ResolveCharacter(ZDOID id)
    {
        GameObject value = !id.IsNone() && ZNetScene.instance ? ZNetScene.instance.FindInstance(id) : null;
        return value ? value.GetComponent<Character>() : null;
    }

    private static bool IsSummonedRoot(Character character)
    {
        if (!character) return false;
        string value = (character.m_name + " " + character.name + " " + Utils.GetPrefabName(character.gameObject)).ToLowerInvariant();
        return IsRootText(value);
    }

    private static bool IsRootText(string value)
    {
        value = (value ?? "").ToLowerInvariant();
        return value.Contains("summonedroot") || value.Contains("greenroots") || value.Contains("tentaroot");
    }

    private static string Timestamp() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static string Nonzero(long value) => value == 0L ? null : value.ToString(CultureInfo.InvariantCulture);
}

[HarmonyPatch(typeof(Character), "RPC_Damage")]
internal static class MagicDamageRpcContextPatch
{
    [HarmonyPrefix] private static void Prefix(HitData hit, out MagicAttributionProbe.RpcContext __state) => __state = MagicAttributionProbe.BeginRpc(hit);
    [HarmonyFinalizer] private static Exception Finalizer(Exception __exception, MagicAttributionProbe.RpcContext __state)
    { MagicAttributionProbe.EndRpc(__state); return __exception; }
}

[HarmonyPatch(typeof(Character), "AddFireDamage")]
internal static class MagicFireAppliedPatch
{
    [HarmonyPrefix] private static void Prefix(Character __instance, float damage, out MagicAttributionProbe.DotState __state) => __state = MagicAttributionProbe.BeginDot(__instance, "Burning", damage);
    [HarmonyPostfix] private static void Postfix(Character __instance, MagicAttributionProbe.DotState __state) => MagicAttributionProbe.EndDot(__instance, __state);
}

[HarmonyPatch(typeof(Character), "AddSpiritDamage")]
internal static class MagicSpiritAppliedPatch
{
    [HarmonyPrefix] private static void Prefix(Character __instance, float damage, out MagicAttributionProbe.DotState __state) => __state = MagicAttributionProbe.BeginDot(__instance, "Spirit", damage);
    [HarmonyPostfix] private static void Postfix(Character __instance, MagicAttributionProbe.DotState __state) => MagicAttributionProbe.EndDot(__instance, __state);
}

[HarmonyPatch(typeof(Character), "AddPoisonDamage")]
internal static class MagicPoisonAppliedPatch
{
    [HarmonyPrefix] private static void Prefix(Character __instance, float damage, out MagicAttributionProbe.DotState __state) => __state = MagicAttributionProbe.BeginDot(__instance, "Poisoned", damage);
    [HarmonyPostfix] private static void Postfix(Character __instance, MagicAttributionProbe.DotState __state) => MagicAttributionProbe.EndDot(__instance, __state);
}

[HarmonyPatch(typeof(SpawnAbility), nameof(SpawnAbility.Setup))]
internal static class MagicSpawnAbilityPatch
{
    [HarmonyPostfix] private static void Postfix(SpawnAbility __instance, Character owner) => MagicAttributionProbe.ObserveSpawnAbility(__instance, owner);
}

[HarmonyPatch(typeof(Tameable), nameof(Tameable.Command), new[] { typeof(Humanoid), typeof(bool) })]
internal static class MagicSummonCommandPatch
{
    [HarmonyPrefix] private static void Prefix(Tameable __instance, Humanoid user) => MagicAttributionProbe.ObserveSummonCommand(__instance, user);
}

[HarmonyPatch]
internal static class MagicSummonSpawnCoroutinePatch
{
    private static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SpawnAbility), "Spawn"));

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        FieldInfo abilityField = null;
        foreach (FieldInfo field in __originalMethod.DeclaringType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (field.FieldType == typeof(SpawnAbility)) { abilityField = field; break; }
        MethodInfo observer = AccessTools.Method(typeof(MagicAttributionProbe), nameof(MagicAttributionProbe.ObserveSpawnCreated));
        bool injected = false;
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction;
            if (!injected && abilityField != null && instruction.operand is MethodInfo method &&
                method.Name == "Instantiate" && method.DeclaringType == typeof(UnityEngine.Object) && method.ReturnType == typeof(GameObject))
            {
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, abilityField);
                yield return new CodeInstruction(OpCodes.Call, observer);
                injected = true;
            }
        }
        if (!injected) Plugin.Warn("MagicSummonHookInstallFailed hook=SpawnAbility.Spawn.MoveNext reason=InstantiateCallNotFound");
    }
}

[HarmonyPatch(typeof(Tameable), "RPC_Command", new[] { typeof(long), typeof(ZDOID), typeof(bool) })]
internal static class MagicSummonRpcCommandPatch
{
    [HarmonyPrefix] private static void Prefix(Tameable __instance, long sender, ZDOID characterID, bool message) =>
        MagicAttributionProbe.ObserveSummonRpcCommand(__instance, sender, characterID, message);
}

[HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.SetFollowTarget), new[] { typeof(GameObject) })]
internal static class MagicSummonFollowTargetPatch
{
    [HarmonyPrefix] private static void Prefix(MonsterAI __instance, GameObject go) => MagicAttributionProbe.ObserveFollowTarget(__instance, go);
}
