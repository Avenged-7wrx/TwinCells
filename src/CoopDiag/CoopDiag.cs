using System;
using ModCore.Mods;
using R=CoopDiagRules;

// CoopDiag: Alpha2 diagnostics part (unit U13). Entry type of the standalone DCCM mod; see CoopDiagHooks for what it
// observes. Kill switch COOP_DIAG_DISABLE=1 (installs nothing); sub-switches COOP_DIAG_TIMING_DISABLE,
// COOP_DIAG_WATCHDOG_DISABLE, COOP_DIAG_BOSS_DISABLE; period COOP_DIAG_HEARTBEAT_SECONDS (1..60, default 5).
public sealed class CoopDiag : ModBase
{
    public CoopDiag(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            CoopDiagHooks.Install();
            if(CoopDiagHooks.Disabled){CoopDiagLog.Info(R.Prefix+"CoopDiag "+R.Version+" disabled by "+R.DisableSwitch+"=1; nothing installed");return;}
            CoopDiagLog.Info(R.Prefix+"CoopDiag "+R.Version+" ready: patches="+CoopDiagHooks.PatchedKeys.Count+" heartbeat="+CoopDiagHooks.Period+"s timing="+R.Bool(CoopDiagHooks.TimingOn)
                +" watchdog="+R.Bool(CoopDiagHooks.WatchdogOn)+" boss="+R.Bool(CoopDiagHooks.BossOn)+" proxy raw="+CoopDiagHooks.ProxyRaw+" symbolic="+CoopDiagHooks.ProxySymbolic);
        }
        catch(Exception error)
        {
            CoopDiagLog.Error(R.Prefix+"Initialization failed: "+error.Message);
            throw;
        }
    }
}
