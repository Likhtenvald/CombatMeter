using HarmonyLib;
using UnityEngine;

namespace DiagnosticDamageProbe.UI;

[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
internal static class CombatMeterCursorPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (!Plugin.UiEditing) return;
        ZCursor.LockState = CursorLockMode.None;
        ZCursor.Show();
    }
}

[HarmonyPatch(typeof(PlayerController), "TakeInput", new[] { typeof(bool) })]
internal static class CombatMeterPlayerInputPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref bool __result)
    {
        if (Plugin.UiEditing) __result = false;
    }
}

[HarmonyPatch(typeof(Menu), "Update")]
internal static class CombatMeterEscapePatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (Plugin.ShouldConsumeUiEscape()) return false;
        if (!Plugin.UiEditing || !ZInput.GetKeyDown(KeyCode.Escape)) return true;
        Plugin.ExitUiEditMode("Escape");
        return false;
    }
}
