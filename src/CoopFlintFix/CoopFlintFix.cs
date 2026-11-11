using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using ModCore.Mods;
using Serilog;

// The pinned mod already has a visual-only path for Flint. Its alias list misses
// the actual v35 item ID. Reuse that path before any detached runtime is created;
// local vanilla preparation and authoritative MobSync damage are untouched.
public sealed class CoopFlintFix : ModBase
{
    const string Owner="CoopFlintFix.BehemothHammer.20261108";
    public const string BaseSha256="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    const BindingFlags Static=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    static readonly object Gate=new();
    static long Decisions;
    public static bool Ready {get;private set;}
    public static bool Disabled {get;private set;}
    public static string InitializationError {get;private set;}
    public static long GuardedDecisions=>Interlocked.Read(ref Decisions);
    public const string GuardReason="kind-behemoth-hammer-v35";
    public CoopFlintFix(ModInfo info):base(info){}
    public override void Initialize()
    {
        Install();
        Log.Information("[CoopFlintFix] Exact BehemothHammer visual replay guard ready; disabled={Disabled}",Disabled);
    }
    public static void Install()
    {
        lock(Gate)
        {
            if(Ready)return;
            if(InitializationError!=null)throw new InvalidOperationException(InitializationError);
            Harmony harmony=null;MethodInfo target=null;
            try
            {
                var assembly=typeof(NetNode).Assembly;
                if(assembly.GetName().Version!=new Version(0,5,1,0) || global::CoopPackRuntime.PackPins.Sha256(assembly.Location)!=BaseSha256)
                    throw new InvalidOperationException("CoopFlintFix requires the exact community 0.5.1 DLL");
                var type=assembly.GetType("DeadCellsMultiplayerMod.Ghost.KingWeaponSupport",true);
                target=type.GetMethods(Static).Single(m=>m.Name=="RequiresVisualOnlyRemoteReplay" && m.GetParameters().Length==3);
                var args=target.GetParameters();
                if(target.ReturnType!=typeof(bool) || args[0].ParameterType!=typeof(string) || args[1].ParameterType.FullName!="dc.tool.Weapon" ||
                    args[2].ParameterType!=typeof(string).MakeByRefType() || !args[2].IsOut)
                    throw new InvalidOperationException("Pinned remote weapon policy signature changed");
                Disabled=Environment.GetEnvironmentVariable("COOP_FLINT_GUARD_DISABLE")=="1";
                harmony=new Harmony(Owner);
                harmony.Patch(target,prefix:new HarmonyMethod(typeof(CoopFlintFix).GetMethod(nameof(RecognizeNativeKind),Static)));
                if(!Harmony.GetPatchInfo(target).Owners.Contains(Owner))throw new InvalidOperationException("Flint policy hook not registered");
                Ready=true;
            }
            catch(Exception error)
            {
                Ready=false;InitializationError=error.Message;
                if(harmony!=null && target!=null)try{harmony.Unpatch(target,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    static bool RecognizeNativeKind(string __0,ref string __2,ref bool __result)
    {
        if(!Ready || Disabled || !string.Equals(__0?.Trim(),"BehemothHammer",StringComparison.OrdinalIgnoreCase))return true;
        __2=GuardReason;__result=true;Interlocked.Increment(ref Decisions);return false;
    }
}
