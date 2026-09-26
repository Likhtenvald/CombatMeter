using System.Collections.Generic;

namespace DiagnosticDamageProbe.Configuration;

internal enum ConfigAuthority
{
    Local,
    Host
}

internal readonly struct ConfigDescriptor
{
    internal readonly string Section, Key, DefaultValue, Bounds, Purpose, RuntimeConsumer;
    internal readonly ConfigAuthority Authority;
    internal readonly bool LiveUpdate;

    internal ConfigDescriptor(string section, string key, string defaultValue, string bounds,
        ConfigAuthority authority, string purpose, string runtimeConsumer, bool liveUpdate)
    {
        Section = section; Key = key; DefaultValue = defaultValue; Bounds = bounds;
        Authority = authority; Purpose = purpose; RuntimeConsumer = runtimeConsumer; LiveUpdate = liveUpdate;
    }
}

// Explicit audit inventory. BepInEx binding remains in Plugin; this pure catalog documents authority and is testable.
internal static class ConfigCatalog
{
    internal static readonly IReadOnlyList<ConfigDescriptor> Entries = new[]
    {
        Local("Diagnostics", "EnableDiagnosticLogging", "false", "bool", "Owner-side damage observations", "ApplyDamagePatch", true),
        Local("Diagnostics", "EnableTransportDiagnosticLogging", "false", "bool", "Damage transport diagnostics", "Plugin transport logger", true),
        Local("Diagnostics", "EnableLifecycleDiagnosticLogging", "false", "bool", "Lifecycle diagnostics", "PlayerLifecycleProbe", true),
        Local("Diagnostics", "EnableMagicAttributionDiagnosticLogging", "false", "bool", "Magic attribution diagnostics", "MagicAttributionProbe", true),
        Local("Diagnostics", "EnablePerformanceDiagnosticLogging", "false", "bool", "Opt-in host performance metrics", "DamageCommitTransport", true),
        Local("UI", "UI Enabled", "true", "bool", "Shows the combat meter", "CombatMeterUiController", true),
        Local("UI", "Toggle Key", "F8", "keyboard shortcut", "Shows or hides the local HUD", "Plugin.Update", true),
        Local("UI", "Edit Mode Key", "LeftControl+F8", "keyboard shortcut", "Enters local HUD edit mode", "Plugin.Update", true),
        Local("UI", "UI Scale", "1", "0.5..2", "Changes local HUD size", "CombatMeterUiController", true),
        Local("UI", "Window Width", "480", "350..800", "Changes local HUD width", "CombatMeterUiController", true),
        Local("UI", "Background Opacity", "0.65", "0..1", "Changes local panel opacity", "CombatMeterUiController", true),
        Local("UI", "Show Damage Bars", "true", "bool", "Shows local contribution bars", "CombatMeterUiController", true),
        Local("UI", "Show Damage Percent", "true", "bool", "Shows local contribution percentages", "CombatMeterUiController", true),
        Local("UI", "Damage Bar Opacity", "0.25", "0..1", "Changes local bar opacity", "CombatMeterUiController", true),
        Local("UI Position", "X", "24", "finite; clamped to canvas", "Persists local HUD position", "CombatMeterUiController", true),
        Local("UI Position", "Y", "-150", "finite; clamped to canvas", "Persists local HUD position", "CombatMeterUiController", true),
        Host("Combat", "Combat Timeout", "20", "5..60", "Ends or recovers an inactive encounter", "EncounterManager", true),
        Host("Combat", "Recovery Timeout", "180", "30..600", "Expires player recovery periods", "EncounterManager", true),
        Host("Combat", "DPS Idle Timeout", "6", "1..20", "Controls personal active DPS time", "PlayerDpsActivityTracker", true)
    };

    internal static ConfigDescriptor Find(string section, string key)
    {
        foreach (ConfigDescriptor entry in Entries)
            if (entry.Section == section && entry.Key == key) return entry;
        throw new KeyNotFoundException(section + "/" + key);
    }

    private static ConfigDescriptor Local(string section, string key, string value, string bounds,
        string purpose, string consumer, bool live) =>
        new ConfigDescriptor(section, key, value, bounds, ConfigAuthority.Local, purpose, consumer, live);
    private static ConfigDescriptor Host(string section, string key, string value, string bounds,
        string purpose, string consumer, bool live) =>
        new ConfigDescriptor(section, key, value, bounds, ConfigAuthority.Host, purpose, consumer, live);
}
