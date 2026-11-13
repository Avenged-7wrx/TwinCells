using System;
using ModCore.Mods;
using Serilog;

// Entry type of part CoopReviveCostFix (Alpha2 unit U6). Standalone DCCM mod for development next to the Alpha1 pack;
// integration (U15) adds it as a pack row (switch COOP_PACK_REVIVE_COST_DISABLE). See CoopReviveCostPatch.cs.
public sealed class CoopReviveCostFix : ModBase
{
    public CoopReviveCostFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            CoopReviveCostPatch.Install();
            if(CoopReviveCostPatch.Disabled)
                Log.Information("[CoopReviveCostFix] Revive cost rules ready; disabled=True (COOP_REVIVE_COST_DISABLE=1: nothing verified or patched)");
            else
                Log.Information("[CoopReviveCostFix] Revive cost rules ready; disabled=False (1 flask charge per downed episode, hint with 0 charges, no charge without a deliverable request)");
        }
        catch(Exception error)
        {
            Log.Error("[CoopReviveCostFix] Initialization failed: {Error}",error.Message);
            throw;
        }
    }
}
