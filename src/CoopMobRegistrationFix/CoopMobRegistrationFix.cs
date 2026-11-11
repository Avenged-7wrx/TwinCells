using System;
using ModCore.Mods;
using Serilog;

public sealed class CoopMobRegistrationFix : ModBase
{
    public CoopMobRegistrationFix(ModInfo info) : base(info) { }
    public override void Initialize()
    {
        try { MobRegistrationPatch.Install(); Log.Information("[CoopMobRegistrationFix] Positive host identity commit ready; disabled={Disabled}", MobRegistrationPatch.Disabled); }
        catch (Exception e) { Log.Error("[CoopMobRegistrationFix] Initialization failed: {Error}", e.Message); throw; }
    }
}
