using System;
using ModCore.Mods;
using ModCore.Events.Interfaces.Game;
using Serilog;

// Alpha2 unit U1 (PLAN-ALPHA2.md issue 10): the client's own Behemoth (Concierge) death scene no longer locks the client.
// Standalone DCCM mod for development next to the Alpha1 pack; the pack row (integration U15) forwards FrameUpdate and its
// switch is COOP_PACK_BOSSOUTRO_DISABLE. Part switch: COOP_BOSS_OUTRO_DISABLE=1 (nothing is patched or hooked).
public sealed class CoopBossOutroFix : ModBase, IOnFrameUpdate
{
    public CoopBossOutroFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            BossOutroPatch.Install();
            Log.Information("[CoopBossOutroFix] Boss outro guard ready; disabled={Disabled} proxy={Proxy}",BossOutroPatch.Disabled,BossOutroPatch.ProxyCheck??"-");
        }
        catch(Exception error)
        {
            Log.Error("[CoopBossOutroFix] Initialization failed: {Error}",error.GetBaseException().Message);
            throw;
        }
    }
    public void OnFrameUpdate(double dt)=>BossOutroPatch.Tick();
}
