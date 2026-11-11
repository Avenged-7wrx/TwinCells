using System;
using ModCore.Mods;
using Serilog;

public sealed class CoopDialogPresenceFix : ModBase
{
    public CoopDialogPresenceFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            DialogPresencePatch.Install();
            Log.Information("[CoopDialogPresenceFix] Live partner during dialogs ready; disabled={Disabled}",DialogPresencePatch.Disabled);
        }
        catch(Exception error)
        {
            Log.Error("[CoopDialogPresenceFix] Initialization failed: {Error}",error.Message);
            throw;
        }
    }
}
