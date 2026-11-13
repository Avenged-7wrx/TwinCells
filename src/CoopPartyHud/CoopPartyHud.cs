using System;
using ModCore.Mods;
using Serilog;

// Alpha2 part CoopPartyHud (unit U16): the partner's party plate in the top-right HUD becomes one panel with two rows
// (full nickname in the partner's lobby colour + Brutality/Tactics/Survival levels; a wide HP bar with "current / max").
// See CoopPartyHudPatch.cs. Kill switch COOP_PARTY_HUD_DISABLE=1 (nothing patched: the base mod's plate).
public sealed class CoopPartyHud : ModBase
{
    public CoopPartyHud(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            CoopPartyHudPatch.Install();
            Log.Information("[CoopPartyHud] Party HUD ready; disabled={Disabled} wire={Tag} symbolic={Symbolic}",
                CoopPartyHudPatch.Disabled,CoopPartyHudRules.Tag,CoopPartyHudPins.SymbolicPin.Substring(0,16));
        }
        catch(Exception error)
        {
            Log.Error("[CoopPartyHud] Initialization failed: {Error}",error.GetBaseException().Message);
            throw;
        }
    }
}
