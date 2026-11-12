# Third-party licence texts

This folder holds the licence texts of the third-party components that the Twin Cells release installer redistributes. The installer contains the official DCCM 35.13.5 and DeadCellsMultiplayerMod 0.5.1 release archives unchanged. Which installer files each text applies to is listed in [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md), section 6.

How the texts were obtained (2026-11-12):

- Each file is a byte-for-byte copy of the upstream file named in the last column, at the tag or commit shown. The files were fetched from raw.githubusercontent.com, or from gnu.org for the two GNU GPL texts. Nothing was retyped or edited. Only the file names were changed.
- Where a project has no separate licence file at that version, the file is a verbatim excerpt of the licence header of the source file named, with its line numbers.
- "Default branch" means that no file tied to the shipped version could be found, so the text was taken from the project's default branch.
- One file is kept per distinct text. When several components use the same text, one file covers all of them.

## DCCM and DeadCellsMultiplayerMod

| File | Component(s) | Licence (SPDX) | Fetched from |
|---|---|---|---|
| [DCCM.txt](DCCM.txt) | Dead Cells Core Modding (DCCM) 35.13.5, DCCM's own code. Also `gog.hdll` (hlgog), which the DCCM README declares MIT | MIT | [dead-cells-core-modding/core@d46d0e1 `LICENSE`](https://github.com/dead-cells-core-modding/core/blob/d46d0e158549c3c506d638cea3e3996f8eac7508/LICENSE) (tag v35.13.5) |
| [DeadCellsMultiplayerMod.txt](DeadCellsMultiplayerMod.txt) | DeadCellsMultiplayerMod 0.5.1 | MIT | [vaiserYT/DeadCellsMultiplayerMod@57af5cf `LICENSE`](https://github.com/vaiserYT/DeadCellsMultiplayerMod/blob/57af5cfa16d8e5b5b11399b768f508008a127758/LICENSE) |

## Managed libraries

| File | Component(s) | Licence (SPDX) | Fetched from |
|---|---|---|---|
| [HarmonyX.txt](HarmonyX.txt) | HarmonyX 2.16.1 (`0Harmony.dll`) | MIT | [BepInEx/HarmonyX@02a98f5 `LICENSE`](https://github.com/BepInEx/HarmonyX/blob/02a98f540fa12ac4656f189a3cf67b0c96cc68f7/LICENSE) |
| [K4os.Hash.xxHash.txt](K4os.Hash.xxHash.txt) | K4os.Hash.xxHash 1.0.8 | MIT | [MiloszKrajewski/K4os.Hash.xxHash tag 1.0.8 `LICENSE`](https://github.com/MiloszKrajewski/K4os.Hash.xxHash/blob/1.0.8/LICENSE) |
| [MemoryPack.txt](MemoryPack.txt) | MemoryPack.Core 1.21.4 | MIT | [Cysharp/MemoryPack@0f2fe08 `LICENSE.md`](https://github.com/Cysharp/MemoryPack/blob/0f2fe0827907940768293b7bb81b9605a7d8cba8/LICENSE.md) |
| [Mono.Cecil.txt](Mono.Cecil.txt) | Mono.Cecil 0.11.6 (Mono.Cecil, .Mdb, .Pdb, .Rocks) | MIT | [jbevain/cecil tag 0.11.6 `LICENSE.txt`](https://github.com/jbevain/cecil/blob/0.11.6/LICENSE.txt) |
| [MonoMod.txt](MonoMod.txt) | MonoMod.Core 1.3.6, MonoMod.RuntimeDetour 25.3.6, MonoMod.Utils 25.0.14, MonoMod.Backports 1.1.2, MonoMod.ILHelpers 1.1.0 | MIT | [MonoMod/MonoMod@a74a1cf `LICENSE`](https://github.com/MonoMod/MonoMod/blob/a74a1cf0d15c5e82dfa6ef8bbe7a131b60bed3fd/LICENSE). Identical at [a1b8285](https://github.com/MonoMod/MonoMod/blob/a1b82852b2574742776af08818487b90b0bfab93/LICENSE) (Backports, ILHelpers) |
| [Iced.txt](Iced.txt) | iced 1.21.0 (`MonoMod.Iced.dll`, part of the MonoMod.Core package) | MIT | [icedland/iced tag v1.21.0 `LICENSE.txt`](https://github.com/icedland/iced/blob/v1.21.0/LICENSE.txt) |
| [Newtonsoft.Json.txt](Newtonsoft.Json.txt) | Json.NET (Newtonsoft.Json) 13.0.4 and 13.0.3 | MIT | [JamesNK/Newtonsoft.Json@4e13299 `LICENSE.md`](https://github.com/JamesNK/Newtonsoft.Json/blob/4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a/LICENSE.md) (13.0.4). Identical at [0a2e291](https://github.com/JamesNK/Newtonsoft.Json/blob/0a2e291c0d9c0c7675d445703e51750363a549ef/LICENSE.md) (13.0.3) |
| [SDL3-CS.txt](SDL3-CS.txt) | SDL3-CS 3.4.14.1 | Zlib | [edwardgushchin/SDL3-CS@b525db5 `LICENSE`](https://github.com/edwardgushchin/SDL3-CS/blob/b525db5bf89a46c3416efe21b09431c28cf00b8d/LICENSE) |
| [Serilog-Apache-2.0.txt](Serilog-Apache-2.0.txt) | Serilog 4.4.0 and 4.2.0 | Apache-2.0 | [serilog/serilog@497f80f `LICENSE`](https://github.com/serilog/serilog/blob/497f80fda4f9e8f98b9c13ba34b1f0530f8c4449/LICENSE) (4.4.0). Identical at [c00e17f](https://github.com/serilog/serilog/blob/c00e17fdeb615d36c062569f7342f72c9ece4aa7/LICENSE) (4.2.0) |
| [Serilog.Sinks-Apache-2.0.txt](Serilog.Sinks-Apache-2.0.txt) | Serilog.Sinks.Console 6.1.1, Serilog.Sinks.File 7.0.0, Serilog.Sinks.TextWriter 3.0.0, Serilog.Sinks.Trace 4.0.0 | Apache-2.0 | [serilog/serilog-sinks-console@b3cb1f0 `LICENSE`](https://github.com/serilog/serilog-sinks-console/blob/b3cb1f042f5636161b831fa8816d70d865a5eec4/LICENSE). Identical in [serilog-sinks-file@23c732a](https://github.com/serilog/serilog-sinks-file/blob/23c732a8658a0df2a5434fe69b0011800b14f0da/LICENSE), [serilog-sinks-textwriter@266723a](https://github.com/serilog/serilog-sinks-textwriter/blob/266723a89b73f5413aca6980c0fc01af8cd5c6c9/LICENSE) and [serilog-sinks-trace@d7bcb06](https://github.com/serilog/serilog-sinks-trace/blob/d7bcb061318851f4f2cc1ade9dd2173b2326012a/LICENSE) |
| [SharpPdb-SharpUtilities.txt](SharpPdb-SharpUtilities.txt) | SharpPdb 1.0.4, SharpPdb.Windows 1.0.4, SharpUtilities 1.4.6 | MIT | [southpolenator/SharpPdb@b20fd50 `LICENSE`](https://github.com/southpolenator/SharpPdb/blob/b20fd504a45ab483f0131b17027b2de2d4198e5a/LICENSE). Identical in [southpolenator/SharpUtils@d048141](https://github.com/southpolenator/SharpUtils/blob/d048141bc1b1c7f87c5ba78a9076301e1c92fe5d/LICENSE). These are the last commits that changed the files; the repositories have no release tags |
| [Spectre.Console.txt](Spectre.Console.txt) | Spectre.Console 0.57.2, Spectre.Console.Ansi 0.57.2, Spectre.Console.Cli 0.55.0 | MIT | [spectreconsole/spectre.console@bbbb572 `LICENSE.md`](https://github.com/spectreconsole/spectre.console/blob/bbbb5729dde27b58deee44f447a788eea46ee451/LICENSE.md). Identical in [spectreconsole/spectre.console.cli@6f30fca](https://github.com/spectreconsole/spectre.console.cli/blob/6f30fcabec457a6e5c10870cefb62fe8259ecb1c/LICENSE.md) |
| [stb.txt](stb.txt) | StbImageSharp 2.30.16 and StbImageWriteSharp 1.16.7, the C# ports of stb_image and stb_image_write | MIT OR Unlicense | [nothings/stb@49cbedf `LICENSE`](https://github.com/nothings/stb/blob/49cbedfab6623f38a8fa65b32d10ab4b2fefd872/LICENSE). The StbSharp repositories have no licence file; the same text ends the stb headers they were generated from |
| [Steamworks.NET.txt](Steamworks.NET.txt) | Steamworks.NET 2025.164.1 | MIT | [rlabrecque/Steamworks.NET tag 2025.164.1 `LICENSE.txt`](https://github.com/rlabrecque/Steamworks.NET/blob/2025.164.1/LICENSE.txt) |
| [RectpackSharp.txt](RectpackSharp.txt) | RectpackSharp source compiled into DCCM's `GameRes.Core.dll` | MIT | [dead-cells-core-modding/core@d46d0e1 `3rd/RectpackSharp/LICENSE`](https://github.com/dead-cells-core-modding/core/blob/d46d0e158549c3c506d638cea3e3996f8eac7508/3rd/RectpackSharp/LICENSE) |
| [crashlink.txt](crashlink.txt) | crashlink 0.0.8 (Python tree `core/crashlink`) | MIT | [dead-cells-core-modding/crashlink@a677481 `LICENSE`](https://github.com/dead-cells-core-modding/crashlink/blob/a677481919905c81b9cebfb3f0df46e4380af189/LICENSE) |
| [CsWin32.txt](CsWin32.txt) | Microsoft.Windows.CsWin32 0.3.321 (generated interop code in DCCM assemblies) | MIT | [microsoft/CsWin32@b0f1e79 `LICENSE`](https://github.com/microsoft/CsWin32/blob/b0f1e799e6aa793eccffafa6ba8164ca45a9266f/LICENSE) |
| [msbuild.txt](msbuild.txt) | MSBuild 18.8.2 XML documentation files (Microsoft.Build.Framework, Microsoft.Build.Utilities.Core, Microsoft.NET.StringTools) | MIT | [dotnet/msbuild@ce25c01 `LICENSE`](https://github.com/dotnet/msbuild/blob/ce25c01082c9c46cd02ad1ff3ff8f16fe5cc2f44/LICENSE) |

## .NET

| File | Component(s) | Licence (SPDX) | Fetched from |
|---|---|---|---|
| [dotnet.txt](dotnet.txt) | .NET runtime 10.0.12 (single-file host and framework assemblies in `deadcells.exe`, NativeAOT runtime in `DeadCellsModding.exe`, apphost `DCCMTool.exe`); `nethost.dll` 9.0.6; System.IO.Hashing 10.0.11; the System.Runtime.CompilerServices.Unsafe 6.1.2 XML documentation file | MIT | [dotnet/dotnet@95017c7 `src/runtime/LICENSE.TXT`](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/runtime/LICENSE.TXT). Identical at [dotnet/dotnet@e2f47b0](https://github.com/dotnet/dotnet/blob/e2f47b0110ed922f21a1522da67279133ce28f32/src/runtime/LICENSE.TXT) (System.IO.Hashing 10.0.11), [dotnet/runtime v9.0.6](https://github.com/dotnet/runtime/blob/v9.0.6/LICENSE.TXT) and [dotnet/maintenance-packages@f62ca00](https://github.com/dotnet/maintenance-packages/blob/f62ca0009b038cab4725a720f386623a969d73ad/LICENSE) |
| [dotnet-runtime-10.0.12-THIRD-PARTY-NOTICES.txt](dotnet-runtime-10.0.12-THIRD-PARTY-NOTICES.txt) | Third-party notices of .NET runtime 10.0.12 and System.IO.Hashing 10.0.11 | various (see file) | [dotnet/dotnet@95017c7 `src/runtime/THIRD-PARTY-NOTICES.TXT`](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/runtime/THIRD-PARTY-NOTICES.TXT). Identical at [dotnet/runtime v10.0.12](https://github.com/dotnet/runtime/blob/v10.0.12/THIRD-PARTY-NOTICES.TXT) and dotnet/dotnet@e2f47b0 |
| [dotnet-runtime-9.0.6-THIRD-PARTY-NOTICES.txt](dotnet-runtime-9.0.6-THIRD-PARTY-NOTICES.txt) | Third-party notices of .NET 9.0.6 (`nethost.dll`) | various (see file) | [dotnet/runtime v9.0.6 `THIRD-PARTY-NOTICES.TXT`](https://github.com/dotnet/runtime/blob/v9.0.6/THIRD-PARTY-NOTICES.TXT) |

## HashLink and the libraries compiled into DCCM's native files

| File | Component(s) | Licence (SPDX) | Fetched from |
|---|---|---|---|
| [HashLink.txt](HashLink.txt) | HashLink at `d11bb13` (`libhl.dll`, `hl.exe`, `hljit.dll`, `heaps.hdll`, and HashLink's `hlc_main.c` in `modcorenative.dll`). It is also the licence the hdlls bindings derive from (`fmt`, `ssl`, `uv`, `sqlite`, `sdl`, `openal`, `ui` `.hdll`); hdlls itself has no licence file | MIT | [HaxeFoundation/hashlink@d11bb13 `LICENSE`](https://github.com/HaxeFoundation/hashlink/blob/d11bb13fd7140e688162e7dd8693c5b25c011f94/LICENSE) |
| [PCRE2.txt](PCRE2.txt) | PCRE2 10.40 (in `libhl.dll`) | BSD-3-Clause WITH PCRE2-exception | [PCRE2Project/pcre2 tag pcre2-10.40 `LICENCE`](https://github.com/PCRE2Project/pcre2/blob/pcre2-10.40/LICENCE). Identical to [hashlink@d11bb13 `include/pcre/LICENCE`](https://github.com/HaxeFoundation/hashlink/blob/d11bb13fd7140e688162e7dd8693c5b25c011f94/include/pcre/LICENCE) |
| [meshoptimizer.txt](meshoptimizer.txt) | meshoptimizer 1.2 (in `heaps.hdll`) | MIT | [zeux/meshoptimizer tag v1.2 `LICENSE.md`](https://github.com/zeux/meshoptimizer/blob/v1.2/LICENSE.md) |
| [V-HACD.txt](V-HACD.txt) | V-HACD 4.1 (in `heaps.hdll`) | BSD-3-Clause | [hashlink@d11bb13 `include/vhacd/VHACD.h`](https://github.com/HaxeFoundation/hashlink/blob/d11bb13fd7140e688162e7dd8693c5b25c011f94/include/vhacd/VHACD.h), lines 1-13 |
| [MikkTSpace.txt](MikkTSpace.txt) | MikkTSpace (in `heaps.hdll` and `fmt.hdll`) | Zlib | [hashlink@d11bb13 `include/mikktspace/mikktspace.h`](https://github.com/HaxeFoundation/hashlink/blob/d11bb13fd7140e688162e7dd8693c5b25c011f94/include/mikktspace/mikktspace.h), lines 4-22. `fmt.hdll` is built from the identical copy in [hdlls@e8be52b `include/mikktspace`](https://github.com/dead-cells-core-modding/hdlls/tree/e8be52b44290e93452f979796a23918086895f63/include/mikktspace) (same git blob `52c44a7` for `mikktspace.h`) |
| [renderdoc_app.txt](renderdoc_app.txt) | RenderDoc in-application API header `renderdoc_app.h` (in `heaps.hdll`) | MIT | [hashlink@d11bb13 `include/renderdoc/renderdoc_app.h`](https://github.com/HaxeFoundation/hashlink/blob/d11bb13fd7140e688162e7dd8693c5b25c011f94/include/renderdoc/renderdoc_app.h), lines 1-23 |
| [hdlls-glext.h-Khronos.txt](hdlls-glext.h-Khronos.txt) | Khronos OpenGL extension header `glext.h` (in `sdl.hdll`) | MIT-style Khronos licence (no exact SPDX match) | [hdlls@e8be52b `include/gl/glext.h`](https://github.com/dead-cells-core-modding/hdlls/blob/e8be52b44290e93452f979796a23918086895f63/include/gl/glext.h), lines 8-29 |
| [libpng.txt](libpng.txt) | libpng 1.6.26 (in `fmt.hdll`) | Libpng | [pnggroup/libpng tag v1.6.26 `LICENSE`](https://github.com/pnggroup/libpng/blob/v1.6.26/LICENSE) |
| [zlib-1.2.8-README.txt](zlib-1.2.8-README.txt) | zlib 1.2.8 (in `fmt.hdll`). The licence is the README's "Copyright notice" section; zlib 1.2.8 has no separate licence file | Zlib | [madler/zlib tag v1.2.8 `README`](https://github.com/madler/zlib/blob/v1.2.8/README) |
| [libjpeg-turbo-LICENSE.txt](libjpeg-turbo-LICENSE.txt) | libjpeg-turbo 1.4.2 (in `fmt.hdll`): overview of its three licences | IJG AND BSD-3-Clause AND Zlib | [libjpeg-turbo tag 1.4.2 `LICENSE.txt`](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/1.4.2/LICENSE.txt) |
| [libjpeg-turbo-README-IJG.txt](libjpeg-turbo-README-IJG.txt) | libjpeg-turbo 1.4.2: the IJG licence ("LEGAL ISSUES" section of the IJG README) | IJG | [libjpeg-turbo tag 1.4.2 `README`](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/1.4.2/README) |
| [libjpeg-turbo-turbojpeg-BSD-3-Clause.txt](libjpeg-turbo-turbojpeg-BSD-3-Clause.txt) | libjpeg-turbo 1.4.2: the TurboJPEG API (Modified BSD licence) | BSD-3-Clause | [libjpeg-turbo tag 1.4.2 `turbojpeg.c`](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/1.4.2/turbojpeg.c), lines 1-27 |
| [libjpeg-turbo-simd-Zlib.txt](libjpeg-turbo-simd-Zlib.txt) | libjpeg-turbo 1.4.2: the SIMD extensions | Zlib | [libjpeg-turbo tag 1.4.2 `simd/jsimdext.inc`](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/1.4.2/simd/jsimdext.inc), lines 1-27 |
| [libvorbis.txt](libvorbis.txt) | libvorbis 1.3.5 (in `fmt.hdll`) | BSD-3-Clause | [xiph/vorbis tag v1.3.5 `COPYING`](https://github.com/xiph/vorbis/blob/v1.3.5/COPYING) |
| [libogg.txt](libogg.txt) | libogg 1.3.2 (in `fmt.hdll`) | BSD-3-Clause | [xiph/ogg tag v1.3.2 `COPYING`](https://github.com/xiph/ogg/blob/v1.3.2/COPYING) |
| [minimp3-CC0-1.0.txt](minimp3-CC0-1.0.txt) | minimp3 (in `fmt.hdll`) | CC0-1.0 | [dead-cells-core-modding/hdlls@e8be52b `include/minimp3/LICENSE`](https://github.com/dead-cells-core-modding/hdlls/blob/e8be52b44290e93452f979796a23918086895f63/include/minimp3/LICENSE) |
| [mbedtls-2.7.0-LICENSE.txt](mbedtls-2.7.0-LICENSE.txt) | Mbed TLS 2.7.0 (in `ssl.hdll`): licence statement | Apache-2.0 | [hdlls@e8be52b `include/mbedtls/LICENSE`](https://github.com/dead-cells-core-modding/hdlls/blob/e8be52b44290e93452f979796a23918086895f63/include/mbedtls/LICENSE) |
| [mbedtls-2.7.0-Apache-2.0.txt](mbedtls-2.7.0-Apache-2.0.txt) | Mbed TLS 2.7.0: the Apache 2.0 text it refers to | Apache-2.0 | [hdlls@e8be52b `include/mbedtls/apache-2.0.txt`](https://github.com/dead-cells-core-modding/hdlls/blob/e8be52b44290e93452f979796a23918086895f63/include/mbedtls/apache-2.0.txt) |
| [libuv.txt](libuv.txt) | libuv 1.9.1 (in `uv.hdll`) | MIT AND ISC AND BSD-2-Clause (see file) | [libuv/libuv tag v1.9.1 `LICENSE`](https://github.com/libuv/libuv/blob/v1.9.1/LICENSE). Identical to [hdlls@e8be52b `include/libuv/LICENSE`](https://github.com/dead-cells-core-modding/hdlls/blob/e8be52b44290e93452f979796a23918086895f63/include/libuv/LICENSE) |
| [hlsteam.txt](hlsteam.txt) | hlsteam (`steam.hdll`) | MIT | [hdlls@e8be52b `libs/hlsteam/LICENSE`](https://github.com/dead-cells-core-modding/hdlls/blob/e8be52b44290e93452f979796a23918086895f63/libs/hlsteam/LICENSE) |
| [discord-rpc.txt](discord-rpc.txt) | Discord RPC (prebuilt library linked into `discord.hdll`) | MIT | [discord/discord-rpc `LICENSE`](https://github.com/discord/discord-rpc/blob/master/LICENSE), default branch (`master`), because the version of the prebuilt library is unknown |
| [rapidjson.txt](rapidjson.txt) | RapidJSON 1.1.0 (used by Discord RPC's build; inclusion in `discord.hdll` not verified) | MIT (see file) | [Tencent/rapidjson tag v1.1.0 `license.txt`](https://github.com/Tencent/rapidjson/blob/v1.1.0/license.txt) |

## SDL3.dll

| File | Component(s) | Licence (SPDX) | Fetched from |
|---|---|---|---|
| [SDL3.txt](SDL3.txt) | SDL 3.4.14 | Zlib | [libsdl-org/SDL tag release-3.4.14 `LICENSE.txt`](https://github.com/libsdl-org/SDL/blob/release-3.4.14/LICENSE.txt) |
| [SDL3-hidapi-LICENSE.txt](SDL3-hidapi-LICENSE.txt) | HIDAPI in SDL 3.4.14: the choice of three licences | GPL-3.0 OR BSD-3-Clause OR original HIDAPI licence | [SDL release-3.4.14 `src/hidapi/LICENSE.txt`](https://github.com/libsdl-org/SDL/blob/release-3.4.14/src/hidapi/LICENSE.txt) |
| [SDL3-hidapi-LICENSE-bsd.txt](SDL3-hidapi-LICENSE-bsd.txt) | HIDAPI: BSD option | BSD-3-Clause | [SDL release-3.4.14 `src/hidapi/LICENSE-bsd.txt`](https://github.com/libsdl-org/SDL/blob/release-3.4.14/src/hidapi/LICENSE-bsd.txt) |
| [SDL3-hidapi-LICENSE-orig.txt](SDL3-hidapi-LICENSE-orig.txt) | HIDAPI: original licence option | LicenseRef-HIDAPI-orig | [SDL release-3.4.14 `src/hidapi/LICENSE-orig.txt`](https://github.com/libsdl-org/SDL/blob/release-3.4.14/src/hidapi/LICENSE-orig.txt) |
| [SDL3-yuv2rgb.txt](SDL3-yuv2rgb.txt) | yuv2rgb in SDL 3.4.14 | BSD-3-Clause | [SDL release-3.4.14 `src/video/yuv2rgb/LICENSE`](https://github.com/libsdl-org/SDL/blob/release-3.4.14/src/video/yuv2rgb/LICENSE) |
| [SDL3-libm-Sun.txt](SDL3-libm-Sun.txt) | `src/libm/s_modf.c` in SDL 3.4.14, from Sun's math library, compiled by `VisualC/SDL/SDL.vcxproj` | SunPro | [SDL release-3.4.14 `src/libm/s_modf.c`](https://github.com/libsdl-org/SDL/blob/release-3.4.14/src/libm/s_modf.c), lines 2-11 |

## OpenAL32.dll (OpenAL Soft) and the code built into it

| File | Component(s) | Licence (SPDX) | Fetched from |
|---|---|---|---|
| [openal-soft-LGPL-2.0.txt](openal-soft-LGPL-2.0.txt) | OpenAL Soft 1.25.2 | LGPL-2.0-or-later | [kcat/openal-soft tag 1.25.2 `COPYING`](https://github.com/kcat/openal-soft/blob/1.25.2/COPYING) |
| [openal-soft-BSD-3-Clause.txt](openal-soft-BSD-3-Clause.txt) | The BSD-3-Clause parts of OpenAL Soft 1.25.2 | BSD-3-Clause | [openal-soft tag 1.25.2 `BSD-3Clause`](https://github.com/kcat/openal-soft/blob/1.25.2/BSD-3Clause) |
| [openal-soft-pffft.txt](openal-soft-pffft.txt) | PFFFT (modified copy in OpenAL Soft 1.25.2) | LicenseRef-pffft (BSD-like, see file) | [openal-soft tag 1.25.2 `LICENSE-pffft`](https://github.com/kcat/openal-soft/blob/1.25.2/LICENSE-pffft) |
| [fmt.txt](fmt.txt) | {fmt} 11.2.0 (bundled in OpenAL Soft 1.25.2) | MIT | [openal-soft tag 1.25.2 `fmt-11.2.0/LICENSE`](https://github.com/kcat/openal-soft/blob/1.25.2/fmt-11.2.0/LICENSE) |
| [ms-gsl.txt](ms-gsl.txt) | Microsoft GSL (bundled in OpenAL Soft 1.25.2) | MIT | [openal-soft tag 1.25.2 `gsl/LICENSE`](https://github.com/kcat/openal-soft/blob/1.25.2/gsl/LICENSE) |
| [ms-gsl-ThirdPartyNotices.txt](ms-gsl-ThirdPartyNotices.txt) | GSL's third-party notices | see file | [openal-soft tag 1.25.2 `gsl/ThirdPartyNotices.txt`](https://github.com/kcat/openal-soft/blob/1.25.2/gsl/ThirdPartyNotices.txt) |
| [mingw-w64-runtime.txt](mingw-w64-runtime.txt) | MinGW-w64 runtime (mingw-w64-crt code statically linked) | various permissive notices (see file) | [mingw-w64/mingw-w64 `COPYING.MinGW-w64-runtime/COPYING.MinGW-w64-runtime.txt`](https://github.com/mingw-w64/mingw-w64/blob/master/COPYING.MinGW-w64-runtime/COPYING.MinGW-w64-runtime.txt), default branch (`master`, at commit `a215715` when fetched; the file was last changed in `96f1cb7`), because the exact toolchain version is unknown |
| [mingw-w64-winpthreads.txt](mingw-w64-winpthreads.txt) | MinGW-w64 winpthreads (statically linked) | MIT AND BSD-3-Clause (see file) | [mingw-w64/mingw-w64 `mingw-w64-libraries/winpthreads/COPYING`](https://github.com/mingw-w64/mingw-w64/blob/master/mingw-w64-libraries/winpthreads/COPYING), default branch (`master`), because the exact toolchain version is unknown |
| [GCC-runtime-library-exception-3.1.txt](GCC-runtime-library-exception-3.1.txt) | GCC Runtime Library Exception 3.1: the GCC 15 runtime (libstdc++, libgcc) statically linked into `OpenAL32.dll`, and `lib/atomic.S` in TinyCC's `libtcc1.a` | GPL-3.0-or-later WITH GCC-exception-3.1 | [gcc-mirror/gcc tag releases/gcc-15.1.0 `COPYING.RUNTIME`](https://github.com/gcc-mirror/gcc/blob/releases/gcc-15.1.0/COPYING.RUNTIME) |

## TinyCC and cv2pdb

| File | Component(s) | Licence (SPDX) | Fetched from |
|---|---|---|---|
| [tinycc-LGPL-2.1.txt](tinycc-LGPL-2.1.txt) | TinyCC (`libtcc.dll`, `tinycc/libtcc1.a`). It is also the LGPL-2.1 text for the LGPL-2.1 headers in cv2pdb (`mscvpdb.h`: 2.1 or later; `dwarf.h`: version 2.1) | LGPL-2.1-or-later (TinyCC's file headers say "version 2 of the License, or (at your option) any later version") | [dead-cells-core-modding/tinycc@7b443d3 `COPYING`](https://github.com/dead-cells-core-modding/tinycc/blob/7b443d38bcad1cfa86a487c9bbff2f78ed90f40b/COPYING) |
| [tinycc-RELICENSING.txt](tinycc-RELICENSING.txt) | TinyCC's record of authors who also allow use under other terms; included for information | see file | [tinycc@7b443d3 `RELICENSING`](https://github.com/dead-cells-core-modding/tinycc/blob/7b443d38bcad1cfa86a487c9bbff2f78ed90f40b/RELICENSING) |
| [tinycc-libtcc1.c-header.txt](tinycc-libtcc1.c-header.txt) | `lib/libtcc1.c` in `libtcc1.a` | GPL-2.0-or-later, with a linking exception (see file) | [tinycc@7b443d3 `lib/libtcc1.c`](https://github.com/dead-cells-core-modding/tinycc/blob/7b443d38bcad1cfa86a487c9bbff2f78ed90f40b/lib/libtcc1.c), lines 1-29 |
| [tinycc-atomic.S-header.txt](tinycc-atomic.S-header.txt) | `lib/atomic.S` in `libtcc1.a` (code extracted from GCC 15.1.0 libatomic) | GPL-3.0-or-later WITH GCC-exception-3.1 | [tinycc@7b443d3 `lib/atomic.S`](https://github.com/dead-cells-core-modding/tinycc/blob/7b443d38bcad1cfa86a487c9bbff2f78ed90f40b/lib/atomic.S), lines 1-19 |
| [cv2pdb-Artistic-2.0.txt](cv2pdb-Artistic-2.0.txt) | cv2pdb 0.54 (`cv2pdb.exe`) | Artistic-2.0 | [dead-cells-core-modding/cv2pdb@e74fefa `LICENSE`](https://github.com/dead-cells-core-modding/cv2pdb/blob/e74fefa87452d58e0b4a941ad788bd281cf6168a/LICENSE) |
| [cv2pdb-Setup.Configuration.h-MIT.txt](cv2pdb-Setup.Configuration.h-MIT.txt) | Microsoft `Setup.Configuration.h` (Microsoft.VisualStudio.Setup.Configuration.Native 1.16.30), compiled into `cv2pdb.exe` | MIT | [cv2pdb@e74fefa `src/packages/Microsoft.VisualStudio.Setup.Configuration.Native.1.16.30/lib/native/include/Setup.Configuration.h`](https://github.com/dead-cells-core-modding/cv2pdb/blob/e74fefa87452d58e0b4a941ad788bd281cf6168a/src/packages/Microsoft.VisualStudio.Setup.Configuration.Native.1.16.30/lib/native/include/Setup.Configuration.h), lines 1-21 |

## Goldberg Steam Emulator (gbe_fork) and the libraries linked into it

| File | Component(s) | Licence (SPDX) | Fetched from |
|---|---|---|---|
| [Goldberg-LGPL-3.0.txt](Goldberg-LGPL-3.0.txt) | Goldberg Steam Emulator, gbe_fork continuation (`goldberg/steam_api64.dll`) | LGPL-3.0-or-later | [dead-cells-core-modding/core@d46d0e1 `3rd/Goldberg/LICENSE`](https://github.com/dead-cells-core-modding/core/blob/d46d0e158549c3c506d638cea3e3996f8eac7508/3rd/Goldberg/LICENSE). gbe_fork's own [`LICENSE` at release-2026_05_30](https://github.com/Detanup01/gbe_fork/blob/release-2026_05_30/LICENSE) is the same text plus one trailing byte |
| [gbe_fork-CREDITS.txt](gbe_fork-CREDITS.txt) | gbe_fork's list of the sources it uses, with their licence texts. It includes the header-only libraries compiled into the DLL (nlohmann/json, stb, utfcpp, simpleini, sha1, fifo_map). It also lists build tools and parts that the DLL does not contain. Its zlib section contains libssq's licence text by mistake; see `zlib-1.3.2.txt` | various (see file) | [Detanup01/gbe_fork tag release-2026_05_30 `CREDITS.md`](https://github.com/Detanup01/gbe_fork/blob/release-2026_05_30/CREDITS.md), renamed to `.txt` |
| [curl.txt](curl.txt) | libcurl 8.20.0 | curl | [curl/curl tag curl-8_20_0 `COPYING`](https://github.com/curl/curl/blob/curl-8_20_0/COPYING) (commit `a05f349`). gbe_fork's record [`curl/SOURCE.txt` at 92a4a13](https://github.com/Detanup01/gbe_fork/blob/92a4a130262083c4e887155cbe6bcab99baf36ea/curl/SOURCE.txt) contains the same licence text with an older copyright year |
| [mbedtls-3.6.6.txt](mbedtls-3.6.6.txt) | Mbed TLS 3.6.6 | Apache-2.0 OR GPL-2.0-or-later | [gbe_fork@92a4a13 `mbedtls/SOURCE.txt`](https://github.com/Detanup01/gbe_fork/blob/92a4a130262083c4e887155cbe6bcab99baf36ea/mbedtls/SOURCE.txt) |
| [libssq.txt](libssq.txt) | libssq 3.0.1 | MIT | [BinaryAlien/libssq tag v3.0.1 `LICENSE`](https://github.com/BinaryAlien/libssq/blob/v3.0.1/LICENSE) (commit `e0dda96`). gbe_fork's record [`libssq/SOURCE.txt` at 92a4a13](https://github.com/Detanup01/gbe_fork/blob/92a4a130262083c4e887155cbe6bcab99baf36ea/libssq/SOURCE.txt) contains the same licence text with an older copyright year |
| [zlib-1.3.2.txt](zlib-1.3.2.txt) | zlib 1.3.2 | Zlib | [madler/zlib tag v1.3.2 `LICENSE`](https://github.com/madler/zlib/blob/v1.3.2/LICENSE) |
| [protobuf.txt](protobuf.txt) | Protocol Buffers 34.1 | BSD-3-Clause | [protocolbuffers/protobuf tag v34.1 `LICENSE`](https://github.com/protocolbuffers/protobuf/blob/v34.1/LICENSE) |
| [protobuf-utf8_range.txt](protobuf-utf8_range.txt) | utf8_range (part of Protocol Buffers 34.1) | MIT | [protobuf tag v34.1 `third_party/utf8_range/LICENSE`](https://github.com/protocolbuffers/protobuf/blob/v34.1/third_party/utf8_range/LICENSE) |
| [abseil-cpp-Apache-2.0.txt](abseil-cpp-Apache-2.0.txt) | Abseil 20250512.1 (used by Protocol Buffers 34.1) | Apache-2.0 | [abseil/abseil-cpp tag 20250512.1 `LICENSE`](https://github.com/abseil/abseil-cpp/blob/20250512.1/LICENSE) |
| [opus.txt](opus.txt) | Opus 1.6.1 | BSD-3-Clause | [xiph/opus tag v1.6.1 `COPYING`](https://github.com/xiph/opus/blob/v1.6.1/COPYING) |
| [portaudio.txt](portaudio.txt) | PortAudio V19.7.0-devel at `b0fe9de` | MIT (PortAudio licence, see file) | [PortAudio/portaudio@b0fe9de `LICENSE.txt`](https://github.com/PortAudio/portaudio/blob/b0fe9de7ec86ebe5a26086f1d662ab74d7ebfae4/LICENSE.txt) |

## GNU licence texts

| File | Used by | Licence | Fetched from |
|---|---|---|---|
| [GPL-2.0.txt](GPL-2.0.txt) | `lib/libtcc1.c` in TinyCC's `libtcc1.a` | GPL-2.0 | <https://www.gnu.org/licenses/gpl-2.0.txt> (current version) |
| [GPL-3.0.txt](GPL-3.0.txt) | The GPL that LGPL-3.0 (Goldberg) builds on; `lib/atomic.S` in `libtcc1.a`; the GCC runtime in `OpenAL32.dll`; one of HIDAPI's licence options | GPL-3.0 | <https://www.gnu.org/licenses/gpl-3.0.txt> (current version) |

The LGPL-2.0 (OpenAL Soft), LGPL-2.1 (TinyCC, cv2pdb) and LGPL-3.0 (Goldberg) texts are the files listed in the sections above.

## Components without a licence text here

- SQLite 3.15.1 (`sqlite.hdll`) is public domain. There is no licence text to include.
- Microsoft Visual C++ runtime files and Valve's `steam_api64.dll`: neither Microsoft nor Valve publishes a licence text to ship with these files. The terms that apply are cited in THIRD-PARTY-NOTICES.md, section 6.4.
- `chroma.hdll` (hlchroma and the Razer Chroma SDK headers) and the hldiscord glue code in `discord.hdll` state no licence (not verified).
