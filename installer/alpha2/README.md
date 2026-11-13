# Alpha2 updater source

Download the complete [Alpha2 update archive](https://github.com/Avenged-7wrx/TwinCells/releases/tag/v0.2.0-alpha) to install. This directory contains the updater source and pinned release metadata; its binary payload and `Source/` snapshot are supplied by the release archive.

The updater accepts the supported Alpha1 installation states, replaces `CoopPack.dll` and `modinfo.json`, updates `coop-install.json`, and retains the replaced files for rollback. It verifies the existing installation and preserves saves and configuration. `Check`, `Update` and `Undo` share the same script.

The public package disables development fault-injection hooks. The runtime DLL is the same `3D0A5880` build as the earlier Alpha2 tester update. Both packages recognize the same installed Alpha2 version.
