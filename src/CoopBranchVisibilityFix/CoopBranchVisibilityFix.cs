using System;
using ModCore.Mods;
using Serilog;

// Alpha2 part CoopBranchVisibilityFix (unit U9): hides the partner while he is in another room/sub-level of the same map,
// keeps a minimap marker where he left, keeps a downed partner in another room hidden and not revivable from here, and
// shows his head flames again once his avatar is visible (issue 6 R3). See CoopBranchVisibilityPatch.cs.
public sealed class CoopBranchVisibilityFix : ModBase
{
    public CoopBranchVisibilityFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            CoopBranchVisibilityPatch.Install();
            Log.Information("[CoopBranchVisibilityFix] Branch visibility ready; disabled={Disabled} marker={Marker} head={Head} corpse={Corpse} symbolic={Symbolic}",
                CoopBranchVisibilityPatch.Disabled,!CoopBranchVisibilityPatch.MarkerDisabled,!CoopBranchVisibilityPatch.HeadDisabled,!CoopBranchVisibilityPatch.CorpseDisabled,
                CoopBranchVisibilityPins.SymbolicPin.Substring(0,16));
        }
        catch(Exception error)
        {
            Log.Error("[CoopBranchVisibilityFix] Initialization failed: {Error}",error.Message);
            throw;
        }
    }
}
