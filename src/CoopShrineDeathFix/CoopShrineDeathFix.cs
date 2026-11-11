using System;
using ModCore.Mods;
using ModCore.Events.Interfaces.Game;
using Serilog;

public sealed class CoopShrineDeathFix : ModBase, IOnGameInit
{
    public CoopShrineDeathFix(ModInfo info) : base(info) { }
    public override void Initialize()
    {
        // DefaultModLoader is still iterating its Initialize callbacks here.
        // IOnGameInit follows that completed loop; readiness/hash checks remain
        // in the unchanged installer and still reject a failed dependency.
        Log.Information("[CoopShrineDeathFix] Deferred until official game initialization");
    }
    void IOnGameInit.OnGameInit()
    {
        try { ShrineLateDeathPatch.Install(); Log.Information("[CoopShrineDeathFix] Exact late shrine death ready; disabled={Disabled}",ShrineLateDeathPatch.Disabled); }
        catch(Exception error) { Log.Error("[CoopShrineDeathFix] Initialization failed: {Error}",error.Message); throw; }
    }
}
