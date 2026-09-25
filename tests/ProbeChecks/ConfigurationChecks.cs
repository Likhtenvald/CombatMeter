using System;
using System.Collections.Generic;
using DiagnosticDamageProbe.Configuration;
using System.IO;

internal static class ConfigurationChecks
{
    private static int _passed;

    internal static int Run()
    {
        Test("config 01: catalog contains every bound entry exactly once", () =>
        {
            Eq(18, ConfigCatalog.Entries.Count); var keys = new HashSet<string>();
            foreach (ConfigDescriptor e in ConfigCatalog.Entries) True(keys.Add(e.Section + "/" + e.Key));
        });
        Test("config 02: combat settings are host authoritative", () =>
        {
            Host("Combat Timeout", "20", "5..60"); Host("Recovery Timeout", "180", "30..600");
            Host("DPS Idle Timeout", "6", "1..20");
        });
        Test("config 03: UI settings are local", () =>
        {
            string[] keys = { "UI Enabled", "Toggle Key", "Edit Mode Key", "UI Scale", "Window Width",
                "Background Opacity", "Show Damage Bars", "Show Damage Percent", "Damage Bar Opacity" };
            foreach (string key in keys) Eq(ConfigAuthority.Local, ConfigCatalog.Find("UI", key).Authority);
        });
        Test("config 04: persisted HUD position is local", () =>
        { Eq(ConfigAuthority.Local, ConfigCatalog.Find("UI Position", "X").Authority); Eq(ConfigAuthority.Local, ConfigCatalog.Find("UI Position", "Y").Authority); });
        Test("config 05: diagnostic switches are local and cannot define combat semantics", () =>
        { foreach (ConfigDescriptor e in ConfigCatalog.Entries) if (e.Section == "Diagnostics") Eq(ConfigAuthority.Local, e.Authority); });
        Test("config 06: every descriptor documents purpose consumer validation and live behavior", () =>
        { foreach (ConfigDescriptor e in ConfigCatalog.Entries) { True(e.Purpose.Length > 0); True(e.RuntimeConsumer.Length > 0); True(e.Bounds.Length > 0); True(e.LiveUpdate); } });
        Test("config migration 07: no old config means no migration", () =>
        { string root = Temp(); try { True(!LegacyConfigMigration.ShouldMigrate(Path.Combine(root, "missing.cfg"), Path.Combine(root, "new.cfg"))); } finally { Directory.Delete(root, true); } });
        Test("config migration 08: old values are parsed and preserved", () =>
        { var v = LegacyConfigMigration.ParseKnown("[Combat]\nCombat Timeout = 37\n[UI]\nUI Scale = 1.2\n[UI Position]\nX = 44\n"); True(LegacyConfigMigration.TryGet(v, "Combat", "Combat Timeout", out string timeout)); Eq("37", timeout); Eq("1.2", v["UI\nUI Scale"]); Eq("44", v["UI Position\nX"]); });
        Test("config migration 09: customized new config wins", () =>
        { string root = Temp(); try { string old = Path.Combine(root, "old.cfg"), current = Path.Combine(root, "new.cfg"); File.WriteAllText(old, "[Combat]\nCombat Timeout=37"); File.WriteAllText(current, "[UI]\nUI Scale=1.4"); True(!LegacyConfigMigration.ShouldMigrate(old, current)); } finally { Directory.Delete(root, true); } });
        Test("config migration 10: malformed and unknown lines are ignored safely", () =>
        { var v = LegacyConfigMigration.ParseKnown("broken\n[Unknown]\nX=1\n[Combat]\nCombat Timeout\nDPS Idle Timeout=oops"); Eq(1, v.Count); Eq("oops", v["Combat\nDPS Idle Timeout"]); });
        Test("config migration 11: migration becomes idempotent after new config exists", () =>
        { string root = Temp(); try { string old = Path.Combine(root, "old.cfg"), current = Path.Combine(root, "new.cfg"); File.WriteAllText(old, "[Combat]\nCombat Timeout=37"); True(LegacyConfigMigration.ShouldMigrate(old, current)); File.WriteAllText(current, "[Combat]\nCombat Timeout=37"); True(!LegacyConfigMigration.ShouldMigrate(old, current)); } finally { Directory.Delete(root, true); } });
        Test("public metadata 12: finalized identity is consistent", () =>
        { Eq("CombatMeter", PublicIdentity.PluginName); Eq("CombatMeter", PublicIdentity.AssemblyName); Eq("Likhtenvald.CombatMeter", PublicIdentity.PluginGuid); Eq(LegacyConfigMigration.LegacyFileName, PublicIdentity.LegacyPluginGuid + ".cfg"); });
        return _passed;
    }

    private static void Host(string key, string value, string bounds)
    { ConfigDescriptor e = ConfigCatalog.Find("Combat", key); Eq(ConfigAuthority.Host, e.Authority); Eq(value, e.DefaultValue); Eq(bounds, e.Bounds); }
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static string Temp() { string path = Path.Combine(Path.GetTempPath(), "CombatMeterChecks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
