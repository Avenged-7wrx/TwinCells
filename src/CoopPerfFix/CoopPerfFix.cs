using System;
using ModCore.Mods;
using Serilog;

public sealed class CoopPerfFix : ModBase
{
    public CoopPerfFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            PerfPatches.Install();
            Log.Information("[CoopPerfFix] Boss query memo ready; disabled={Disabled}",PerfPatches.Disabled);
        }
        catch(Exception error)
        {
            Log.Error("[CoopPerfFix] Initialization failed: {Error}",error.Message);
            throw;
        }
    }
}
