using System;
using ModCore.Mods;
using ModCore.Events.Interfaces.Game;
using Serilog;

// Entry type of the CoopBossTargetFix part (PLAN-ALPHA2.md issue 9, unit U3). Standalone DCCM mod during Alpha2
// development; integration (U15) adds it as a pack row (events: FrameUpdate; pack switch COOP_PACK_BOSS_TARGET_DISABLE).
// Kill switch: COOP_BOSS_TARGET_DISABLE=1 (nothing is patched).
public sealed class CoopBossTargetFix : ModBase, IOnFrameUpdate
{
    public CoopBossTargetFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            CoopBossTargetPatch.Install();
            if(CoopBossTargetPatch.Disabled)
            {
                Log.Information("[CoopBossTargetFix] Kill switch {Switch}=1 is set: boss nemesis hand-over not installed",CoopBossTargetRules.KillSwitch);
                return;
            }
            Log.Information("[CoopBossTargetFix] Boss nemesis hand-over ready: allowlist={Allow} proxyPin={Proxy} patchedMethods={Count} harmony={Id}",
                string.Join(",",CoopBossTargetRules.AllowedBossTypes),CoopBossTargetPatch.ProxyPinMode,CoopBossTargetPatch.PatchedTargets,CoopBossTargetRules.HarmonyId);
        }
        catch(Exception error)
        {
            Log.Error("[CoopBossTargetFix] Initialization failed: {Error}",error.GetBaseException().Message);
            throw;
        }
    }
    public void OnFrameUpdate(double dt)
    {
        try{CoopBossTargetPatch.Tick();}catch{}
    }
}
