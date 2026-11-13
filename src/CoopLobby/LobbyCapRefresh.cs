// Install hook of the REFRESH part (LobbyRefresh.cs; Alpha2 unit U12, PLAN-ALPHA2.md issue 17).
//
// CoopLobby.cs installs its parts from a fixed table (UI, CAP, COLOR), and U12 may only add files to this folder
// (PLAN-ALPHA2.md section 6). This explicit static constructor of the partial LobbyCap is the hook: CoopLobby.Initialize
// invokes LobbyCap.Install through reflection, and invoking a static method runs the type initializer first, so
// LobbyRefresh.Boot runs exactly once, on the loader thread, right before CAP installs (after the pins and after UI).
// LobbyCap had no static constructor (its static field initializers run first, unchanged). Nothing else touches LobbyCap
// before Install (no other file names it; LobbyUi / LobbyColor do not read it). Consequence: with
// COOP_LOBBY_CAP_DISABLE=1 (or COOP_LOBBY_DISABLE=1, or failed pins) REFRESH is not installed either. Boot never throws.
// The file is in the CAP group of Build.ps1 (LobbyCap*.cs), so a subset build without CAP leaves it out too.
// Integration may replace this hook by a ("LobbyRefresh","REFRESH") row in CoopLobby.cs's part table.
public static partial class LobbyCap
{
    static LobbyCap(){LobbyRefresh.Boot();}
}
