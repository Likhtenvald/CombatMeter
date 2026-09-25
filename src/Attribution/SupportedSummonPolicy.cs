using System;

namespace DiagnosticDamageProbe.Attribution;

internal static class SupportedSummonPolicy
{
    internal static bool IsSupportedPrefab(string objectName)
    {
        string name = (objectName ?? string.Empty).Trim();
        const string clone = "(Clone)";
        if (name.EndsWith(clone, StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - clone.Length).TrimEnd();
        return name.Equals("staff_greenroots_tentaroot", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Skeleton_Friendly", StringComparison.OrdinalIgnoreCase);
    }
}
