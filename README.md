# Twin Cells

<p align="center"><img src="media/twin-cells-cover.jpg" alt="Twin Cells: Dead Cells 2 player co-op mod" width="520"></p>

**A two-player co-op overhaul for Dead Cells.** [Русская версия](README_ru.md)

Current release: **Alpha2 · 0.2.0-alpha · 13 November 2026**, Windows x64. Build `3D0A5880`.

Twin Cells adds fixes and features to **DeadCellsMultiplayerMod 0.5.1**, using **Dead Cells Core Modding 35.13.5**. Its 30 parts are compiled into one `CoopPack.dll`; the in-game module still uses the technical name **CoopPack**.

## Changes since Alpha1

See the [Alpha2 changelog](docs/Alpha2-changelog-en.md) for the fixes to the Concierge fight, Combo, revival costs, death sounds, room visibility, cinematics, elevators, transition doors, gamepad controls, player labels, partner HUD and version notifications, plus synchronization and allocation optimizations.

## Install or update

Download **`TwinCells-Alpha2-Update-2026-11-13.zip`** from the [Alpha2 release](https://github.com/Avenged-7wrx/TwinCells/releases/tag/v0.2.0-alpha). Installation instructions in Russian and a SHA-256 checksum are attached to the release.

For an existing Alpha1 co-op copy:

1. Close Dead Cells Coop.
2. Extract the entire ZIP into a separate folder outside the game.
3. Drag the existing `DeadCells-Coop` folder onto `Update-Coop-Alpha2.cmd`.
4. Wait for `UpdateCompleted`. `AlreadyUpdated` means this build is already installed.
5. Launch the game using the usual co-op shortcut. **Both players need Alpha2 build `3D0A5880`.**

The delta updates only `CoopPack.dll`, `modinfo.json` and the installation record. Saves and settings are preserved, and the previous files are kept under `coop-fix-updates` for rollback. It creates no additional game copy. If you already installed the Alpha2 tester update with build `3D0A5880`, no reinstall is needed.

To check without changes, drag the same game folder onto `Check-Coop-Alpha2.cmd`. To roll back, close the game and use `Undo-Coop-Alpha2.cmd`; keep the backup folder. Both players must use the same version after rollback.

For a fresh installation, first use the [Alpha1 full installer](https://github.com/Avenged-7wrx/TwinCells/releases/tag/v0.1.0-alpha), then apply this Alpha2 delta. The full installer uses your own Steam game to create the initial co-op copy. Requirements: your own Steam copy of Dead Cells v35, Windows 10/11 x64 and Microsoft .NET Runtime 10 x64. The installer checks supported file versions. Turn off `DebugPlayerImmortal` in Multiplayer settings → Debug on both PCs.

## Credits and licence

- [DeadCellsMultiplayerMod](https://github.com/vaiserYT/DeadCellsMultiplayerMod) by vaiserYT (Alexey), MIT: the underlying co-op implementation. Its 0.5.1 DLL is used unchanged; Twin Cells applies runtime add-on patches.
- [Dead Cells Core Modding](https://github.com/dead-cells-core-modding/core), MIT: the mod loader.
- Dead Cells belongs to Motion Twin / Evil Empire and its rights holders. Twin Cells is an unofficial fan project, not endorsed by the game developers or the upstream mod projects.
- Twin Cells' own code is [MIT-licensed](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) and [licence texts](licenses/third-party/README.md).

The repository and Alpha2 delta contain no game assets. The base mod, DCCM and the unchanged Steam worker remain in the existing installation.

## Sources and building

`src/` is the exact source snapshot for the released Alpha2 DLL. [`src/build-manifest.json`](src/build-manifest.json) records 30 parts, the 183-entry compile order, reference hashes and compiler settings. The complete DLL SHA-256 is `3D0A58807946E01A8E68683905EF96A7757185470476AFA5550FB09F6F58C2D1`.

The snapshot includes the part sources, applied patch lists, pack runtime and frozen inputs. The build and native test tooling still lives outside this repository. Development-relative paths and references in the snapshot are retained because its files are hash-pinned. The release was compiled with PowerShell 7.6.9 `Add-Type`, DCCM 35.13.5 references, a locally generated GameProxy, base mod 0.5.1 and the unchanged Steam invite worker. A separately rebuilt DLL is not automatically interchangeable with the release build.

[`installer/alpha2/`](installer/alpha2/) holds the updater source and release metadata. Ready-to-install binaries are release assets, not Git files. Alpha1 sources remain available at tag [`v0.1.0-alpha`](https://github.com/Avenged-7wrx/TwinCells/tree/v0.1.0-alpha).

## Reporting problems

Open a [Twin Cells issue](https://github.com/Avenged-7wrx/TwinCells/issues). Include the build and reproduction steps. Remove personal paths, player names, Steam IDs and IP addresses from logs or screenshots before posting them publicly.
