using System;
using ModCore.Mods;
using Serilog;

// Entry type of the Alpha2 part CoopDeathSoundFix (unit U7, PLAN-ALPHA2.md issue 1). Standalone DCCM mod for the unit's
// development and native tests next to the Alpha1 pack; integration (U15) adds it as a pack row (proposed switch
// COOP_PACK_DEATH_SOUND_DISABLE, no forwarded events). Everything happens in DeathSoundPatch.Install.
public sealed class CoopDeathSoundFix : ModBase
{
    public CoopDeathSoundFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            DeathSoundPatch.Install();
            if(DeathSoundPatch.Disabled)
                Log.Information("[CoopDeathSoundFix] Network death sound ready; disabled=True ({Switch}=1: nothing patched)",DeathSoundRules.KillSwitch);
            else
                Log.Information("[CoopDeathSoundFix] Network death sound ready; disabled=False snapshotFence={Snapshot} shrineReceipts={Shrine} proxy={Proxy}",
                    DeathSoundPatch.SnapshotFenceBound,DeathSoundPatch.ShrineReceiptsBound,DeathSoundPatch.ProxyFingerprint);
        }
        catch(Exception error)
        {
            Log.Error("[CoopDeathSoundFix] Initialization failed: {Error}",error.GetBaseException().Message);
            throw;
        }
    }
}
