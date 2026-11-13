# Third-party notices

The Alpha2 delta (`TwinCells-Alpha2-Update-2026-11-13.zip`) distributes Twin Cells' own `CoopPack.dll`, module metadata, source snapshot, updater and licence texts. It reuses the existing Alpha1 installation of DCCM, DeadCellsMultiplayerMod and the Steam invite worker. The third-party binary inventory below describes the separate Alpha1 full installer.

Twin Cells' own code is released under the MIT licence in [LICENSE](LICENSE). The git tree contains source code only: no binaries, no third-party files other than the licence texts in [`licenses/third-party`](licenses/third-party/), and no game files. The installer attached to the GitHub release redistributes the official release archives of DCCM 35.13.5 and DeadCellsMultiplayerMod 0.5.1 unchanged. Both archives include Valve's `steam_api64.dll` (see [6.4](#64-microsoft-and-valve-redistributables)). DCCM's release archive also contains third-party components under their own licences, including LGPL components. Section 6, [Components redistributed by the release installer](#6-components-redistributed-by-the-release-installer), lists them with their licences and sources, and their licence texts are in [`licenses/third-party`](licenses/third-party/). The Alpha1 installer predates section 6: its `base/licenses` folder holds only the licence texts of DCCM, DeadCellsMultiplayerMod and this project's earlier releases, and its `base/THIRD-PARTY-NOTICES.md` lists DCCM simply as MIT (see [6.6](#66-open-points-for-the-release)). Twin Cells' own parts of the installer contain no game files; the installer copies the player's own Steam installation. DCCM's unchanged release archive, however, includes files that are generated from the game or appear to come from it: `GameProxy.dll`, `data.cdb` and `tmxtool.hl` (see [6.1, Other contents](#other-contents)). Twin Cells depends on the projects below, and each one keeps its own licence.

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

## 6. Components redistributed by the release installer

The release installer `DeadCells-Coop-Full-Install-Alpha1-2026-11-11.zip`, attached to the [Alpha1 release](https://github.com/Avenged-7wrx/TwinCells/releases/tag/v0.1.0-alpha), contains two upstream release archives, unchanged:

| Entry in the installer | Upstream release asset | SHA-256 |
|---|---|---|
| `base/upstream/win-x64.zip` | `win-x64.zip` of the [DCCM v35.13.5 release](https://github.com/dead-cells-core-modding/core/releases/tag/v35.13.5) | `58989cacd4ce7580d1001c39194bc631fb68331abd7af2643373d45fd909d08d` |
| `base/upstream/DeadCellsMultiplayerMod_0.5.1.zip` | `DeadCellsMultiplayerMod_0.5.1.zip` of the [DeadCellsMultiplayerMod 0.5.1 release](https://github.com/vaiserYT/DeadCellsMultiplayerMod/releases/tag/0.5.1) | `37cccbcc2504ace5be7251fb4b69fbb5262281a86aba734564f4f65192d93175` |

Each hash matches the SHA-256 digest that GitHub reports for that release asset. The rest of the installer is Twin Cells' own code (see [LICENSE](LICENSE) and section 4).

The tables below list what the two archives contain. Paths are inside the archive. The licence texts are in [`licenses/third-party`](licenses/third-party/), and [its README](licenses/third-party/README.md) gives the exact upstream file each text was fetched from. "Not verified" marks what this audit could not confirm; all of these points are collected in [6.5](#65-not-verified). Open decisions about the published release are in [6.6](#66-open-points-for-the-release). This is a factual inventory, not legal advice.

### 6.1 DCCM 35.13.5 release archive (`base/upstream/win-x64.zip`)

#### DCCM's own code

| Files | Component | Version | Licence | Source at that version | Licence text |
|---|---|---|---|---|---|
| The DCCM project assemblies in `core/host/`: `ModCore`, `ModCore.Common`, `ModCore.Native`, `HashlinkSharp`, `HashlinkNET.Bytecode`, `HashlinkNET.Compiler`, `HaxeProxy`, `HaxeDocs`, `BytecodeMapping`, `NonPublicNativeMembers`, `GameRes.Core` and `DCCMShell`, with their `.pdb`, `.xml` and `.json` files. `DCCMTool.dll` and the same libraries in `core/mdk/tools/`. `core/mdk/MDK.Package.*`, `core/mdk/ModCore.Tasks.*`, `core/mdk/packages/DeadCellsCoreModding.MDK.1.0.1.nupkg`, `core/mdk/build/**`, `core/mdk/install.ps1`, `uninstall.ps1` and `install-linux.sh`. `plugins/ModCore.ModLoader.Default/`. The DCCM code in `core/host/startup/steam/deadcells.exe`, `core/host/startup/gog/deadcells.exe` and `core/host/startup/DeadCellsModding.exe` (+ `.pdb`). `core/native/win-x64/modcorenative.dll` (+ `.pdb`), `host_lib.lib`, `tinycc/core_hlc_inc.h` and `nativemembers.json`. `core/host/res.pak` (DCCM's translation resources: a PAK named `dccm-core` holding `main.en.mo`, `main.en.pot`, `main.zh.mo` and `main.zh.po` for the ModCore sources), `ModCoreVersion.txt` (it contains `35.13.5`) and `core/host/startup/steam_appid.txt` (it contains `588650`, the Steam app ID of Dead Cells) | Dead Cells Core Modding (DCCM) | 35.13.5, commit `d46d0e1` | MIT | [dead-cells-core-modding/core at v35.13.5](https://github.com/dead-cells-core-modding/core/tree/d46d0e158549c3c506d638cea3e3996f8eac7508) | [DCCM.txt](licenses/third-party/DCCM.txt) |

DCCM's assemblies carry the company name "Dead Cells Core Modding" and the informational version `35.13.5+d46d0e158549c3c506d638cea3e3996f8eac7508`, the commit of tag v35.13.5. Some of these files also contain code from other projects, listed in the tables below: RectpackSharp source in `GameRes.Core.dll`, CsWin32-generated interop code, the .NET runtime in the three launchers, the Microsoft C/C++ runtime (6.4) and HashLink code. `modcorenative.dll` includes HashLink's `hlc_main.c` (Haxe Foundation, MIT, [HashLink.txt](licenses/third-party/HashLink.txt)). `tinycc/core_hlc_inc.h` contains declarations that match HashLink's [`src/hl.h`](https://github.com/HaxeFoundation/hashlink/blob/d11bb13fd7140e688162e7dd8693c5b25c011f94/src/hl.h), for example the `hl_type` structures and the `HL_EXC_*` and `HL_TRACK_*` macros (Haxe Foundation, MIT, [HashLink.txt](licenses/third-party/HashLink.txt)).

#### Managed libraries

`core/host/` and `core/mdk/tools/` are written as "both folders" where a file is in both.

| Files | Component | Version | Licence | Source at that version | Licence text |
|---|---|---|---|---|---|
| `core/host/0Harmony.dll`, `.xml` | HarmonyX | 2.16.1 | MIT | [BepInEx/HarmonyX@02a98f5](https://github.com/BepInEx/HarmonyX/tree/02a98f540fa12ac4656f189a3cf67b0c96cc68f7) | [HarmonyX.txt](licenses/third-party/HarmonyX.txt) |
| `K4os.Hash.xxHash.dll`, `.xml` in both folders | K4os.Hash.xxHash | 1.0.8 | MIT | [tag 1.0.8](https://github.com/MiloszKrajewski/K4os.Hash.xxHash/tree/1.0.8) | [K4os.Hash.xxHash.txt](licenses/third-party/K4os.Hash.xxHash.txt) |
| `MemoryPack.Core.dll`, `.xml` in both folders | MemoryPack.Core | 1.21.4 | MIT | [Cysharp/MemoryPack@0f2fe08](https://github.com/Cysharp/MemoryPack/tree/0f2fe0827907940768293b7bb81b9605a7d8cba8) | [MemoryPack.txt](licenses/third-party/MemoryPack.txt) |
| `Mono.Cecil.dll`, `Mono.Cecil.Mdb.dll`, `Mono.Cecil.Pdb.dll`, `Mono.Cecil.Rocks.dll` in both folders | Mono.Cecil | 0.11.6 | MIT | [jbevain/cecil tag 0.11.6](https://github.com/jbevain/cecil/tree/0.11.6) | [Mono.Cecil.txt](licenses/third-party/Mono.Cecil.txt) |
| `MonoMod.Core`, `MonoMod.RuntimeDetour`, `MonoMod.Utils` (`.dll`, `.xml`) in both folders | MonoMod | Core 1.3.6, RuntimeDetour 25.3.6, Utils 25.0.14 | MIT | [MonoMod/MonoMod@a74a1cf](https://github.com/MonoMod/MonoMod/tree/a74a1cf0d15c5e82dfa6ef8bbe7a131b60bed3fd) | [MonoMod.txt](licenses/third-party/MonoMod.txt) |
| `MonoMod.Backports.dll`, `.xml` and `MonoMod.ILHelpers.dll` in both folders | MonoMod | Backports 1.1.2, ILHelpers 1.1.0 | MIT | [MonoMod/MonoMod@a1b8285](https://github.com/MonoMod/MonoMod/tree/a1b82852b2574742776af08818487b90b0bfab93), the commit recorded in Backports' informational version (`1.1.2+a1b82852b`). ILHelpers records only `1.1.0` and no commit, so this commit is inferred for it | [MonoMod.txt](licenses/third-party/MonoMod.txt) |
| `MonoMod.Iced.dll`, `.xml` in both folders (part of the MonoMod.Core package) | iced | 1.21.0 | MIT | [icedland/iced tag v1.21.0](https://github.com/icedland/iced/tree/v1.21.0) | [Iced.txt](licenses/third-party/Iced.txt) |
| `Newtonsoft.Json.dll`, `.xml` in both folders; the copy bundled in both `deadcells.exe`; `Newtonsoft.Json.xml` in `core/mdk/`, `core/host/startup/steam/` and `core/host/startup/gog/` | Json.NET (Newtonsoft.Json) | 13.0.4 | MIT | [JamesNK/Newtonsoft.Json@4e13299](https://github.com/JamesNK/Newtonsoft.Json/tree/4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a) | [Newtonsoft.Json.txt](licenses/third-party/Newtonsoft.Json.txt) |
| `core/mdk/Newtonsoft.Json.dll` (the binary committed in DCCM's `mdk/MDK.Package/`) | Json.NET (Newtonsoft.Json) | 13.0.3 | MIT | [JamesNK/Newtonsoft.Json@0a2e291](https://github.com/JamesNK/Newtonsoft.Json/tree/0a2e291c0d9c0c7675d445703e51750363a549ef) | [Newtonsoft.Json.txt](licenses/third-party/Newtonsoft.Json.txt) |
| `core/host/SDL3-CS.dll`, `.xml` | SDL3-CS (managed SDL3 bindings) | 3.4.14.1 | Zlib | [edwardgushchin/SDL3-CS@b525db5](https://github.com/edwardgushchin/SDL3-CS/tree/b525db5bf89a46c3416efe21b09431c28cf00b8d) | [SDL3-CS.txt](licenses/third-party/SDL3-CS.txt) |
| `Serilog.dll`, `.xml` in both folders | Serilog | 4.4.0 | Apache-2.0 | [serilog/serilog@497f80f](https://github.com/serilog/serilog/tree/497f80fda4f9e8f98b9c13ba34b1f0530f8c4449) | [Serilog-Apache-2.0.txt](licenses/third-party/Serilog-Apache-2.0.txt) |
| The copy bundled in both `deadcells.exe`; `Serilog.xml` in `core/host/startup/steam/` and `gog/` | Serilog | 4.2.0 | Apache-2.0 | [serilog/serilog@c00e17f](https://github.com/serilog/serilog/tree/c00e17fdeb615d36c062569f7342f72c9ece4aa7) | [Serilog-Apache-2.0.txt](licenses/third-party/Serilog-Apache-2.0.txt) |
| `core/host/Serilog.Sinks.Console.dll`, `.xml`; the copy bundled in both `deadcells.exe`; `.xml` in `core/host/startup/steam/` and `gog/` | Serilog.Sinks.Console | 6.1.1 | Apache-2.0 | [serilog/serilog-sinks-console@b3cb1f0](https://github.com/serilog/serilog-sinks-console/tree/b3cb1f042f5636161b831fa8816d70d865a5eec4) | [Serilog.Sinks-Apache-2.0.txt](licenses/third-party/Serilog.Sinks-Apache-2.0.txt) |
| `core/host/Serilog.Sinks.File.dll`, `.xml`; the copy bundled in both `deadcells.exe`; `.xml` in `core/host/startup/steam/` and `gog/` | Serilog.Sinks.File | 7.0.0 | Apache-2.0 | [serilog/serilog-sinks-file@23c732a](https://github.com/serilog/serilog-sinks-file/tree/23c732a8658a0df2a5434fe69b0011800b14f0da) | [Serilog.Sinks-Apache-2.0.txt](licenses/third-party/Serilog.Sinks-Apache-2.0.txt) |
| `core/host/Serilog.Sinks.TextWriter.dll`, `.xml` | Serilog.Sinks.TextWriter | 3.0.0 | Apache-2.0 | [serilog/serilog-sinks-textwriter@266723a](https://github.com/serilog/serilog-sinks-textwriter/tree/266723a89b73f5413aca6980c0fc01af8cd5c6c9) | [Serilog.Sinks-Apache-2.0.txt](licenses/third-party/Serilog.Sinks-Apache-2.0.txt) |
| `core/host/Serilog.Sinks.Trace.dll`, `.xml` | Serilog.Sinks.Trace | 4.0.0 | Apache-2.0 | [serilog/serilog-sinks-trace@d7bcb06](https://github.com/serilog/serilog-sinks-trace/tree/d7bcb061318851f4f2cc1ade9dd2173b2326012a) | [Serilog.Sinks-Apache-2.0.txt](licenses/third-party/Serilog.Sinks-Apache-2.0.txt) |
| `SharpPdb.dll`, `SharpPdb.Windows.dll`, `SharpUtilities.dll` (+ `.xml`) in both folders | SharpPdb, SharpPdb.Windows, SharpUtilities | 1.0.4, 1.0.4, 1.4.6 | MIT | [southpolenator/SharpPdb](https://github.com/southpolenator/SharpPdb), [southpolenator/SharpUtils](https://github.com/southpolenator/SharpUtils). The repositories have no release tags, so the exact commit was not verified | [SharpPdb-SharpUtilities.txt](licenses/third-party/SharpPdb-SharpUtilities.txt) |
| `core/mdk/tools/Spectre.Console.dll`, `Spectre.Console.Ansi.dll` (+ `.xml`) | Spectre.Console, Spectre.Console.Ansi | 0.57.2 | MIT | [spectreconsole/spectre.console@bbbb572](https://github.com/spectreconsole/spectre.console/tree/bbbb5729dde27b58deee44f447a788eea46ee451) | [Spectre.Console.txt](licenses/third-party/Spectre.Console.txt) |
| `core/mdk/tools/Spectre.Console.Cli.dll`, `.xml` and the 10 satellite assemblies `core/mdk/tools/*/Spectre.Console.Cli.resources.dll` | Spectre.Console.Cli | 0.55.0 | MIT | [spectreconsole/spectre.console.cli@6f30fca](https://github.com/spectreconsole/spectre.console.cli/tree/6f30fcabec457a6e5c10870cefb62fe8259ecb1c) | [Spectre.Console.txt](licenses/third-party/Spectre.Console.txt) |
| `StbImageSharp.dll` in both folders | StbImageSharp (C# port of stb_image) | 2.30.16 | MIT OR Unlicense | [StbSharp/StbImageSharp@125af70](https://github.com/StbSharp/StbImageSharp/tree/125af70cb557033f2c46aec8e82eaaf72ac49817) | [stb.txt](licenses/third-party/stb.txt) |
| `StbImageWriteSharp.dll` in both folders | StbImageWriteSharp (C# port of stb_image_write) | 1.16.7 | MIT OR Unlicense (the licence block of `stb_image_write.h`; the port's own README says "Public Domain") | [StbSharp/StbImageWriteSharp tag 1.16.7](https://github.com/StbSharp/StbImageWriteSharp/tree/1.16.7) | [stb.txt](licenses/third-party/stb.txt) |
| `Steamworks.NET.dll` in both folders; the copy bundled in both `deadcells.exe` | Steamworks.NET | 2025.164.1 | MIT | [rlabrecque/Steamworks.NET tag 2025.164.1](https://github.com/rlabrecque/Steamworks.NET/tree/2025.164.1) | [Steamworks.NET.txt](licenses/third-party/Steamworks.NET.txt) |
| `core/host/System.IO.Hashing.dll`, `.xml` | System.IO.Hashing | 10.0.11 | MIT | [dotnet/dotnet@e2f47b0, `src/runtime/src/libraries/System.IO.Hashing`](https://github.com/dotnet/dotnet/tree/e2f47b0110ed922f21a1522da67279133ce28f32/src/runtime/src/libraries/System.IO.Hashing) | [dotnet.txt](licenses/third-party/dotnet.txt), [dotnet-runtime-10.0.12-THIRD-PARTY-NOTICES.txt](licenses/third-party/dotnet-runtime-10.0.12-THIRD-PARTY-NOTICES.txt) |
| Code compiled into `GameRes.Core.dll` in both folders (namespace `GameRes.Core.Utilities.RectpackSharp`) | RectpackSharp (source copied into DCCM) | not identified | MIT | [DCCM `mdk/GameRes.Core/Utilities/RectpackSharp` at v35.13.5](https://github.com/dead-cells-core-modding/core/tree/d46d0e158549c3c506d638cea3e3996f8eac7508/mdk/GameRes.Core/Utilities/RectpackSharp); upstream [ThomasMiz/RectpackSharp](https://github.com/ThomasMiz/RectpackSharp) | [RectpackSharp.txt](licenses/third-party/RectpackSharp.txt) |
| Generated `Windows.Win32.*` code in `DCCMShell.dll`, and in `ModCore.Native.dll` and `NonPublicNativeMembers.dll` in both folders | Microsoft.Windows.CsWin32 (a build-time source generator; no CsWin32 binary is included) | 0.3.321 | MIT | [microsoft/CsWin32@b0f1e79](https://github.com/microsoft/CsWin32/tree/b0f1e799e6aa793eccffafa6ba8164ca45a9266f) | [CsWin32.txt](licenses/third-party/CsWin32.txt) |
| `core/mdk/Microsoft.Build.Framework.xml`, `Microsoft.Build.Utilities.Core.xml`, `Microsoft.NET.StringTools.xml` (XML documentation only) | MSBuild | 18.8.2 | MIT | [dotnet/msbuild@ce25c01](https://github.com/dotnet/msbuild/tree/ce25c01082c9c46cd02ad1ff3ff8f16fe5cc2f44) | [msbuild.txt](licenses/third-party/msbuild.txt) |
| `core/mdk/System.Runtime.CompilerServices.Unsafe.xml` (XML documentation only) | System.Runtime.CompilerServices.Unsafe | 6.1.2 | MIT | [dotnet/maintenance-packages@f62ca00](https://github.com/dotnet/maintenance-packages/tree/f62ca0009b038cab4725a720f386623a969d73ad) | [dotnet.txt](licenses/third-party/dotnet.txt) |
| `core/crashlink/crashlink/**` and `core/crashlink/hlrun/**` (Python sources) | crashlink, DCCM's fork of [N3rdL0rd/crashlink](https://github.com/N3rdL0rd/crashlink) | 0.0.8 | MIT | [dead-cells-core-modding/crashlink@a677481](https://github.com/dead-cells-core-modding/crashlink/tree/a677481919905c81b9cebfb3f0df46e4380af189) | [crashlink.txt](licenses/third-party/crashlink.txt) |

#### .NET runtime

| Files | Component | Version | Licence | Source at that version | Licence text |
|---|---|---|---|---|---|
| `core/host/startup/steam/deadcells.exe`, `core/host/startup/gog/deadcells.exe` | .NET runtime, self-contained in each single-file bundle: the host and 46 trimmed framework assemblies (each bundle has 55 entries: these 46, 7 application assemblies, `deps.json` and `runtimeconfig.json`) | 10.0.12 | MIT | [dotnet/dotnet@95017c7, `src/runtime`](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701/src/runtime) | [dotnet.txt](licenses/third-party/dotnet.txt), [dotnet-runtime-10.0.12-THIRD-PARTY-NOTICES.txt](licenses/third-party/dotnet-runtime-10.0.12-THIRD-PARTY-NOTICES.txt) |
| `core/host/startup/DeadCellsModding.exe` (+ `.pdb`) | .NET NativeAOT runtime, compiled into the executable | 10.0.12 | MIT | same | same |
| `core/mdk/tools/DCCMTool.exe` | .NET apphost (the launcher of `DCCMTool.dll`) | 10.0.12 | MIT | [dotnet/dotnet@95017c7, `src/runtime/src/native/corehost`](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701/src/runtime/src/native/corehost) | [dotnet.txt](licenses/third-party/dotnet.txt) |

The runtime's THIRD-PARTY-NOTICES file lists the third-party code compiled into .NET. For example, zlib-ng and Brotli were found in the single-file host. The whole file is included because which entries apply to these trimmed and AOT-compiled builds was not determined. `DCCMShell` and `DCCMTool.dll` need an installed .NET 10 and ship no runtime of their own.

#### Native libraries

Paths are in `core/native/win-x64/`. The `.lib`, `.exp` and `.pdb` files next to a binary are build by-products of that binary. The LGPL-licensed files `OpenAL32.dll`, `libtcc.dll`, `tinycc/libtcc1.a`, `cv2pdb.exe` and `goldberg/steam_api64.dll` are listed in [6.3](#63-lgpl-components). The Microsoft and Valve files are in [6.4](#64-microsoft-and-valve-redistributables).

| Files | Component | Version | Licence | Source at that version | Licence text |
|---|---|---|---|---|---|
| `libhl.dll`, `hl.exe`, `hljit.dll` | HashLink: runtime library, VM, and the JIT library that DCCM builds (`hljit.dll` also contains DCCM's `hlc_extern.c`) | commit `d11bb13` (HL_VERSION 2.0.0) | MIT | [HaxeFoundation/hashlink@d11bb13](https://github.com/HaxeFoundation/hashlink/tree/d11bb13fd7140e688162e7dd8693c5b25c011f94) | [HashLink.txt](licenses/third-party/HashLink.txt) |
| `libhl.dll` (compiled in) | PCRE2 | 10.40 | BSD-3-Clause WITH PCRE2-exception | [PCRE2Project/pcre2 tag pcre2-10.40](https://github.com/PCRE2Project/pcre2/tree/pcre2-10.40); copy in [hashlink `include/pcre`](https://github.com/HaxeFoundation/hashlink/tree/d11bb13fd7140e688162e7dd8693c5b25c011f94/include/pcre) | [PCRE2.txt](licenses/third-party/PCRE2.txt) |
| `heaps.hdll` | HashLink `libs/heaps` | commit `d11bb13` | MIT | [hashlink `libs/heaps`](https://github.com/HaxeFoundation/hashlink/tree/d11bb13fd7140e688162e7dd8693c5b25c011f94/libs/heaps) | [HashLink.txt](licenses/third-party/HashLink.txt) |
| `heaps.hdll` (compiled in) | meshoptimizer | 1.2 | MIT | [zeux/meshoptimizer tag v1.2](https://github.com/zeux/meshoptimizer/tree/v1.2); copy in [hashlink `include/meshoptimizer`](https://github.com/HaxeFoundation/hashlink/tree/d11bb13fd7140e688162e7dd8693c5b25c011f94/include/meshoptimizer) | [meshoptimizer.txt](licenses/third-party/meshoptimizer.txt) |
| `heaps.hdll` (compiled in) | V-HACD | 4.1 | BSD-3-Clause | copy in [hashlink `include/vhacd`](https://github.com/HaxeFoundation/hashlink/tree/d11bb13fd7140e688162e7dd8693c5b25c011f94/include/vhacd); upstream [kmammou/v-hacd](https://github.com/kmammou/v-hacd) | [V-HACD.txt](licenses/third-party/V-HACD.txt) |
| `heaps.hdll`, `fmt.hdll` (compiled in) | MikkTSpace | unversioned | Zlib | `heaps.hdll`: copy in [hashlink `include/mikktspace`](https://github.com/HaxeFoundation/hashlink/tree/d11bb13fd7140e688162e7dd8693c5b25c011f94/include/mikktspace). `fmt.hdll`: copy in [hdlls `include/mikktspace`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/include/mikktspace) (set in [hdlls `libs/fmt/CMakeLists.txt`](https://github.com/dead-cells-core-modding/hdlls/blob/e8be52b44290e93452f979796a23918086895f63/libs/fmt/CMakeLists.txt#L1)). The two copies are identical (git blobs `62aa2da` for `mikktspace.c` and `52c44a7` for `mikktspace.h`). Upstream: [mmikk/MikkTSpace](https://github.com/mmikk/MikkTSpace) | [MikkTSpace.txt](licenses/third-party/MikkTSpace.txt) |
| `heaps.hdll` (compiled in) | RenderDoc in-application API header `renderdoc_app.h` (`renderdoc.dll` is not included) | as in hashlink `d11bb13` | MIT | copy in [hashlink `include/renderdoc`](https://github.com/HaxeFoundation/hashlink/tree/d11bb13fd7140e688162e7dd8693c5b25c011f94/include/renderdoc); upstream [baldurk/renderdoc](https://github.com/baldurk/renderdoc) | [renderdoc_app.txt](licenses/third-party/renderdoc_app.txt) |
| `fmt.hdll`, `ssl.hdll`, `uv.hdll`, `sqlite.hdll`, `sdl.hdll`, `openal.hdll`, `ui.hdll` | HashLink bindings, as forked in DCCM's hdlls repository | hdlls commit `e8be52b` | MIT, inferred from HashLink; hdlls has no licence file (not verified). `libs/fmt/sha1.c` and `sha1.h` (in `fmt.hdll`) carry the Haxe Foundation MIT header | [dead-cells-core-modding/hdlls@e8be52b, `libs`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/libs) | [HashLink.txt](licenses/third-party/HashLink.txt) |
| `sdl.hdll` (compiled in) | Khronos OpenGL extension header `glext.h`, which hdlls `libs/sdl/gl.c` includes on Windows | Khronos revision 32749 (2016-04-28) | MIT-style Khronos licence (see file) | copy in [hdlls `include/gl`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/include/gl) | [hdlls-glext.h-Khronos.txt](licenses/third-party/hdlls-glext.h-Khronos.txt) |
| `fmt.hdll` (compiled in) | libpng | 1.6.26 | Libpng | [pnggroup/libpng tag v1.6.26](https://github.com/pnggroup/libpng/tree/v1.6.26) | [libpng.txt](licenses/third-party/libpng.txt) |
| `fmt.hdll` (compiled in) | zlib | 1.2.8 | Zlib | [madler/zlib tag v1.2.8](https://github.com/madler/zlib/tree/v1.2.8) | [zlib-1.2.8-README.txt](licenses/third-party/zlib-1.2.8-README.txt) |
| `fmt.hdll` (compiled in) | libjpeg-turbo: libjpeg API, TurboJPEG API and the prebuilt SIMD library | 1.4.2 | IJG AND BSD-3-Clause AND Zlib | [libjpeg-turbo tag 1.4.2](https://github.com/libjpeg-turbo/libjpeg-turbo/tree/1.4.2) | [libjpeg-turbo-LICENSE.txt](licenses/third-party/libjpeg-turbo-LICENSE.txt), [libjpeg-turbo-README-IJG.txt](licenses/third-party/libjpeg-turbo-README-IJG.txt), [libjpeg-turbo-turbojpeg-BSD-3-Clause.txt](licenses/third-party/libjpeg-turbo-turbojpeg-BSD-3-Clause.txt), [libjpeg-turbo-simd-Zlib.txt](licenses/third-party/libjpeg-turbo-simd-Zlib.txt) |
| `fmt.hdll` (compiled in) | libvorbis, libogg | 1.3.5, 1.3.2 | BSD-3-Clause | [xiph/vorbis tag v1.3.5](https://github.com/xiph/vorbis/tree/v1.3.5), [xiph/ogg tag v1.3.2](https://github.com/xiph/ogg/tree/v1.3.2) | [libvorbis.txt](licenses/third-party/libvorbis.txt), [libogg.txt](licenses/third-party/libogg.txt) |
| `fmt.hdll` (compiled in) | minimp3 | unversioned header | CC0-1.0 | copy in [hdlls `include/minimp3`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/include/minimp3); upstream [lieff/minimp3](https://github.com/lieff/minimp3) | [minimp3-CC0-1.0.txt](licenses/third-party/minimp3-CC0-1.0.txt) |
| `ssl.hdll` (compiled in) | Mbed TLS | 2.7.0 | Apache-2.0 | [Mbed-TLS/mbedtls tag mbedtls-2.7.0](https://github.com/Mbed-TLS/mbedtls/tree/mbedtls-2.7.0); copy in [hdlls `include/mbedtls`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/include/mbedtls) | [mbedtls-2.7.0-LICENSE.txt](licenses/third-party/mbedtls-2.7.0-LICENSE.txt), [mbedtls-2.7.0-Apache-2.0.txt](licenses/third-party/mbedtls-2.7.0-Apache-2.0.txt) |
| `uv.hdll` (compiled in) | libuv | 1.9.1 | MIT AND ISC AND BSD-2-Clause | [libuv/libuv tag v1.9.1](https://github.com/libuv/libuv/tree/v1.9.1) | [libuv.txt](licenses/third-party/libuv.txt) |
| `sqlite.hdll` (compiled in) | SQLite amalgamation | 3.15.1 | blessing (public-domain dedication) | copy in [hdlls `include/sqlite`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/include/sqlite) | none: there is no licence text |
| `steam.hdll` | hlsteam (HashLink Steam API wrapper). It is also compiled against the Valve Steamworks SDK 1.64 headers and import library (6.4) | hdlls commit `e8be52b` | MIT | [hdlls `libs/hlsteam`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/libs/hlsteam) | [hlsteam.txt](licenses/third-party/hlsteam.txt) |
| `gog.hdll` | hlgog stub, built only from `libs/hlgog/src/dummy.c` (no GOG SDK code) | hdlls commit `e8be52b` | MIT, as declared in the DCCM README; hdlls has no licence file | [hdlls `libs/hlgog`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/libs/hlgog) | [DCCM.txt](licenses/third-party/DCCM.txt) |
| `discord.hdll` | hldiscord glue code, statically linked with a prebuilt `discord-rpc.lib` (Discord RPC), which normally includes RapidJSON | Discord RPC: not identified; RapidJSON 1.1.0 (inferred, not verified) | MIT; the glue code states no licence (not verified) | [hdlls `libs/hldiscord`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/libs/hldiscord); [discord/discord-rpc](https://github.com/discord/discord-rpc); [Tencent/rapidjson tag v1.1.0](https://github.com/Tencent/rapidjson/tree/v1.1.0) | [discord-rpc.txt](licenses/third-party/discord-rpc.txt), [rapidjson.txt](licenses/third-party/rapidjson.txt) |
| `chroma.hdll` | hlchroma glue code compiled with Razer Chroma SDK headers (`RzChromaSDK64.dll` is not included) | hdlls commit `e8be52b` | NOASSERTION: no licence stated (not verified) | [hdlls `libs/hlchroma`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/libs/hlchroma) | none |
| `SDL3.dll` | SDL, the official binary from `SDL3-devel-3.4.14-VC.zip`, including HIDAPI and yuv2rgb. The x64 release binary is built from [`VisualC/SDL/SDL.vcxproj`](https://github.com/libsdl-org/SDL/blob/release-3.4.14/VisualC/SDL/SDL.vcxproj) (per [`build-scripts/release-info.json`](https://github.com/libsdl-org/SDL/blob/release-3.4.14/build-scripts/release-info.json)), which also compiles `src/libm/s_modf.c`, taken from Sun's math library | 3.4.14 | Zlib; HIDAPI: BSD-3-Clause (one of its three licence options); yuv2rgb: BSD-3-Clause; `s_modf.c`: SunPro | [libsdl-org/SDL tag release-3.4.14](https://github.com/libsdl-org/SDL/tree/release-3.4.14) | [SDL3.txt](licenses/third-party/SDL3.txt), [SDL3-hidapi-LICENSE.txt](licenses/third-party/SDL3-hidapi-LICENSE.txt), [SDL3-hidapi-LICENSE-bsd.txt](licenses/third-party/SDL3-hidapi-LICENSE-bsd.txt), [SDL3-hidapi-LICENSE-orig.txt](licenses/third-party/SDL3-hidapi-LICENSE-orig.txt), [SDL3-yuv2rgb.txt](licenses/third-party/SDL3-yuv2rgb.txt), [SDL3-libm-Sun.txt](licenses/third-party/SDL3-libm-Sun.txt) |
| `OpenAL32.dll` (compiled in) | Code bundled in OpenAL Soft: {fmt}, PFFFT (modified), its BSD-3-Clause files and Microsoft GSL (whether GSL code is compiled in was not verified) | {fmt} 11.2.0; the rest as in OpenAL Soft 1.25.2 | MIT; LicenseRef-pffft; BSD-3-Clause; MIT | [kcat/openal-soft tag 1.25.2](https://github.com/kcat/openal-soft/tree/1.25.2) | [fmt.txt](licenses/third-party/fmt.txt), [openal-soft-pffft.txt](licenses/third-party/openal-soft-pffft.txt), [openal-soft-BSD-3-Clause.txt](licenses/third-party/openal-soft-BSD-3-Clause.txt), [ms-gsl.txt](licenses/third-party/ms-gsl.txt), [ms-gsl-ThirdPartyNotices.txt](licenses/third-party/ms-gsl-ThirdPartyNotices.txt) |
| `OpenAL32.dll` (statically linked) | The MinGW-w64 runtime (mingw-w64-crt start-up and support code: the DLL contains its "Mingw-w64 runtime failure:" and pseudo-relocation messages and uses the system `msvcrt.dll` as its C library); MinGW-w64 winpthreads; the GCC 15 C++ and support runtime (libstdc++, libgcc) | MinGW-w64: not identified; GCC 15 | MinGW-w64 runtime: the notices in its runtime licence file (see file); MIT AND BSD-3-Clause (winpthreads); GPL-3.0-or-later WITH GCC-exception-3.1 (GCC runtime) | [mingw-w64/mingw-w64](https://github.com/mingw-w64/mingw-w64); [GCC](https://gcc.gnu.org/) | [mingw-w64-runtime.txt](licenses/third-party/mingw-w64-runtime.txt), [mingw-w64-winpthreads.txt](licenses/third-party/mingw-w64-winpthreads.txt), [GCC-runtime-library-exception-3.1.txt](licenses/third-party/GCC-runtime-library-exception-3.1.txt), [GPL-3.0.txt](licenses/third-party/GPL-3.0.txt) |
| `goldberg/steam_api64.dll` (statically linked) | libcurl, Mbed TLS, zlib, Protocol Buffers with Abseil and utf8_range, Opus, PortAudio and libssq. Also the header-only libraries nlohmann/json, stb, utfcpp, simpleini, sha1 and fifo_map | libcurl 8.20.0, Mbed TLS 3.6.6, zlib 1.3.2, Protocol Buffers 34.1, Abseil 20250512.1, Opus 1.6.1, PortAudio `b0fe9de`, libssq 3.0.1 | curl; Apache-2.0 OR GPL-2.0-or-later; Zlib; BSD-3-Clause; Apache-2.0; MIT; BSD-3-Clause; MIT; MIT. Header-only libraries: see `gbe_fork-CREDITS.txt` | gbe_fork's copies of these sources at [Detanup01/gbe_fork@92a4a13](https://github.com/Detanup01/gbe_fork/tree/92a4a130262083c4e887155cbe6bcab99baf36ea), the `third-party/deps/common` submodule commit pinned by release-2026_05_30 (the other submodules are listed in 6.3) | [curl.txt](licenses/third-party/curl.txt), [mbedtls-3.6.6.txt](licenses/third-party/mbedtls-3.6.6.txt), [zlib-1.3.2.txt](licenses/third-party/zlib-1.3.2.txt), [protobuf.txt](licenses/third-party/protobuf.txt), [abseil-cpp-Apache-2.0.txt](licenses/third-party/abseil-cpp-Apache-2.0.txt), [protobuf-utf8_range.txt](licenses/third-party/protobuf-utf8_range.txt), [opus.txt](licenses/third-party/opus.txt), [portaudio.txt](licenses/third-party/portaudio.txt), [libssq.txt](licenses/third-party/libssq.txt), [gbe_fork-CREDITS.txt](licenses/third-party/gbe_fork-CREDITS.txt) |

libjpeg-turbo's licence asks binary distributions to state the following, which applies to `fmt.hdll`: This software is based in part on the work of the Independent JPEG Group.

#### Other contents

- `core/mdk/ref/GameProxy.dll` (15,980,440 bytes) is part of DCCM's published release. It is a reference assembly that DCCM's build generates (`build/Build.cs`, target `GenerateGameProxy`). The build runs DCCMTool on the Dead Cells boot bytecode files `hlboots/hlboot-opengl-steam.dat` and `hlboot-opengl-gog.dat`, which are committed in the DCCM repository. The assembly declares 58,620 types, 320,836 methods and 1,590 fields that mirror the game's code. It is marked with `ReferenceAssemblyAttribute`, and the method bodies sampled are stubs. DCCM's MDK build files reference it for compiling mods only (`Private=false`). This audit made no assessment of its licence status.
- `core/mdk/databases/v35/data.cdb` (2,434,393 bytes) is a CastleDB database with 162 sheets (items, mobs, weapons and more). DCCM's MDK uses it as the template database for game version 35. It appears to be the game's own data. It is byte-identical to `mdk/MDK.Package/raw/databases/v35/data.cdb` in the DCCM repository. Its origin and licence were not established (not verified).
- `core/mdk/tools/tmxtool.hl` (1,047,936 bytes) is HashLink bytecode (`TmxToolApp`) that DCCMTool runs. It is byte-identical to `mdk/DCCMTool/tmxtool.hl` in the DCCM repository, which holds no source code for it. It contains debug paths that name `motion-twin\deadcells`. Its origin and licence were not established (not verified).
- Whether redistributing these three files is acceptable was not assessed (see [6.6](#66-open-points-for-the-release)).

### 6.2 DeadCellsMultiplayerMod 0.5.1 release archive (`base/upstream/DeadCellsMultiplayerMod_0.5.1.zip`)

| Files | Component | Version | Licence | Source at that version | Licence text |
|---|---|---|---|---|---|
| `DeadCellsMultiplayerMod/DeadCellsMultiplayerMod.dll`, `.pdb` | DeadCellsMultiplayerMod | 0.5.1 (informational version `0.5.1-vanilla-host-authoritative-mobsync+57af5cf`) | MIT | [vaiserYT/DeadCellsMultiplayerMod@57af5cf](https://github.com/vaiserYT/DeadCellsMultiplayerMod/tree/57af5cfa16d8e5b5b11399b768f508008a127758) | [DeadCellsMultiplayerMod.txt](licenses/third-party/DeadCellsMultiplayerMod.txt) |
| `DeadCellsMultiplayerMod/res.pak` (the mod's translations), `modinfo.json`, `.dcmm-installed-build.json` | DeadCellsMultiplayerMod | 0.5.1 | MIT | same | same |
| `DeadCellsMultiplayerMod/steam_api64.dll` | Valve Steamworks API | 10.51.08.25 | Valve Steamworks SDK terms | none | see [6.4](#64-microsoft-and-valve-redistributables) |

The mod's DLL embeds no third-party code; it uses the libraries that DCCM provides. `.dcmm-installed-build.json` records version `v0.4.2` and looks like leftover mod-manager metadata.

### 6.3 LGPL components

The DCCM 35.13.5 release archive contains the LGPL-licensed binaries below. Some of them also contain GPL-licensed parts. **These are unmodified binaries exactly as published in the DCCM 35.13.5 release.** The installer's `base/upstream/win-x64.zip` is byte-identical to that release's `win-x64.zip` (see the hash above). **Twin Cells did not modify, rebuild or relink them.**

| File in the DCCM archive | Component | Version | Licence | Corresponding source | Licence text |
|---|---|---|---|---|---|
| `core/native/win-x64/OpenAL32.dll` | OpenAL Soft: the official Windows binary `bin/Win64/soft_oal.dll` from `openal-soft-1.25.2-bin.zip`, copied as `OpenAL32.dll` by the hdlls build ([`libs/openal/CMakeLists.txt`, lines 8 and 28-33](https://github.com/dead-cells-core-modding/hdlls/blob/e8be52b44290e93452f979796a23918086895f63/libs/openal/CMakeLists.txt#L28-L33)) | 1.25.2 (internal version strings; the file's version resource says 1.25.1) | LGPL-2.0-or-later | [kcat/openal-soft tag 1.25.2](https://github.com/kcat/openal-soft/tree/1.25.2) (commit `b2c48f7`). Binary: [openal-soft-1.25.2-bin.zip](https://github.com/kcat/openal-soft/releases/download/1.25.2/openal-soft-1.25.2-bin.zip) | [openal-soft-LGPL-2.0.txt](licenses/third-party/openal-soft-LGPL-2.0.txt); its bundled code is in 6.1 |
| `core/native/win-x64/libtcc.dll` (+ `libtcc.pdb`), `core/native/win-x64/tinycc/tccdefs.h` | TinyCC (libtcc), DCCM's fork | 0.9.28rc, commit `7b443d3` | LGPL-2.1-or-later. `COPYING` is the LGPL-2.1 text; the file headers (for example of `libtcc.c` and `lib/bcheck.c`) say "version 2 of the License, or (at your option) any later version" | [dead-cells-core-modding/tinycc@7b443d3](https://github.com/dead-cells-core-modding/tinycc/tree/7b443d38bcad1cfa86a487c9bbff2f78ed90f40b), a fork of [TinyCC/tinycc](https://github.com/TinyCC/tinycc). DCCM's build files: [`sources/native` at v35.13.5](https://github.com/dead-cells-core-modding/core/tree/d46d0e158549c3c506d638cea3e3996f8eac7508/sources/native) (its `CMakeLists.txt` and `CMakePresets.json`, and the wrapper [`sources/native/tinycc`](https://github.com/dead-cells-core-modding/core/tree/d46d0e158549c3c506d638cea3e3996f8eac7508/sources/native/tinycc)) | [tinycc-LGPL-2.1.txt](licenses/third-party/tinycc-LGPL-2.1.txt), [tinycc-RELICENSING.txt](licenses/third-party/tinycc-RELICENSING.txt) |
| `core/native/win-x64/tinycc/libtcc1.a` | TinyCC runtime library: an archive of object files that is not linked into any program | commit `7b443d3` | LGPL-2.1-or-later (as for `libtcc.dll`) AND GPL-2.0-or-later (`lib/libtcc1.c`, with a linking exception) AND GPL-3.0-or-later WITH GCC-exception-3.1 (`lib/atomic.S`) | [tinycc@7b443d3, `lib/`](https://github.com/dead-cells-core-modding/tinycc/tree/7b443d38bcad1cfa86a487c9bbff2f78ed90f40b/lib) | [tinycc-LGPL-2.1.txt](licenses/third-party/tinycc-LGPL-2.1.txt), [tinycc-libtcc1.c-header.txt](licenses/third-party/tinycc-libtcc1.c-header.txt), [GPL-2.0.txt](licenses/third-party/GPL-2.0.txt), [tinycc-atomic.S-header.txt](licenses/third-party/tinycc-atomic.S-header.txt), [GPL-3.0.txt](licenses/third-party/GPL-3.0.txt), [GCC-runtime-library-exception-3.1.txt](licenses/third-party/GCC-runtime-library-exception-3.1.txt) |
| `core/native/win-x64/cv2pdb.exe` (+ `cv2pdb.pdb`) | cv2pdb, DCCM's fork | 0.54, commit `e74fefa` | Artistic-2.0 AND LGPL-2.1-or-later (the header `mscvpdb.h`, from WINE) AND LGPL-2.1-only (the header `dwarf.h`, from libdwarf: its own header says "version 2.1", while cv2pdb's `README.MD` says "version 2.1 of the License, or (at your option) any later version") AND MIT (Microsoft's `Setup.Configuration.h`) | [dead-cells-core-modding/cv2pdb@e74fefa](https://github.com/dead-cells-core-modding/cv2pdb/tree/e74fefa87452d58e0b4a941ad788bd281cf6168a), a fork of [rainers/cv2pdb](https://github.com/rainers/cv2pdb). DCCM's build files: [`sources/native` at v35.13.5](https://github.com/dead-cells-core-modding/core/tree/d46d0e158549c3c506d638cea3e3996f8eac7508/sources/native) (its `CMakeLists.txt` and `CMakePresets.json`, and [`sources/native/cv2pdb`](https://github.com/dead-cells-core-modding/core/tree/d46d0e158549c3c506d638cea3e3996f8eac7508/sources/native/cv2pdb)) | [cv2pdb-Artistic-2.0.txt](licenses/third-party/cv2pdb-Artistic-2.0.txt), [tinycc-LGPL-2.1.txt](licenses/third-party/tinycc-LGPL-2.1.txt) (the same LGPL-2.1 text), [cv2pdb-Setup.Configuration.h-MIT.txt](licenses/third-party/cv2pdb-Setup.Configuration.h-MIT.txt) |
| `core/native/win-x64/goldberg/steam_api64.dll` | Goldberg Steam Emulator, gbe_fork continuation (version resource "GSE Client API" 08.33.09.23) | most likely gbe_fork release-2026_05_30 (not verified) | LGPL-3.0-or-later | [Detanup01/gbe_fork tag release-2026_05_30](https://github.com/Detanup01/gbe_fork/tree/release-2026_05_30) (commit `909e286`; probable, not hash-verified, see 6.5). The tag pins these submodules for shared and Windows-specific dependencies and build files. They are branches of the same repository, so GitHub's source archive of the tag does not contain them: `third-party/deps/common` [92a4a13](https://github.com/Detanup01/gbe_fork/tree/92a4a130262083c4e887155cbe6bcab99baf36ea), `third-party/deps/win` [83308a2](https://github.com/Detanup01/gbe_fork/tree/83308a20b093ff955fd8119726eb6d531027b2a6), `third-party/build/win` [556998f](https://github.com/Detanup01/gbe_fork/tree/556998fa1c2df35e7d208c1ddfb7445080ab2ec2) and `third-party/common/win` [d431ffc](https://github.com/Detanup01/gbe_fork/tree/d431ffceaadf4e279e70bc4adea3ae42be6820ac). The same file is in the DCCM repository at [`3rd/Goldberg` at v35.13.5](https://github.com/dead-cells-core-modding/core/tree/d46d0e158549c3c506d638cea3e3996f8eac7508/3rd/Goldberg). Original project: [Mr_Goldberg/goldberg_emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) | [Goldberg-LGPL-3.0.txt](licenses/third-party/Goldberg-LGPL-3.0.txt), [GPL-3.0.txt](licenses/third-party/GPL-3.0.txt); its statically linked libraries are in 6.1 |

How these files reach the DCCM release:

- DCCM's build compiles `libtcc.dll` and `cv2pdb.exe` from the sources above. [`sources/native/CMakeLists.txt`](https://github.com/dead-cells-core-modding/core/blob/d46d0e158549c3c506d638cea3e3996f8eac7508/sources/native/CMakeLists.txt) adds `3rd/tinycc`, the `tinycc` wrapper and `cv2pdb` (lines 94, 114 and 119).
- DCCM's CI downloads the official OpenAL Soft binary into `sources/native/3rd/hashlink/include/openal` ([`.github/workflows/build.yml`, lines 252-254](https://github.com/dead-cells-core-modding/core/blob/d46d0e158549c3c506d638cea3e3996f8eac7508/.github/workflows/build.yml#L252-L254)). The hdlls build then copies its `soft_oal.dll` to `OpenAL32.dll` (see the table).
- The Goldberg DLL comes from `3rd/Goldberg` in the DCCM repository.

The installer includes neither the source code of these components nor a written offer for it. The links above point to the source as published by these projects, at the tag or commit each binary was built from. The exception is the Goldberg DLL, whose release is identified only from circumstantial evidence (see [6.5](#65-not-verified)). Whether these links meet the licences' source requirements was not determined (see [6.6](#66-open-points-for-the-release)).

**You may replace these files with your own builds.** Their licences let you modify them and use your modified builds: the LGPL, plus the Artistic License 2.0 for `cv2pdb.exe` and the GPL for parts of `libtcc1.a`. Each one is a separate file, so you can replace it with your own build of the same component at the same path in the installed DCCM files, for example a build made from the corresponding source above.

`lib/libtcc1.c` is licensed under the GPL (version 2 or later) with an extra permission. Its header grants unlimited permission to link its compiled form into combinations with other programs and to distribute those combinations "without any restriction coming from the use of this file". The same header says that the GPL restrictions still apply in other respects, including to "distribution when not linked into a combine executable". `libtcc1.a` is shipped unlinked. Its source is at the TinyCC commit above, and the full header is in [tinycc-libtcc1.c-header.txt](licenses/third-party/tinycc-libtcc1.c-header.txt).

### 6.4 Microsoft and Valve redistributables

| Files | Component | Version | Terms | Licence text |
|---|---|---|---|---|
| `core/native/win-x64/vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll` | Microsoft Visual C++ runtime, as app-local copies. DCCM's build copies them from the compiler directory (`sources/native/modcorenative/CMakeLists.txt`) | 14.51.36256.0 (MSVC 14.51, Visual Studio 2026) | Microsoft Software License Terms for Visual Studio, "Distributable Code" | none: Microsoft publishes no separate text to ship |
| Statically linked into `core/native/win-x64/modcorenative.dll` (LIBCMT, libvcruntime, libucrt) and `core/host/startup/DeadCellsModding.exe` (LIBCMT, libvcruntime) | Microsoft C/C++ static runtime libraries | MSVC 14.51.36231; UCRT from Windows SDK 10.0.26100.0 | Microsoft Software License Terms for Visual Studio | none |
| Statically linked into `core/native/win-x64/goldberg/steam_api64.dll`. It imports no C runtime DLL and contains `minkernel\crts\ucrt\inc\corecrt_internal_strtox.h` and "Microsoft Visual C++ Runtime Library"; its build paths name `vs2022` | Microsoft C/C++ static runtime libraries and UCRT | not identified (Visual Studio 2022 toolchain) | Microsoft Software License Terms for Visual Studio | none |
| `core/native/win-x64/nethost.dll` | .NET nethost | 9.0.6 (commit `3875b54`) | MIT | [dotnet.txt](licenses/third-party/dotnet.txt), [dotnet-runtime-9.0.6-THIRD-PARTY-NOTICES.txt](licenses/third-party/dotnet-runtime-9.0.6-THIRD-PARTY-NOTICES.txt); source: [dotnet/runtime tag v9.0.6](https://github.com/dotnet/runtime/tree/v9.0.6) |
| `core/native/win-x64/steam_api64.dll` and, in the base mod's archive, `DeadCellsMultiplayerMod/steam_api64.dll` (identical files, SHA-256 `eb17909a76668cf9ae0b92a618a34a50f6c73d3a6787cb4dd8ce36a8b10bfb75`) | Valve Steamworks API ("Steam Client API") | 10.51.08.25 | Steamworks SDK Access Agreement | none: Valve publishes no text to ship |
| `core/mdk/tools/steam_api64.dll` (SHA-256 `e3fe06e2d802c2f753aeb46869e2af898f34715ce85f08001456fd8a80ecd21e`). `SteamLauncher.dll`, which is bundled in both `deadcells.exe` (identical copies, SHA-256 `aaa6985b523b2595c8cffcaef51eeb8c38e4396eb53a2c196f5c157620de5583`), embeds the same `steam_api64.dll` and Valve's Linux library `libsteam_api.so` (SHA-256 `3b3f6b452a0151a14cd3926c7f285e6def243a4ac73165fbb3d28fa4a6b0a634`) as resources | Valve Steamworks API | `steam_api64.dll`: 10.24.16.75; `libsteam_api.so`: not identified | Steamworks SDK Access Agreement | none |

The terms cited above:

- **Visual C++ runtime.** Microsoft's [Redistribute Visual C++ Files](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files) page says that distribution of the redistributable package, merge modules and individual binaries "is limited to licensed Visual Studio users". It is subject to the Microsoft Software License Terms. The same page describes installing the DLLs in the application's local folder but does not recommend it, and it recommends the Visual C++ Redistributable packages instead. The [Visual Studio 2026 distributable code list](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution) lets holders of a validly licensed copy distribute, with their program and unmodified, the files in `VC\redist`. Whether these terms cover someone who redistributes DCCM's unchanged archive was not determined.
- **nethost.dll** is MIT-licensed .NET code, with the licence and notices linked in the table.
- **steam_api64.dll.** Section 1.1(b) of Valve's [Steamworks SDK Access Agreement](https://partner.steamgames.com/documentation/sdk_access_agreement) grants a licensee the right to reproduce and distribute the SDK's `redistributable_bin` files "along with the Licensee Software in object code form".
  - Whether DCCM, the base mod's author or Twin Cells is a Steamworks licensee was not verified.
  - The 10.51.08.25 file is identical to `3rd/Steamworks.NET/Windows-x64/steam_api64.dll` in the DCCM repository. The 10.24.16.75 file is identical to `sources/SteamLauncher/steam_api64.dll`, and the embedded `libsteam_api.so` is identical to `sources/SteamLauncher/libsteam_api.so`.
  - Both embedded files were extracted from the `SteamLauncher.dll` resources in both `deadcells.exe` and hash-compared.
  - `steam.hdll` (6.1) is also compiled against the Steamworks SDK 1.64 headers and import library. The SDK version is inferred.

### 6.5 Not verified

- **hdlls has no licence file.** The hdlls repository at `e8be52b` has no top-level licence. The binding sources in `fmt`, `ssl`, `uv`, `sqlite`, `sdl`, `openal` and `ui` derive from HashLink (MIT). Apart from `libs/fmt/sha1.c` and `sha1.h`, which carry the Haxe Foundation MIT header, they carry no licence headers. The hldiscord and hlchroma sources state no licence. The DCCM README declares MIT only for hlsteam and hlgog.
- **Razer Chroma SDK headers.** The Razer Chroma SDK headers compiled into `chroma.hdll` carry no licence, and Razer's SDK terms were not checked.
- **discord.hdll.** The version of the prebuilt `discord-rpc.lib` is unknown. That it contains RapidJSON 1.1.0 is inferred from Discord RPC's build. The Discord RPC licence text comes from its default branch.
- **Goldberg DLL release.** `goldberg/steam_api64.dll` is identified as gbe_fork release-2026_05_30 only from circumstantial evidence. Its build paths start with `D:\a\gbe_fork\gbe_fork\build\deps\win\vs2022`. Its PE timestamp, 2026-05-30 21:37:54 UTC, falls between the release's publication (21:10:23 UTC) and the upload of `emu-win-release-vs22.7z` (21:45:12 UTC); the non-vs22 `emu-win-release.7z` was uploaded earlier, at 21:25:13 UTC. Its version resource ("GSE Client API" 08.33.09.23) does not name a release. The DLL was not hash-compared with that release's files.
- **OpenAL32.dll and SDL3.dll.** Neither file was hash-compared with the official binary archive it comes from. For OpenAL Soft, the version resource (1.25.1) and the internal version strings (1.25.2) disagree. The licence files are identical at both tags. Whether code from SDL's `s_modf.c` is present in `SDL3.dll` was not checked.
- **OpenAL32.dll's statically linked code.** That Microsoft GSL code is compiled into `OpenAL32.dll` was not proven. Which mingw-w64-crt objects are linked (for example its gdtoa or math code, whose notices are in the runtime licence file) was not checked. The MinGW-w64 runtime and winpthreads licence texts come from the default branch, and the exact MinGW-w64 toolchain version is unknown.
- **cv2pdb's demangle.cpp.** `src/demangle.cpp` in cv2pdb says it was "adopted from demangle.d distributed with DMD" and names Walter Bright (Digital Mars) among its authors, but states no licence. cv2pdb's `README.MD` says it "is placed into the Public Domain". This was not checked against the DMD sources.
- **Mbed TLS 2.7.0.** Its copyright line was not checked against a source file.
- **.NET THIRD-PARTY-NOTICES.** Which entries of the .NET THIRD-PARTY-NOTICES files apply to these trimmed, AOT-compiled and single-file builds was not determined.
- **Managed DLLs.** The third-party managed DLLs were not byte-compared with the official NuGet packages. They were identified from DCCM's `deps.json` entries, assembly metadata and the source commits recorded in the packages.
- **SharpPdb and SharpUtilities.** The exact source commits of SharpPdb 1.0.4, SharpPdb.Windows 1.0.4 and SharpUtilities 1.4.6 are unknown.
- **K4os.Hash.xxHash.** The tag 1.0.8 is assumed to match the 1.0.8 package.
- **HarmonyX.** HarmonyX's licence names only "BepInEx". The package lists Andreas Pardeike, the author of Harmony, among its authors. Whether a separate Harmony notice is needed was not determined.
- **stb ports.** The StbSharp repositories have no licence file. The text used is the stb licence that ends the headers the ports were generated from.
- **MonoMod.Backports.** It may contain code ported from dotnet/runtime; per-file origin was not checked.
- **Projects credited in the DCCM README.** The DCCM README credits HashlinkNET (DreamBoxSpy, MIT), sharplink (Tomat, MIT), and DeadCellsDecomp and alivecells (N3rdL0rd, both MIT on their default branches). Whether code from them was copied into DCCM's assemblies was not determined.
- **RectpackSharp.** The upstream version of the source copied into `GameRes.Core.dll` was not identified.
- **Newtonsoft.Json.xml in core/mdk.** `core/mdk/Newtonsoft.Json.xml` is presumed to be the 13.0.4 documentation file.
- **GameProxy.dll, data.cdb and tmxtool.hl.** For these three files (see 6.1, "Other contents"), the origin and basis for redistribution were not established, and no legal assessment was made.
- **Microsoft and Valve terms.** Whether the Microsoft and Valve terms in 6.4 cover a redistributor of the unchanged upstream archives was not determined.
- **gbe_fork dependency records.** gbe_fork's dependency record for zlib (`zlib/SOURCE.txt`, and the zlib section of `CREDITS.md`) contains libssq's licence text by mistake. Its records for libcurl and libssq carry older copyright years than the versions they name (curl "1996 - 2024" against "1996 - 2026" at tag curl-8_20_0; libssq "2023" against "2024" at tag v3.0.1). The zlib, libcurl and libssq licences were therefore taken from the upstream repositories at the tags of those versions.
- **gbe_fork parts with no texts here.** gbe_fork's build lists SDL 3.4.8 among the linked libraries, but no SDL code was found in the DLL. The ingame_overlay, imgui and detours parts are used only by gbe_fork's experimental build and were not found either. No separate texts for these are included. gbe_fork's credits file covers them.

### 6.6 Open points for the release

These points concern the published release rather than this repository. They are open decisions for the project owner. This is not legal advice.

- **The Alpha1 installer's own notices.** The installer attached to the [Alpha1 release](https://github.com/Avenged-7wrx/TwinCells/releases/tag/v0.1.0-alpha) was built before this section was written. Its `base/licenses` folder holds three MIT texts (DCCM, DeadCellsMultiplayerMod and CoopSyncFixes), and its `base/THIRD-PARTY-NOTICES.md` lists DCCM simply as MIT. It ships none of the texts in [`licenses/third-party`](licenses/third-party/). The release asset has not been updated.
- **Source code of the LGPL and GPL components.** The installer ships no source code and no written offer for the components in 6.3; this file links to the upstream source. The licences set conditions on distributing object code: LGPL-2.0 and LGPL-2.1 section 4 (OpenAL Soft, TinyCC, cv2pdb's LGPL headers), GPL-2.0 section 3 (`lib/libtcc1.c`, shipped unlinked in `libtcc1.a`) and GPL-3.0 section 6, which LGPL-3.0 incorporates (Goldberg). Whether links to other projects' repositories meet these conditions was not determined. One option is to attach the corresponding source to the GitHub release as extra assets: TinyCC at `7b443d3`, cv2pdb at `e74fefa`, OpenAL Soft 1.25.2, gbe_fork release-2026_05_30 with the submodules listed in 6.3, and DCCM's `sources/native` at v35.13.5. Another option is a written offer.
- **Files generated from the game or appearing to come from it.** DCCM's release archive ships `GameProxy.dll`, `data.cdb` and `tmxtool.hl` (see [6.1, Other contents](#other-contents)). Whether redistributing them is acceptable was not assessed.
