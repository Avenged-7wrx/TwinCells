using System;
using ModCore.Mods;
using Serilog;

// DCCM entry of the CoopComboFix part (Alpha2 unit U8). Standalone mod for development; integration (U15) adds the pack
// row (switch COOP_PACK_COMBO_DISABLE). Own switches: COOP_COMBO_DISABLE=1 (nothing is patched), COOP_COMBO_PARTB_DISABLE=1
// (Part A only). Patches and rules: ComboFixPatch.cs, ComboFixWire.cs, ComboFixRules.cs.
public sealed class CoopComboFix : ModBase
{
    public CoopComboFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        if(ComboFixRules.SwitchSet(Environment.GetEnvironmentVariable(ComboFixRules.KillSwitch)))
        {
            Log.Information("[CoopComboFix] Combo fix disabled by {Switch:l}=1; nothing patched",ComboFixRules.KillSwitch);
            return;
        }
        try
        {
            ComboFixPatch.Install();
            Log.Information("[CoopComboFix] Combo fix ready: {Describe:l}",ComboFixPatch.Describe());
        }
        catch(Exception error)
        {
            Log.Error("[CoopComboFix] Initialization failed: {Error}",error.GetBaseException().Message);
            throw;
        }
    }
}
