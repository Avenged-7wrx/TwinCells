using System;
using ModCore.Mods;
using Serilog;

public sealed class CoopShrineFix : ModBase
{
    public CoopShrineFix(ModInfo info) : base(info) { }
    public override void Initialize()
    {
        try { ShrineReplicaPatch.Install(); Log.Information("[CoopShrineFix] Exact native shrine pairing ready; disabled={Disabled}", ShrineReplicaPatch.Disabled); }
        catch (Exception e) { Log.Error("[CoopShrineFix] Initialization failed: {Error}", e.Message); throw; }
    }
}
