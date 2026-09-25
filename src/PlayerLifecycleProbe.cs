using System;
using System.Globalization;
using HarmonyLib;

namespace DiagnosticDamageProbe;

internal static class PlayerLifecycleProbe
{
    internal static void Observe(string marker, string hookName, Player player, long? rpcSender = null,
        bool? afterDeath = null)
    {
        if (!Plugin.LifecycleLoggingEnabled) return;
        try
        {
            ZNet net = ZNet.instance;
            ZNetView view = player ? player.GetComponent<ZNetView>() : null;
            ZDO zdo = view && view.IsValid() ? view.GetZDO() : null;
            long localPeer = ZDOMan.instance == null ? 0L : ZDOMan.GetSessionID();

            var fields = new LogFields()
                .Text("Timestamp", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Text("ProcessRunId", Plugin.ProcessRunId)
                .Text("HookName", hookName)
                .Text("LocalPeerId", localPeer.ToString(CultureInfo.InvariantCulture))
                .Flag("IsHost", net && net.IsServer())
                .Text("PlayerID", player ? Nonzero(player.GetPlayerID()) : null)
                .Text("PlayerName", player ? player.GetPlayerName() : null)
                .Text("PlayerZDOID", zdo == null ? null : zdo.m_uid.ToString())
                .Text("CurrentOwnerPeerId", zdo == null ? null : zdo.GetOwner().ToString(CultureInfo.InvariantCulture))
                .Flag("IsOwner", view && view.IsValid() && view.IsOwner())
                .Flag("IsLocalPlayer", player && ReferenceEquals(Player.m_localPlayer, player))
                .Text("CharacterInstanceIdentity", player ? player.GetInstanceID().ToString(CultureInfo.InvariantCulture) : null)
                .Flag("IsDead", player && player.IsDead())
                .Number("Health", player ? player.GetHealth() : 0f)
                .Flag("ZNetViewPresent", view)
                .Flag("ZNetViewValid", view && view.IsValid())
                .Flag("ZdoPresent", zdo != null);

            if (rpcSender.HasValue) fields.Text("RpcSender", rpcSender.Value.ToString(CultureInfo.InvariantCulture));
            if (afterDeath.HasValue) fields.Flag("AfterDeath", afterDeath.Value);
            Plugin.ProbeLog?.LogInfo(marker + " " + fields);
        }
        catch (Exception ex)
        {
            Plugin.Warn($"PlayerLifecycleProbeFailed hook={hookName} exception={ex.GetType().Name}");
        }
    }

    private static string Nonzero(long value) => value == 0L ? null : value.ToString(CultureInfo.InvariantCulture);

    internal static void CommitDeath(Player player)
    {
        try
        {
            long playerId = player ? player.GetPlayerID() : 0L;
            ZNetView view = player ? player.GetComponent<ZNetView>() : null;
            ZDO zdo = view && view.IsValid() ? view.GetZDO() : null;
            string incarnation = zdo == null ? null : zdo.m_uid.ToString();
            DamageCommitTransport.DeathObservation outcome = Plugin.Transport == null
                ? new DamageCommitTransport.DeathObservation("NoActiveSession", Encounter.EncounterState.NoEncounter,
                    Encounter.EncounterState.NoEncounter)
                : Plugin.Transport.ObservePlayerDeath(playerId, incarnation);

            if (!Plugin.LifecycleLoggingEnabled) return;
            ZNet net = ZNet.instance;
            string marker = outcome.Result == Encounter.PlayerDeathResult.Committed.ToString()
                ? "PlayerLifecycleDeathCommitted" : "PlayerLifecycleDeathIgnored";
            var fields = new LogFields()
                .Text("Timestamp", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Text("PlayerID", playerId == 0L ? null : playerId.ToString(CultureInfo.InvariantCulture))
                .Text("PlayerName", player ? player.GetPlayerName() : null)
                .Text("PlayerZDOID", zdo == null ? null : zdo.m_uid.ToString())
                .Flag("IsLocalPlayer", player && ReferenceEquals(Player.m_localPlayer, player))
                .Flag("IsOwner", view && view.IsValid() && view.IsOwner())
                .Flag("IsHost", net && net.IsServer() && !net.IsDedicated())
                .Text("EncounterStateBefore", outcome.StateBefore.ToString())
                .Text("EncounterStateAfter", outcome.StateAfter.ToString());
            if (marker == "PlayerLifecycleDeathIgnored") fields.Text("Reason", outcome.Result);
            Plugin.ProbeLog?.LogInfo(marker + " " + fields);
        }
        catch (Exception ex)
        {
            Plugin.Warn($"PlayerLifecycleProbeFailed hook=Player.RPC_OnDeath.Commit exception={ex.GetType().Name}");
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
internal static class PlayerOnDeathLifecyclePatch
{
    [HarmonyPrefix]
    private static void Prefix(Player __instance) =>
        PlayerLifecycleProbe.Observe("PlayerLifecycleDeathObserved", "Player.OnDeath.Prefix", __instance);
}

[HarmonyPatch(typeof(Player), "RPC_OnDeath")]
internal static class PlayerRpcOnDeathLifecyclePatch
{
    [HarmonyPrefix]
    private static void Prefix(Player __instance, long sender)
    {
        PlayerLifecycleProbe.Observe("PlayerLifecycleDeathObserved", "Player.RPC_OnDeath.Prefix", __instance, sender);
        PlayerLifecycleProbe.CommitDeath(__instance);
    }
}

[HarmonyPatch(typeof(Game), "SpawnPlayer")]
internal static class GameSpawnPlayerLifecyclePatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __result, bool ___m_respawnAfterDeath)
    {
        if (___m_respawnAfterDeath)
            PlayerLifecycleProbe.Observe("PlayerLifecycleRespawnObserved", "Game.SpawnPlayer.Postfix", __result,
                afterDeath: true);
    }
}

[HarmonyPatch(typeof(Player), "Start")]
internal static class PlayerStartLifecyclePatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __instance) =>
        PlayerLifecycleProbe.Observe("PlayerLifecycleInstanceObserved", "Player.Start.Postfix", __instance);
}

[HarmonyPatch(typeof(Player), "OnDestroy")]
internal static class PlayerDestroyLifecyclePatch
{
    [HarmonyPrefix]
    private static void Prefix(Player __instance) =>
        PlayerLifecycleProbe.Observe("PlayerLifecycleInstanceObserved", "Player.OnDestroy.Prefix", __instance);
}
