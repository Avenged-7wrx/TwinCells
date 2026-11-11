# Third-party notices

Twin Cells' own code is released under the MIT licence in [LICENSE](LICENSE). This repository contains source code only. It ships no binaries, no third-party files and no game files. Twin Cells depends on the projects below, and each one keeps its own licence.

## 1. DeadCellsMultiplayerMod

- Project: <https://github.com/vaiserYT/DeadCellsMultiplayerMod>
- Author: vaiserYT (Alexey)
- Version used: 0.5.1, the official release <https://github.com/vaiserYT/DeadCellsMultiplayerMod/releases/tag/0.5.1>
- Licence: MIT

DeadCellsMultiplayerMod is the community co-op mod that makes co-op in Dead Cells possible: connecting, lobby sessions and synchronising players and enemies. Twin Cells is an add-on built on top of it.

How Twin Cells uses it:
- The Twin Cells module declares DeadCellsMultiplayerMod as a dependency, and its sources are compiled against `DeadCellsMultiplayerMod.dll` 0.5.1 as a reference assembly. That DLL is not part of CoopPack.dll and is not in this repository.
- **The base mod's files and source code are not modified.** At run time, some Twin Cells parts patch specific methods of the base mod with Harmony.
- CoopPerfFix applies at run time, to the released 0.5.1 DLL, a per-frame saving that the base mod's author made after 0.5.1 (upstream commit `e626986`).

```
MIT License

Copyright (c) 2025 Alexey

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 2. Dead Cells Core Modding (DCCM)

- Project: <https://github.com/dead-cells-core-modding/core>
- Authors: Dead Cells Core Modding contributors
- Version used: 35.13.5
- Licence: MIT

How Twin Cells uses it:
- DCCM is the mod loader Twin Cells runs on.
- The sources are compiled against DCCM's assemblies, and against the `GameProxy.dll` that DCCM generates from the player's own game, as reference assemblies only. None of them is part of CoopPack.dll or in this repository.

```
MIT License

Copyright (c) 2025-2026 Dead Cells Core Modding

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 3. Libraries the sources compile against

The sources in `src/` use the libraries below through the reference set of a DCCM 35.13.5 installation (see `src/build-manifest.json`). They come with DCCM. None of them is included in this repository.

| Library | Used for | Version in DCCM 35.13.5 | Licence | Project |
|---|---|---|---|---|
| HarmonyX (`0Harmony.dll`) | Patching game and base-mod methods at run time | 2.16.1 | MIT | <https://github.com/BepInEx/HarmonyX> |
| Mono.Cecil | Reading `GameProxy.dll` for the fingerprint checks | 0.11.6 | MIT | <https://github.com/jbevain/cecil> |
| Json.NET (`Newtonsoft.Json.dll`) | JSON data in CoopSyncFixes | 13.0.4 | MIT | <https://github.com/JamesNK/Newtonsoft.Json> |
| Serilog | Logging | 4.4.0 | Apache-2.0 | <https://github.com/serilog/serilog> |
| Steamworks.NET | Steam lobby calls in CoopLobby | 2025.164.1 | MIT | <https://github.com/rlabrecque/Steamworks.NET> |

Steamworks.NET wraps Valve's Steamworks API, which is covered by Valve's Steamworks SDK terms.

## 4. This project's earlier releases

Before the pack, most of its parts were released by this project as separate DCCM mods. They were released under the MIT licence, with this copyright line:

```
Copyright (c) 2026 Local Dead Cells Sync Fixes contributors
```

The permission notice and disclaimer are the same MIT text as in [LICENSE](LICENSE).

## 5. Dead Cells

Dead Cells and its assets belong to their rights holders (Motion Twin / Evil Empire). Twin Cells is not affiliated with or endorsed by them.

This repository contains no game files, game executables, extracted game bytecode or saves. Each player needs their own copy of Dead Cells on Steam. The sources refer to some of the game's types and methods by name, for example in the GameProxy fingerprint lists in `src/inputs/roots/`, to check that they run against the expected game version.
