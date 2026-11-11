using System;
using ModCore.Mods;
using Serilog;

public sealed class CoopDownedAnchorFix : ModBase
{
    public CoopDownedAnchorFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            DownedAnchorPatch.Install();
            Log.Information("[CoopDownedAnchorFix] Supported downed anchor check ready; disabled={Disabled}",DownedAnchorPatch.Disabled);
        }
        catch(Exception error)
        {
            Log.Error("[CoopDownedAnchorFix] Initialization failed: {Error}",error.Message);
            throw;
        }
    }
}
