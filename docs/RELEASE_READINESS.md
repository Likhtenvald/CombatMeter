# Stable release packaging — CombatMeter 1.0.1

The canonical project Version generates BuildInfo.Version for the BepInEx plugin
attribute. Plugin and manifest version are 1.0.1; assembly version is 1.0.1.0.
Plugin GUID Likhtenvald.CombatMeter, CombatMeter package/DLL name, and internal
DiagnosticDamageProbe namespaces and project filename remain unchanged.

The runtime-tested feature commit 60efaa6e9d57ec60abc59529df61cc3914692dcb is frozen.
Release preparation changes only version metadata and documentation. The baseline
contains 553 managed checks; final main must pass these and a clean Release build
with zero warnings/errors before packaging.

Runtime dependency remains denikson-BepInExPack_Valheim-5.4.2350. Valheim and Unity
assemblies are game-provided; Harmony is supplied by BepInEx. No test doubles,
decompiler dependencies, or other runtime libraries belong in the package.

The canonical scripts/package.ps1 produces outputs/package/CombatMeter-1.0.1.zip
with exactly these entries:

    CHANGELOG.md
    LICENSE
    README.md
    icon.png
    manifest.json
    plugins/CombatMeter.dll

The repository URL is https://github.com/Likhtenvald/CombatMeter; the license is MIT.
Independently reopen the ZIP, compare every entry to its source, and require the
DLL to match the final clean main Release output. Preserve all previous ZIPs.
Upload to Thunderstore is manual.

All multiplayer participants must use the same current version. Snapshot RPC is
CombatMeter.CombatSnapshot.v2, protocol 2; no explicit version negotiation or
mixed-version compatibility is provided. Dedicated servers remain unsupported.
