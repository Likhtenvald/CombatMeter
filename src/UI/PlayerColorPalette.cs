namespace DiagnosticDamageProbe.UI;

// Pure presentation data: no Unity, snapshot, peer identity or mutable color state.
internal static class PlayerColorPalette
{
    private static readonly uint[] Colors =
    {
        0x58A6FF, 0xFFAD5C, 0x69D29C, 0xD58AE6,
        0xE6CF62, 0xF07888, 0x5CD5D5, 0xA897FF,
        0xB4D66B, 0xED91BC, 0x78BDD1, 0xD6A678
    };

    internal static int Count => Colors.Length;

    internal static int ResolveIndex(long playerId)
    {
        // SplitMix64: reinterpret signed bits and wrap every operation modulo 2^64.
        // Explicit unchecked also supports long.MinValue in checked builds.
        unchecked
        {
            ulong value = (ulong)playerId + 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (int)(value % (ulong)Colors.Length);
        }
    }

    // Packed RGB deliberately excludes alpha; Damage Bar Opacity belongs to the view.
    internal static uint Resolve(long playerId) => Colors[ResolveIndex(playerId)];
}
