using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using ModCore.Mods;

// CoopPadFix (Alpha2 unit U5; PLAN-ALPHA2.md issue 7; owner decisions OD5 and Q6): the D-pad moves the hero (the game's
// own "D-Pad function" option set to Movement on new copies and switched once on existing ones) and a DualSense is
// auto-detected for PlayStation glyphs. The manual override is the game's own Settings > Video > Icons > "Icons used".
// Add-on only: no base-mod method is patched; three DCCM native hooks (PadFixHooks.cs). GameProxy members are pinned
// symbolically (PadProxyFingerprint.cs), the native game by its hlboot.dat.
// Switches: COOP_PAD_DISABLE=1 (nothing installed), COOP_PAD_DPAD_DISABLE=1 (no D-pad change),
// COOP_PAD_GLYPHS_DISABLE=1 (pad names unchanged); the pack row switch COOP_PACK_PAD_DISABLE comes with integration.
// Log prefix [CoopPadFix]; one "... ready" line at install, "Initialization failed: <reason>" (ERR) when it cannot install.
public sealed class CoopPadFix : ModBase
{
    const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    static readonly object InitGate=new object();
    static bool InstallAttempted;
    public CoopPadFix(ModInfo info):base(info){}

    public override void Initialize()
    {
        lock(InitGate)
        {
            if(PadFixStatus.Ready||PadFixStatus.Disabled)return;
            if(InstallAttempted)throw new InvalidOperationException("CoopPadFix initialization previously failed");
            InstallAttempted=true;
            if(PadFixRules.SwitchSet(Environment.GetEnvironmentVariable(PadFixRules.PartSwitch)))
            {PadFixStatus.Disabled=true;PadFixLog.Info("Disabled by "+PadFixRules.PartSwitch);return;}
            bool dpad=!PadFixRules.SwitchSet(Environment.GetEnvironmentVariable(PadFixRules.DpadSwitch));
            bool glyphs=!PadFixRules.SwitchSet(Environment.GetEnvironmentVariable(PadFixRules.GlyphSwitch));
            try
            {
                VerifyPinnedFiles();
                string probe=Environment.GetEnvironmentVariable("COOP_PROBE");
                string statePath=PadFixRules.StatePath(probe=="1"?null:DataRoot(Info),Environment.CurrentDirectory,probe,Environment.GetEnvironmentVariable(PadFixRules.QaStateVariable));
                PadFixStatus.StatePath=statePath;
                PadFixHooks.Install(statePath,dpad,glyphs);
                PadFixStatus.Ready=true;
                PadFixLog.Info("D-pad default and DualSense glyph detection ready: dpad="+(dpad?"on":"off by "+PadFixRules.DpadSwitch)
                    +" glyphs="+(glyphs?"on":"off by "+PadFixRules.GlyphSwitch)+" padLog=on proxy="+PadFixStatus.ProxyCheck+" state="+statePath);
            }
            catch(Exception ex)
            {
                PadFixStatus.InitError=ex.ToString();
                try{PadFixHooks.Uninstall();}catch{}
                PadFixLog.Error("Initialization failed: "+Innermost(ex));
                throw;
            }
        }
    }
    static void VerifyPinnedFiles()
    {
        var proxy=typeof(dc.Options).Assembly;
        if(Hash(Path.Combine(Path.GetDirectoryName(proxy.Location)??"","hlboot.dat"))!=NativePin)
            throw new InvalidOperationException("CoopPadFix requires the pinned 35.13.5 native game (hlboot.dat A37869A3)");
        PadProxyCompatibility.ValidateLoaded(proxy);
    }
    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    // ModInfo.ModDataRoot is a ModCore.Common FolderInfo; read by reflection so that this part needs no reference to
    // ModCore.Common (gate G6), as PackHost.PartInfo does.
    static string DataRoot(ModInfo info)
    {
        var folder=typeof(ModInfo).GetProperty("ModDataRoot",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(info);
        var path=folder?.GetType().GetProperty("FullPath",BindingFlags.Instance|BindingFlags.Public)?.GetValue(folder) as string;
        if(string.IsNullOrEmpty(path))throw new InvalidOperationException("ModInfo.ModDataRoot unavailable");
        return path;
    }
    static string Innermost(Exception e)
    {
        while((e is TargetInvocationException||e is TypeInitializationException)&&e.InnerException!=null)e=e.InnerException;
        return e.Message;
    }
}
