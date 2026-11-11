using System;
using ModCore.Mods;
using Serilog;

// Optional sidecar: the community DLL and its on-disk bytes stay unchanged.
public sealed class CoopNetworkFixes : ModBase
{
    public CoopNetworkFixes(ModInfo info) : base(info) { }

    public override void Initialize()
    {
        try
        {
            MobMoveQueuePatch.Install();
            Log.Information("[CoopNetworkFixes] Ready: pending movement ordering; disabled={Disabled}", MobMoveQueuePatch.Disabled);
        }
        catch (Exception ex)
        {
            Log.Error("[CoopNetworkFixes] Initialization failed: {Error}", ex.Message);
            throw;
        }
    }
}
