using System;
using System.Collections.Generic;
using System.IO;

namespace DiagnosticDamageProbe.Configuration;

internal static class LegacyConfigMigration
{
    internal const string LegacyFileName = "local.valheim.diagnosticdamageprobe.cfg";

    internal static bool ShouldMigrate(string oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(oldPath) || !File.Exists(oldPath)) return false;
        if (string.IsNullOrEmpty(newPath) || !File.Exists(newPath)) return true;
        return !ContainsKnownSetting(File.ReadAllText(newPath));
    }

    internal static Dictionary<string, string> ParseKnown(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return values;
        string section = "";
        using var reader = new StringReader(text);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';') continue;
            if (trimmed[0] == '[' && trimmed.EndsWith("]", StringComparison.Ordinal))
            { section = trimmed.Substring(1, trimmed.Length - 2).Trim(); continue; }
            int equals = trimmed.IndexOf('=');
            if (equals <= 0) continue;
            string key = trimmed.Substring(0, equals).Trim();
            string value = trimmed.Substring(equals + 1).Trim();
            if (IsKnown(section, key)) values[section + "\n" + key] = value;
        }
        return values;
    }

    internal static bool TryGet(Dictionary<string, string> values, string section, string key, out string value) =>
        values.TryGetValue(section + "\n" + key, out value);

    private static bool ContainsKnownSetting(string text) => ParseKnown(text).Count != 0;

    private static bool IsKnown(string section, string key)
    {
        foreach (ConfigDescriptor entry in ConfigCatalog.Entries)
            if (entry.Section == section && entry.Key == key) return true;
        return false;
    }
}
