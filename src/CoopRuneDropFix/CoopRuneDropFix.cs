using System;
using ModCore.Mods;
using Serilog;

public sealed class CoopRuneDropFix : ModBase
{
    public CoopRuneDropFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try{RuneDropPatch.Install();Log.Information("[CoopRuneDropFix] Existing remote LadderKey cleanup ready; disabled={Disabled}",RuneDropPatch.Disabled);}
        catch(Exception error){Log.Error("[CoopRuneDropFix] Initialization failed: {Error}",error.Message);throw;}
    }
}
