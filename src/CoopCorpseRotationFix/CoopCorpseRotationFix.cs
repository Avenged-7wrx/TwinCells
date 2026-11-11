using System;
using ModCore.Mods;
using Serilog;

public sealed class CoopCorpseRotationFix : ModBase
{
    public CoopCorpseRotationFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            CorpseRotationPatch.Install();
            Log.Information("[CoopCorpseRotationFix] Local landed corpse rotation ready; disabled={Disabled}",CorpseRotationPatch.Disabled);
        }
        catch(Exception error)
        {
            Log.Error("[CoopCorpseRotationFix] Initialization failed: {Error}",error.Message);
            throw;
        }
    }
}
