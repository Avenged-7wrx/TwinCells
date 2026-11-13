using System;
using ModCore.Mods;
using Serilog;

// CoopNameplateFix (Alpha2 unit U4, PLAN-ALPHA2.md issue 8; owner decision 6): the nickname labels the base mod hangs
// over the players (the partner's avatar and the local hero, GhostHero._labels) at about half their size, alpha 0.65,
// centred, colour kept. Presentation only, local to each peer: no wire feature, no capability token.
// Kill switch COOP_NAMEPLATE_DISABLE=1: nothing is patched (the pack row switch COOP_PACK_NAMEPLATE_DISABLE is added by
// integration with the parts.json row).
public sealed class CoopNameplateFix:ModBase
{
    public CoopNameplateFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        if(NameplateRules.Disabled(Environment.GetEnvironmentVariable(NameplateRules.DisableSwitch)))
        {
            Log.Information("[CoopNameplateFix] Nameplate fix not applied: {Switch}=1; nickname labels stay as the base mod draws them",NameplateRules.DisableSwitch);
            return;
        }
        try
        {
            var line=NameplatePatch.Install();
            Log.Information("[CoopNameplateFix] Nameplate fix ready: {Line}",line);
        }
        catch(Exception error)
        {
            Log.Error("[CoopNameplateFix] Initialization failed: {Error}",error.GetBaseException().Message);
            throw;
        }
    }
}
