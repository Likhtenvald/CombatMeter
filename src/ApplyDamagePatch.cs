using System;
using System.Globalization;
using HarmonyLib;
using DiagnosticDamageProbe.Transport;

namespace DiagnosticDamageProbe;

[HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage),
    new[] { typeof(HitData), typeof(bool), typeof(bool), typeof(HitData.DamageModifier) })]
internal static class ApplyDamagePatch
{
    internal sealed class CallState
    {
        internal readonly ZNetView View;
        internal readonly ZDO Zdo;
        internal readonly long Session;
        internal readonly long Owner;
        internal readonly float HpBefore;
        internal readonly LogFields Fields;
        internal readonly DamageFacts Facts;

        internal CallState(ZNetView view, ZDO zdo, long session, float hpBefore, LogFields fields, DamageFacts facts)
        {
            View = view;
            Zdo = zdo;
            Session = session;
            Owner = zdo.GetOwner();
            HpBefore = hpBefore;
            Fields = fields;
            Facts = facts;
        }
    }

    // __state is a local of Harmony's generated wrapper for THIS invocation.
    // Nested calls have their own state. No shared current-hit field, stack or Finalizer needed.
    [HarmonyPrefix]
    internal static void Prefix(Character __instance, HitData hit, out CallState __state)
    {
        __state = null;
        // Observation is independent of logging switches: transport must not lose damage.
        try
        {
            if (!__instance) return;
            ZNetView view = __instance.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || !view.IsOwner()) return;
            ZDO zdo = view.GetZDO();
            if (zdo == null || ZDOMan.instance == null) return;

            float hpBefore = __instance.GetHealth();
            long session = ZDOMan.GetSessionID();
            ZNet net = ZNet.instance;
            bool server = net && net.IsServer();
            bool dedicated = net && net.IsDedicated();
            bool singlePlayer = net && ZNet.IsSinglePlayer;
            string role = !net ? "Unknown" : dedicated ? "DedicatedServer" :
                !server ? "Client" : singlePlayer ? "SinglePlayer" : "ListenHost";

            var fields = new LogFields()
                .Text("TimestampUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Text("CallId", Guid.NewGuid().ToString("N"))
                .Text("ProcessRunId", Plugin.ProcessRunId)
                .Text("PeerSessionId", session.ToString(CultureInfo.InvariantCulture))
                .Text("Role", role)
                .Flag("IsServer", server).Flag("IsDedicated", dedicated).Flag("IsSinglePlayer", singlePlayer)
                .Text("VictimName", __instance.m_name)
                .Text("VictimObjectName", __instance.name)
                .Text("VictimType", __instance.GetType().FullName)
                .Flag("VictimIsPlayer", __instance.IsPlayer())
                .Text("VictimZDOID", zdo.m_uid.ToString())
                .Text("VictimOwnerBefore", zdo.GetOwner().ToString(CultureInfo.InvariantCulture))
                .Flag("IsOwnerBefore", view.IsOwner())
                .Number("HPBefore", hpBefore)
                .Text("HitType", hit?.m_hitType.ToString())
                .Text("HitDataState", hit == null ? "Null" : "Present");

            if (hit != null)
            {
                // Snapshot BEFORE original: ApplyDamage mutates damage via final multipliers.
                fields.Number("HitTotalDamageBefore", hit.GetTotalDamage());
                HitData.DamageTypes d = hit.m_damage;
                fields.Number("GenericBefore", d.m_damage).Number("BluntBefore", d.m_blunt)
                    .Number("SlashBefore", d.m_slash).Number("PierceBefore", d.m_pierce)
                    .Number("ChopBefore", d.m_chop).Number("PickaxeBefore", d.m_pickaxe)
                    .Number("FireBefore", d.m_fire).Number("FrostBefore", d.m_frost)
                    .Number("LightningBefore", d.m_lightning).Number("PoisonBefore", d.m_poison)
                    .Number("SpiritBefore", d.m_spirit).Number("NonPlayerBefore", d.m_nonPlayer);
            }

            Character attacker = hit?.GetAttacker();
            MagicAttributionProbe.ObserveDotTick(__instance, hit);
            MagicAttributionProbe.ObserveSummonHit(attacker);
            Player player = attacker as Player;
            string classification = hit == null || hit.m_attacker.IsNone() ? "None" :
                !attacker ? "Unresolved" : player ? "Player" : "NPC";
            fields.Text("AttackerZDOID", hit == null ? null : hit.m_attacker.ToString())
                .Text("AttackerClass", classification)
                .Text("GetAttackerResult", attacker ? attacker.GetType().FullName : "null")
                .Text("AttackerName", attacker ? attacker.m_name : null)
                .Text("AttackerObjectName", attacker ? attacker.name : null)
                .Text("AttackerPlayerID", player ? player.GetPlayerID().ToString(CultureInfo.InvariantCulture) : null)
                .Text("AttackerPlayerName", player ? player.GetPlayerName() : null);

            Player victimPlayer = __instance as Player;
            long? victimId = victimPlayer ? Nonzero(victimPlayer.GetPlayerID()) : null;
            long? attackerId = player ? Nonzero(player.GetPlayerID()) : null;
            var facts = new DamageFacts(zdo.m_uid.UserID, zdo.m_uid.ID, __instance.IsPlayer(), victimId,
                (AttackerClass)Enum.Parse(typeof(AttackerClass), classification), attackerId,
                hit == null ? (byte)0 : (byte)hit.m_hitType,
                victimPlayer ? victimPlayer.GetPlayerName() : __instance.m_name,
                player ? player.GetPlayerName() : attacker ? attacker.m_name : null,
                hit == null || hit.m_attacker.IsNone() ? 0 : hit.m_attacker.UserID,
                hit == null || hit.m_attacker.IsNone() ? 0u : hit.m_attacker.ID,
                hit == null ? (byte)0 : hit.m_hitType == HitData.HitType.Poisoned ? (byte)1 :
                    hit.m_hitType == HitData.HitType.Burning && hit.m_damage.m_spirit > 0f && hit.m_damage.m_fire <= 0f ? (byte)3 :
                    hit.m_hitType == HitData.HitType.Burning ? (byte)2 : (byte)0);
            __state = new CallState(view, zdo, session, hpBefore, fields, facts);
        }
        catch (Exception ex)
        {
            __state = null;
            Plugin.Warn($"DamageProbeCaptureFailed phase=Prefix exception={ex.GetType().Name}");
        }
    }

