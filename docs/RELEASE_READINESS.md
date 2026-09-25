# Pre-release packaging audit

CombatMeter `0.11.2` now uses public plugin name `CombatMeter`, GUID `Likhtenvald.CombatMeter`, assembly/DLL `CombatMeter.dll`, and Thunderstore package name `CombatMeter`. Internal `DiagnosticDamageProbe` namespaces and the project filename remain unchanged to minimize implementation risk.

The project `<Version>` is canonical. MSBuild generates `BuildInfo.Version`, which supplies the BepInEx attribute/runtime version. The packaging script reads the same project value and rejects a mismatched manifest or filename.

Runtime package dependency: `denikson-BepInExPack_Valheim-5.4.2350`. Valheim and Unity assemblies are game-provided; Harmony is supplied by BepInEx. Test doubles and decompiler dependencies are development-only. No Jotunn, ConfigSync, or other runtime library is used.

`manifest.json`, player README, changelog, release notes, Release DLL, and allowlist packaging script are present. No repository URL was discoverable, so `website_url` is empty. No author-selected license was found, so no LICENSE was invented.

A Thunderstore-required `icon.png` is absent. `scripts/package.ps1` creates an explicitly incomplete staging directory and then fails before producing a ZIP. Once an approved icon is added, the script will create `outputs/package/CombatMeter-0.11.2.zip`, reopen it, and compare its actual entries to the allowlist.

Expected archive:

```text
CHANGELOG.md
README.md
icon.png
manifest.json
plugins/CombatMeter.dll
```

LICENSE is included automatically if the author later adds one.
