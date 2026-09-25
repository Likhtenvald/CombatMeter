using HarmonyLib;

namespace DiagnosticDamageProbe;

[HarmonyPatch(typeof(ZNet), "Awake")]
internal static class NetworkStartPatch
{
    [HarmonyPostfix] private static void Postfix(ZNet __instance) => Plugin.SafeTransport(() => Plugin.Transport?.Bind(__instance));
}

[HarmonyPatch(typeof(ZNet), "StopAll")]
internal static class NetworkStopPatch
{
    [HarmonyPrefix] private static void Prefix(ZNet __instance)
    { Plugin.DestroyUi(); MagicAttributionProbe.ResetRuntimeDiscovery(); Plugin.SafeTransport(() => Plugin.Transport?.Stop(__instance)); }
}

[HarmonyPatch(typeof(ZNet), "OnDestroy")]
internal static class NetworkDestroyPatch
{
    [HarmonyPrefix] private static void Prefix(ZNet __instance)
    { Plugin.DestroyUi(); MagicAttributionProbe.ResetRuntimeDiscovery(); Plugin.SafeTransport(() => Plugin.Transport?.Stop(__instance)); }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
internal static class NetworkDisconnectPatch
{
    [HarmonyPostfix] private static void Postfix(ZNet __instance)
    { Plugin.DestroyUi(); MagicAttributionProbe.ResetRuntimeDiscovery(); Plugin.SafeTransport(() => Plugin.Transport?.Disconnected(__instance)); }
}
