using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using DiagnosticDamageProbe.Transport;
using DiagnosticDamageProbe.Attribution;
using DiagnosticDamageProbe.UI;
using DiagnosticDamageProbe.Configuration;
using BepInEx.Bootstrap;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace DiagnosticDamageProbe;

[BepInPlugin(PluginId, PublicIdentity.PluginName, BuildInfo.Version)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginId = PublicIdentity.PluginGuid;
    public const string LegacyPluginId = PublicIdentity.LegacyPluginGuid;
    public const string PluginVersion = BuildInfo.Version;

    internal static ConfigEntry<bool> EnableDiagnosticLogging;
    internal static ConfigEntry<bool> EnableTransportDiagnosticLogging;
    internal static ConfigEntry<bool> EnableLifecycleDiagnosticLogging;
    internal static ConfigEntry<bool> EnableMagicAttributionDiagnosticLogging;
    internal static ConfigEntry<bool> EnablePerformanceDiagnosticLogging;
    internal static ConfigEntry<bool> UiEnabled;
    internal static ConfigEntry<KeyboardShortcut> UiToggleKey;
    internal static ConfigEntry<KeyboardShortcut> UiEditModeKey;
    internal static ConfigEntry<float> UiPositionX;
    internal static ConfigEntry<float> UiPositionY;
    internal static ConfigEntry<float> UiScale;
    internal static ConfigEntry<float> UiWindowWidth;
    internal static ConfigEntry<float> UiBackgroundOpacity;
    internal static ConfigEntry<bool> UiShowDamageBars;
    internal static ConfigEntry<bool> UiShowDamagePercent;
    internal static ConfigEntry<float> UiDamageBarOpacity;
    internal static ConfigEntry<float> CombatTimeout;
    internal static ConfigEntry<float> RecoveryTimeout;
    internal static ConfigEntry<float> DpsIdleTimeout;
    internal static DamageCommitTransport Transport;
    private static Plugin Instance;
    internal static ManualLogSource ProbeLog;
    internal static readonly string ProcessRunId = Guid.NewGuid().ToString("N");
    private Harmony _harmony;
    private CombatMeterUiController _ui;
    private static bool _uiFailureLogged;
    private static int _uiEscapeConsumedFrame = -1;

    internal static bool LoggingEnabled => EnableDiagnosticLogging?.Value == true;
    internal static bool LifecycleLoggingEnabled => EnableLifecycleDiagnosticLogging?.Value == true;
    internal static bool MagicLoggingEnabled => EnableMagicAttributionDiagnosticLogging?.Value == true;
    internal static bool UiEditing => Instance?._ui?.IsEditing == true;

    private void Awake()
    {
        Instance = this;
        ProbeLog = Logger;
        Dictionary<string, string> legacy = LoadLegacyConfig();
        EnableDiagnosticLogging = Config.Bind("Diagnostics", "EnableDiagnosticLogging", false,
            "Logs detailed owner-side HP observations on this process only. Turning this off does not disable DamageCommit transport.");
        EnableTransportDiagnosticLogging = Config.Bind("Diagnostics", "EnableTransportDiagnosticLogging", false,
            "Logs DamageCommit creation, acceptance, rejection and acknowledgements on this process only. Does not disable transport.");
        EnableLifecycleDiagnosticLogging = Config.Bind("Diagnostics", "EnableLifecycleDiagnosticLogging", false,
            "Logs Player death, respawn and instance lifecycle candidates on this process only. Does not update encounters.");
        EnableMagicAttributionDiagnosticLogging = Config.Bind("Diagnostics", "EnableMagicAttributionDiagnosticLogging", false,
            "Logs summoned-root and elemental DoT provenance candidates on this process only. Does not change attribution.");
        EnablePerformanceDiagnosticLogging = Config.Bind("Diagnostics", "EnablePerformanceDiagnosticLogging", false,
            "Collects and logs aggregated CombatMeter performance metrics on the host. Disabled by default. Does not change combat, routing, snapshots or gameplay.");
        EnablePerformanceDiagnosticLogging.SettingChanged += OnPerformanceLoggingChanged;
        UiEnabled = Config.Bind("UI", "UI Enabled", true, "Shows the Combat Meter on this client only.");
        UiToggleKey = Config.Bind("UI", "Toggle Key", new KeyboardShortcut(KeyCode.F8),
            "Shows or hides the Combat Meter on this client only. New snapshots do not override a manual hide.");
        UiEditModeKey = Config.Bind("UI", "Edit Mode Key", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
            "Enters or exits edit mode on this client only. Escape also exits; F8 visibility is ignored while editing.");
        UiScale = Config.Bind("UI", "UI Scale", CombatMeterLayout.DefaultScale,
            new ConfigDescription("Changes the size of the Combat Meter on this client only; applied live.", new AcceptableValueRange<float>(CombatMeterLayout.MinScale, CombatMeterLayout.MaxScale)));
        UiWindowWidth = Config.Bind("UI", "Window Width", CombatMeterLayout.DefaultWidth,
            new ConfigDescription("Changes the Combat Meter width on this client only; applied live.", new AcceptableValueRange<float>(CombatMeterLayout.MinWidth, CombatMeterLayout.MaxWidth)));
        UiBackgroundOpacity = Config.Bind("UI", "Background Opacity", CombatMeterLayout.DefaultOpacity,
            new ConfigDescription("Changes panel background opacity on this client only; applied live.", new AcceptableValueRange<float>(0f, 1f)));
        UiShowDamageBars = Config.Bind("UI", "Show Damage Bars", true, "Shows damage contribution bars on this client only.");
        UiShowDamagePercent = Config.Bind("UI", "Show Damage Percent", true, "Shows the damage contribution percentage on this client only.");
        UiDamageBarOpacity = Config.Bind("UI", "Damage Bar Opacity", 0.25f,
            new ConfigDescription("Changes contribution bar opacity on this client only; applied live.", new AcceptableValueRange<float>(0f, 1f)));
        UiPositionX = Config.Bind("UI Position", "X", CombatMeterLayout.DefaultX, "Saved horizontal HUD position on this client only.");
        UiPositionY = Config.Bind("UI Position", "Y", CombatMeterLayout.DefaultY, "Saved vertical HUD position on this client only.");
        CombatTimeout = Config.Bind("Combat", "Combat Timeout", (float)Encounter.EncounterSettings.DefaultSoftTimeoutSeconds,
            new ConfigDescription("How long combat can remain inactive before the current encounter ends. The host's value is used for the whole session.",
                new AcceptableValueRange<float>((float)Encounter.EncounterSettings.MinSoftTimeoutSeconds, (float)Encounter.EncounterSettings.MaxSoftTimeoutSeconds)));
        RecoveryTimeout = Config.Bind("Combat", "Recovery Timeout", (float)Encounter.EncounterSettings.DefaultRecoveryTimeoutSeconds,
            new ConfigDescription("How long a dead player may return to the same encounter before their recovery period expires. The host's value is used for the whole session.",
                new AcceptableValueRange<float>((float)Encounter.EncounterSettings.MinRecoveryTimeoutSeconds, (float)Encounter.EncounterSettings.MaxRecoveryTimeoutSeconds)));
        DpsIdleTimeout = Config.Bind("Combat", "DPS Idle Timeout", (float)Encounter.EncounterSettings.DefaultDpsIdleTimeoutSeconds,
            new ConfigDescription("How long after your last damaging PvE event time continues to count toward your DPS. Longer idle gaps do not reduce DPS. The host's value is used for the whole session.",
                new AcceptableValueRange<float>((float)Encounter.EncounterSettings.MinDpsIdleTimeoutSeconds, (float)Encounter.EncounterSettings.MaxDpsIdleTimeoutSeconds)));
        ApplyLegacyConfig(legacy);
        WarnIfLegacyPluginLoaded();
        Transport = new DamageCommitTransport(softTimeout: () => CombatTimeout?.Value ?? 20f,
            recoveryTimeout: () => RecoveryTimeout?.Value ?? 180f,
            dpsIdleTimeout: () => DpsIdleTimeout?.Value ?? 6f);
        _ui = new CombatMeterUiController();
        _harmony = new Harmony(PluginId);
        _harmony.PatchAll(typeof(ApplyDamagePatch));
        _harmony.PatchAll(typeof(NetworkStartPatch));
        _harmony.PatchAll(typeof(NetworkStopPatch));
        _harmony.PatchAll(typeof(NetworkDestroyPatch));
        _harmony.PatchAll(typeof(NetworkDisconnectPatch));
        _harmony.PatchAll(typeof(PlayerOnDeathLifecyclePatch));
        _harmony.PatchAll(typeof(PlayerRpcOnDeathLifecyclePatch));
        _harmony.PatchAll(typeof(GameSpawnPlayerLifecyclePatch));
        _harmony.PatchAll(typeof(PlayerStartLifecyclePatch));
        _harmony.PatchAll(typeof(PlayerDestroyLifecyclePatch));
        _harmony.PatchAll(typeof(MagicDamageRpcContextPatch));
        _harmony.PatchAll(typeof(MagicFireAppliedPatch));
        _harmony.PatchAll(typeof(MagicSpiritAppliedPatch));
        _harmony.PatchAll(typeof(MagicPoisonAppliedPatch));
        _harmony.PatchAll(typeof(MagicSpawnAbilityPatch));
        _harmony.PatchAll(typeof(MagicSummonCommandPatch));
        _harmony.PatchAll(typeof(MagicSummonSpawnCoroutinePatch));
        _harmony.PatchAll(typeof(MagicSummonRpcCommandPatch));
        _harmony.PatchAll(typeof(MagicSummonFollowTargetPatch));
        _harmony.PatchAll(typeof(CombatMeterCursorPatch));
        _harmony.PatchAll(typeof(CombatMeterPlayerInputPatch));
        _harmony.PatchAll(typeof(CombatMeterEscapePatch));
        LogMagicHook("SpawnAbility.Setup", AccessTools.Method(typeof(SpawnAbility), nameof(SpawnAbility.Setup)));
        LogMagicHook("Tameable.Command", AccessTools.Method(typeof(Tameable), nameof(Tameable.Command), new[] { typeof(Humanoid), typeof(bool) }));
        LogMagicHook("Tameable.RPC_Command", AccessTools.Method(typeof(Tameable), "RPC_Command", new[] { typeof(long), typeof(ZDOID), typeof(bool) }));
        LogMagicHook("MonsterAI.SetFollowTarget", AccessTools.Method(typeof(MonsterAI), nameof(MonsterAI.SetFollowTarget), new[] { typeof(UnityEngine.GameObject) }));
        LogMagicHook("SpawnAbility.Spawn.MoveNext", AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SpawnAbility), "Spawn")));
        SafeTransport(() => Transport.Bind(ZNet.instance));
        Logger.LogInfo($"CombatMeterReady version={PluginVersion} run={ProcessRunId} " +
            $"valheim={(global::Version.CurrentVersion)} enabled={LoggingEnabled} " +
            "target=Character.ApplyDamage(HitData,bool,bool,HitData.DamageModifier)");
    }

    private Dictionary<string, string> LoadLegacyConfig()
    {
        try
        {
            string oldPath = Path.Combine(Paths.ConfigPath, LegacyConfigMigration.LegacyFileName);
            if (!LegacyConfigMigration.ShouldMigrate(oldPath, Config.ConfigFilePath)) return null;
            return LegacyConfigMigration.ParseKnown(File.ReadAllText(oldPath));
        }
        catch (Exception ex) { Warn("CombatMeterConfigMigrationSkipped reason=" + ex.GetType().Name); return null; }
    }

    private void ApplyLegacyConfig(Dictionary<string, string> values)
    {
        if (values == null || values.Count == 0) return;
        int applied = 0;
        applied += Apply(values, "Diagnostics", "EnableDiagnosticLogging", EnableDiagnosticLogging);
        applied += Apply(values, "Diagnostics", "EnableTransportDiagnosticLogging", EnableTransportDiagnosticLogging);
        applied += Apply(values, "Diagnostics", "EnableLifecycleDiagnosticLogging", EnableLifecycleDiagnosticLogging);
        applied += Apply(values, "Diagnostics", "EnableMagicAttributionDiagnosticLogging", EnableMagicAttributionDiagnosticLogging);
        applied += Apply(values, "Diagnostics", "EnablePerformanceDiagnosticLogging", EnablePerformanceDiagnosticLogging);
        applied += Apply(values, "UI", "UI Enabled", UiEnabled); applied += Apply(values, "UI", "Toggle Key", UiToggleKey);
        applied += Apply(values, "UI", "Edit Mode Key", UiEditModeKey); applied += Apply(values, "UI", "UI Scale", UiScale);
        applied += Apply(values, "UI", "Window Width", UiWindowWidth); applied += Apply(values, "UI", "Background Opacity", UiBackgroundOpacity);
        applied += Apply(values, "UI", "Show Damage Bars", UiShowDamageBars); applied += Apply(values, "UI", "Show Damage Percent", UiShowDamagePercent);
        applied += Apply(values, "UI", "Damage Bar Opacity", UiDamageBarOpacity); applied += Apply(values, "UI Position", "X", UiPositionX);
        applied += Apply(values, "UI Position", "Y", UiPositionY); applied += Apply(values, "Combat", "Combat Timeout", CombatTimeout);
        applied += Apply(values, "Combat", "Recovery Timeout", RecoveryTimeout); applied += Apply(values, "Combat", "DPS Idle Timeout", DpsIdleTimeout);
        Config.Save(); Logger.LogInfo("CombatMeterConfigMigrated source=" + LegacyConfigMigration.LegacyFileName + " values=" + applied);
    }

    private static int Apply<T>(Dictionary<string, string> values, string section, string key, ConfigEntry<T> entry)
    {
        if (!LegacyConfigMigration.TryGet(values, section, key, out string raw)) return 0;
        try { entry.Value = (T)TomlTypeConverter.ConvertToValue(raw, typeof(T)); return 1; }
        catch { return 0; }
    }

    private static void WarnIfLegacyPluginLoaded()
    {
        try
        {
            if (Chainloader.PluginInfos.ContainsKey(LegacyPluginId))
                Warn("CombatMeterLegacyPluginDetected remove DiagnosticDamageProbe.dll to avoid duplicate patches and statistics");
        }
        catch { }
    }

    private void OnDestroy()
    {
        SafeUi(() => { _ui?.ExitEditMode("Lifecycle", PersistUiPosition); _ui?.Destroy(); });
        SafeTransport(() => Transport?.Dispose());
        MagicAttributionProbe.ResetRuntimeDiscovery();
        Transport = null;
        _harmony?.UnpatchSelf();
        EnableDiagnosticLogging = null;
        EnableTransportDiagnosticLogging = null;
        EnableLifecycleDiagnosticLogging = null;
        EnableMagicAttributionDiagnosticLogging = null;
        if (EnablePerformanceDiagnosticLogging != null)
            EnablePerformanceDiagnosticLogging.SettingChanged -= OnPerformanceLoggingChanged;
        EnablePerformanceDiagnosticLogging = null;
        UiEnabled = null;
        UiToggleKey = null;
        UiEditModeKey = null;
        UiPositionX = null;
        UiPositionY = null;
        UiScale = null;
        UiWindowWidth = null;
        UiBackgroundOpacity = null;
        UiShowDamageBars = null;
        UiShowDamagePercent = null;
        UiDamageBarOpacity = null;
        CombatTimeout = null;
        RecoveryTimeout = null;
        DpsIdleTimeout = null;
        _ui = null;
        Instance = null;
        ProbeLog = null;
    }

    private void Update()
    {
        SafeTransport(() => Transport?.Update());
        MagicAttributionProbe.UpdatePendingRoots();
        bool worldReady = ZNet.instance && !ZNet.instance.IsDedicated() && ZDOMan.instance != null &&
            ZDOMan.GetSessionID() != 0L && Player.m_localPlayer && Transport?.HasActiveSession == true;
        KeyboardShortcut shortcut = UiToggleKey?.Value ?? new KeyboardShortcut(KeyCode.F8);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool reset = shift && shortcut.MainKey != KeyCode.None && Input.GetKeyDown(shortcut.MainKey);
        bool editToggle = !reset && (UiEditModeKey?.Value.IsDown() == true);
        bool toggle = !reset && !editToggle && shortcut.IsDown();
        bool escape = UiEditing && Input.GetKeyDown(KeyCode.Escape);
        bool vanillaModal = IsVanillaModalVisible();
        SafeUi(() => _ui?.Tick(Transport?.SnapshotStore, worldReady, UiEnabled?.Value == true, toggle, reset,
            editToggle, escape, vanillaModal,
            UiPositionX?.Value ?? CombatMeterLayout.DefaultX, UiPositionY?.Value ?? CombatMeterLayout.DefaultY,
            UiScale?.Value ?? CombatMeterLayout.DefaultScale, UiWindowWidth?.Value ?? CombatMeterLayout.DefaultWidth,
            UiBackgroundOpacity?.Value ?? CombatMeterLayout.DefaultOpacity,
            UiShowDamageBars?.Value != false, UiShowDamagePercent?.Value != false,
            UiDamageBarOpacity?.Value ?? 0.25f, PersistUiPosition));
    }

    private void PersistUiPosition(float x, float y)
    {
        if (UiPositionX == null || UiPositionY == null) return;
        UiPositionX.Value = x; UiPositionY.Value = y; Config.Save();
    }

    internal static void DestroyUi()
    {
        SafeUi(() =>
        {
            Instance?._ui?.ExitEditMode("Lifecycle", Instance.PersistUiPosition);
            Instance?._ui?.Destroy();
        });
    }

    internal static void ExitUiEditMode(string reason) => SafeUi(() =>
        Instance?._ui?.ExitEditMode(reason, Instance.PersistUiPosition));

    internal static void MarkUiEscapeConsumed() => _uiEscapeConsumedFrame = Time.frameCount;
    internal static bool ShouldConsumeUiEscape() => _uiEscapeConsumedFrame == Time.frameCount;

    internal static bool AcquireUiCursor()
    {
        try
        {
            if (!GameCamera.instance) return false;
            GameCamera.instance.UpdateMouseCapture();
            return ZCursor.LockState == CursorLockMode.None;
        }
        catch { return false; }
    }

    internal static void RestoreVanillaCursor()
    {
        try { if (GameCamera.instance) GameCamera.instance.UpdateMouseCapture(); }
        catch { }
    }

    private static bool IsVanillaModalVisible()
    {
        try
        {
            return InventoryGui.IsVisible() || Menu.IsVisible() || Minimap.IsOpen() || Console.IsVisible() ||
                TextInput.IsVisible() || StoreGui.IsVisible() || UnifiedPopup.IsVisible() || Hud.IsPieceSelectionVisible();
        }
        catch { return false; }
    }

    private static void LogMagicHook(string name, MethodBase method)
    {
        bool installed = false;
        Patches patches = method == null ? null : Harmony.GetPatchInfo(method);
        if (patches != null)
        {
            foreach (Patch patch in patches.Prefixes) if (patch.owner == PluginId) installed = true;
            foreach (Patch patch in patches.Postfixes) if (patch.owner == PluginId) installed = true;
            foreach (Patch patch in patches.Transpilers) if (patch.owner == PluginId) installed = true;
        }
        MagicLog((installed ? "MagicSummonHookInstalled" : "MagicSummonHookInstallFailed") + " hook=" + name +
            (method == null ? " reason=MethodNotFound" : ""));
    }

    internal static void Publish(long peer, DamageFacts facts, float loss) =>
        SafeTransport(() => Transport?.Observe(peer, facts, loss));

    internal static bool PublishSummonProvenance(ZDOID summon, long playerId)
    {
        try
        {
            if (Transport == null) return false;
            return Transport.ObserveSummonProvenance(ZDOMan.GetSessionID(), summon.UserID, summon.ID, playerId);
        }
        catch (Exception ex) { Warn($"DamageCommitTransportFailure exception={ex.GetType().Name}"); return false; }
    }

    internal static void PublishDotPool(ZDOID victim, DotKind kind, float before, float after,
        AttackerClass sourceClass, long? sourcePlayerId, ZDOID source, bool updateAccepted) => SafeTransport(() =>
        Transport?.ObserveDotPool(ZDOMan.GetSessionID(), victim.UserID, victim.ID, kind, before, after,
            sourceClass, sourcePlayerId, source.IsNone() ? 0 : source.UserID, source.IsNone() ? 0u : source.ID,
            updateAccepted));

    internal static void SafeTransport(Action action)
    {
        try { action(); }
        catch (Exception ex) { Warn($"DamageCommitTransportFailure exception={ex.GetType().Name}"); }
    }

    private static void OnPerformanceLoggingChanged(object sender, EventArgs args) =>
        Transport?.RefreshPerformanceSettings();

    internal static bool PerformanceLoggingEnabled => EnablePerformanceDiagnosticLogging?.Value == true;
    internal static void PerformanceLog(string message)
    {
        if (!PerformanceLoggingEnabled) return;
        try { ProbeLog?.LogInfo(message); }
        catch { /* Diagnostics cannot change gameplay/delivery. */ }
    }
    internal static void TransportLog(string message)
    {
        if (EnableTransportDiagnosticLogging?.Value != true) return;
        try { ProbeLog?.LogInfo(message); }
        catch { /* Logging cannot change delivery/acceptance state. */ }
    }

    internal static void MagicLog(string message)
    {
        if (!MagicLoggingEnabled) return;
        try { ProbeLog?.LogInfo(message); }
        catch { }
    }

    internal static void EncounterLog(string message)
    {
        bool always = message.StartsWith("CombatEncounterSettings", StringComparison.Ordinal) ||
            message.StartsWith("CombatSettingsAuthority", StringComparison.Ordinal) ||
            message.StartsWith("EncounterStateChanged", StringComparison.Ordinal);
        if (!always && !LifecycleLoggingEnabled) return;
        try { ProbeLog?.LogInfo(message); }
        catch { }
    }

    internal static void SnapshotLog(string message)
    {
        if (EnableTransportDiagnosticLogging?.Value != true) return;
        try { ProbeLog?.LogInfo(message); }
        catch { }
    }

    internal static void UiLog(string message)
    {
        if (EnableTransportDiagnosticLogging?.Value != true) return;
        try { ProbeLog?.LogInfo(message); }
        catch { }
    }

    private static void SafeUi(Action action)
    {
        try { action(); _uiFailureLogged = false; }
        catch (Exception ex)
        {
            if (_uiFailureLogged) return;
            _uiFailureLogged = true;
            Warn("CombatMeterUiFailure exception=" + ex.GetType().Name);
        }
    }

    // Diagnostics must not turn an observation/formatting error into a gameplay exception.
    internal static void Warn(string message)
    {
        try { ProbeLog?.LogWarning(message); }
        catch { /* No diagnostic state needs cleanup. */ }
    }
}
