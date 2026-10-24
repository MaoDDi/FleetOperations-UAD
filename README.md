# Fleet Operations for Ultimate Admiral: Dreadnoughts

[简体中文](README.zh-CN.md) · [Downloads](https://github.com/wangchuanyu666/FleetOperations-UAD/releases)

An experimental Windows x64 manual-aiming mod for **Ultimate Admiral: Dreadnoughts**. Control the selected fleet's guns and torpedoes separately, inspect live dispersion and fired-salvo markers, and display advisory lead points. The settings UI supports Chinese/English and configurable shortcuts.

Current version: **0.0.8.1-export-r1**. This export changes directory/save isolation and version identification; gameplay matches the 0.0.8.1 candidate. This project is separate from the Modern-P0 ship mod and the UAD Commander bridge.

## Features

- Fixed-point and mouse-follow gun aiming, with one requested volley per native turret.
- Independent torpedo aiming and launch commands.
- Live gun-dispersion outlines; orange launch-time salvo areas and endpoint markers removed when their tracked shells end.
- Current torpedo aim lines and initial heading-spread boundaries, without historical launch fans.
- Gun and torpedo lead indicators against an explicitly selected, visible enemy.
- Rebindable keys with Ctrl/Alt/Shift, conflict detection, and Chinese/English settings.

## Install

1. Download the **Windows** ZIP from [Releases](https://github.com/wangchuanyu666/FleetOperations-UAD/releases). GitHub's source archive and a repository checkout do not contain the compiled payload.
2. Extract into an ordinary ASCII path, e.g. `C:\UAD-FleetAim-0081`, outside the Steam game directory. Do not use directory links.
3. Close the game normally. Run `Install.cmd` and provide your own installed game directory. Keep Steam signed in.
4. The installer validates exact game hashes, makes an independent copy (about 4.19 GB), and downloads pinned official MelonLoader 0.6.6 and .NET Core 6.0.36 archives. Allow at least 6 GB free and an Internet connection.
5. After `INSTALLED`, use `Start.cmd`. The first loader launch needs Internet access to generate interop assemblies. Start a **new custom battle**; loading a saved battle disables these experimental commands.

The package includes neither game files nor third-party runtime binaries. Exact game fingerprints and dependency URLs/checksums are in `fleetops-package.json`; a matching marketing version alone does not establish compatibility. Do not bypass hash checks or copy these DLLs directly into Steam's original game.

## Default controls

| Shortcut | Action |
| --- | --- |
| Ctrl+K | Set fixed gun aim at the cursor |
| Ctrl+O | Toggle gun mouse-follow |
| Ctrl+I | Request a gun volley at the current aim point |
| Ctrl+Y | Toggle independent torpedo mouse-follow |
| Ctrl+U | Request a torpedo launch |
| Ctrl+L | Restore native orders for selected ships |
| Ctrl+B | Toggle lead indicators |
| Ctrl+N | Open/close settings |

Select a visible enemy explicitly, then reselect your own firing ships to use lead indicators. They are advisory: they do not set orders or fire. Without an existing gun point, Ctrl+I uses a visible native target or a point 3000 m ahead; without a torpedo point, Ctrl+U uses the cursor's sea point or a 500 m forward fallback.

Ammo, reload, weapon traverse, arcs and range remain native. A request does not guarantee simultaneous fire. F1–F10 and J are reserved. Settings changes save immediately, with backups of old settings.

## Precision and limitations

The free-point spread adapts native **static ship/turret design accuracy** to an explicit **20 m diameter reference circle**, using the native three-disk random kernel. An outline is an outer support boundary, not a confidence interval or the probability of hitting a real ship. If native accuracy is unavailable, the UI explicitly labels native distance-spread fallback.

This does not reproduce the complete native target-conditioned hit/miss distribution, rangefinding warm-up, or all instantaneous speed/turn/recoil/flooding accuracy modifiers. Lead calculations assume an immediate launch and constant target motion, using the first main gun/torpedo mount as references. The torpedo preview is an initial heading bound, not the complete future path.

**Validation status:** builds and static interop/argument checks completed; export installation, portability, visual effects and post-fix combat behavior have not been accepted in a runtime playtest. This is a prerelease, not a claim that all gameplay is verified. Tests are included for maintainers but were not run for this export.

## Build from source

Requirements: Windows PowerShell 5.1 or PowerShell 7, a .NET 6 SDK, and your own compatible independent game copy with MelonLoader and generated `Il2CppAssemblies`.

```powershell
.\Build-Source.ps1 -GameDirectory 'C:\UAD-FleetAim-0081\lab\fleet-operations\game'
# If the SDK is not on PATH, add -DotnetPath 'C:\SDK\dotnet.exe'
```

Outputs: `out/bin/FleetOperations/FleetOperations.dll` and `out/bin/FleetOpsLabIsolation/FleetOpsLabIsolation.dll`. Build scripts generate projects and do not launch/deploy the game. `Test-Source.ps1` is provided for explicit maintainer execution. See [build details](BUILD_README.zh-CN.txt).

After rebuilding, update the runtime package's payload hashes and install into a fresh directory; the launcher rejects mismatched binaries. There is no game assembly redistribution or game-dependent CI pipeline in this repository.

## Data and removal

The release uses `lab/fleet-operations/userdata` for isolated saves/settings and `lab/fleet-operations/logs` for launch logs. Back up `userdata` before updates. Extract updates into a fresh directory instead of overwriting an old installation. Close the game before removing the extracted package; the original remains launchable through Steam.

## Contributing and license

Bug reports should include the mod version, game hash mismatch details if relevant, battle mode, selected ships/weapons and exact steps. Review logs before posting and remove private information. Do not commit game files, generated game assemblies or personal saves.

Project source is licensed under [MIT](LICENSE). Third-party dependencies and the game retain their own licenses; see [third-party notices](THIRD_PARTY_NOTICES.md).
