using ModCore.Mods;
using ModCore.Events.Interfaces.Game;

// DCCM entry of the CoopPack module (PACK-DESIGN.md section 2.1). Global namespace, priority 0.
// All work is in CoopPackRuntime.PackHost; none of these members throws into DCCM.
public sealed class CoopPack : ModBase, IOnFrameUpdate, IOnGameInit
{
    public CoopPack(ModInfo info) : base(info) {}
    public override void Initialize() { CoopPackRuntime.PackHost.Run(this); }
    public void OnFrameUpdate(double dt) { CoopPackRuntime.PackHost.Frame(dt); }
    void IOnGameInit.OnGameInit() { CoopPackRuntime.PackHost.GameInit(); }
}
