using System;
using ModCore.Mods;
using Serilog;

public sealed class CoopInventoryFix:ModBase
{
    public CoopInventoryFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try{InventorySlotPatch.Install();Log.Information("[CoopInventoryFix] Pending inventory slots ready; disabled={Disabled}",InventorySlotPatch.Disabled);}
        catch(Exception error){Log.Error("[CoopInventoryFix] Initialization failed: {Error}",error.Message);throw;}
    }
}
