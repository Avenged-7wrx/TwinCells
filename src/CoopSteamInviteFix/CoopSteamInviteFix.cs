using ModCore.Mods;
using ModCore.Events.Interfaces.Game;
using DeadCellsMultiplayerMod;
using CoopSteamPresenceProposal;

public sealed class CoopSteamInviteFix : ModBase,IOnFrameUpdate
{
    public CoopSteamInviteFix(ModInfo info):base(info){}
    public override void Initialize()=>SteamInviteRuntime.InitializeParent(typeof(ModEntry).Assembly);
    public void OnFrameUpdate(double dt)=>SteamInviteRuntime.Tick();
}
