using System;
using ModCore.Mods;
using Serilog;

public sealed class CoopDeadCameraFix:ModBase
{
    public CoopDeadCameraFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        try { DeadCameraPatch.Install();Log.Information("[CoopDeadCameraFix] Current downed camera target ready; disabled={Disabled}",DeadCameraPatch.Disabled); }
        catch(Exception error){Log.Error("[CoopDeadCameraFix] Initialization failed: {Error}",error.Message);throw;}
    }
}
