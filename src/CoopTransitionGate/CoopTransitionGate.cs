using System;
using ModCore.Mods;
using Serilog;

// Alpha2 part CoopTransitionGate (unit U10): the doors behind Guillain and the Collector wait for the partner, with no time
// limit, and open when the partner is done there, downed, disconnected or has left the level. See CoopTransitionGatePatch.cs.
public sealed class CoopTransitionGate : ModBase
{
    public CoopTransitionGate(ModInfo info):base(info){}
    public override void Initialize()
    {
        try
        {
            CoopTransitionGatePatch.Install();
            Log.Information("[CoopTransitionGate] Transition gate ready; disabled={Disabled} wire={Tag} symbolic={Symbolic}",
                CoopTransitionGatePatch.Disabled,CoopTransitionGateRules.Tag,CoopTransitionGatePins.SymbolicPin.Substring(0,16));
        }
        catch(Exception error)
        {
            Log.Error("[CoopTransitionGate] Initialization failed: {Error}",error.Message);
            throw;
        }
    }
}