    [HarmonyPostfix]
    internal static void Postfix(Character __instance, CallState __state)
    {
        if (__state == null) return;
        try
        {
            // Do not read a fallback max HP after destruction or attribute a different ZDO/session.
            if (!__instance || !__state.View || !__state.View.IsValid() ||
                !ReferenceEquals(__state.Zdo, __state.View.GetZDO()) ||
                ZDOMan.instance == null || ZDOMan.GetSessionID() != __state.Session ||
                !__state.View.IsOwner() || __state.Zdo.GetOwner() != __state.Owner)
            {
                Plugin.Warn("DamageProbeObservationLost reason=VictimInvalidOrOwnershipChanged");
                return;
            }

            float hpAfter = __instance.GetHealth();
            if (!IsFinite(__state.HpBefore) || !IsFinite(hpAfter))
            {
                Plugin.Warn("DamageProbeObservationLost reason=NonFiniteHP");
                return;
            }
            float loss = Math.Max(0f, __state.HpBefore - hpAfter);
            if (!(loss > 0f) || !IsFinite(loss)) return;

            Plugin.Publish(__state.Session, __state.Facts, loss);

            __state.Fields
                .Text("CompletedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Text("VictimOwner", __state.Zdo.GetOwner().ToString(CultureInfo.InvariantCulture))
                .Flag("IsOwner", __state.View.IsOwner())
                .Number("HPAfter", hpAfter).Number("EffectiveHpLoss", loss);
            if (Plugin.LoggingEnabled) Plugin.ProbeLog?.LogInfo("DamageProbeEvent " + __state.Fields);
        }
        catch (Exception ex)
        {
            Plugin.Warn($"DamageProbeCaptureFailed phase=Postfix exception={ex.GetType().Name}");
        }
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static long? Nonzero(long value) => value == 0 ? (long?)null : value;
}
