using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using dc.en;
using dc.libs.heaps.slib;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using HaxeProxy.Runtime;
using File=System.IO.File;
using Path=System.IO.Path;

// Native HeroDeadCorpse.fixedUpdate #12947 clears sprite rotation at landing.
// The original mod's timeout instead plays lethalSlam and freezes the body,
// bypassing that native landing branch. Complete only the omitted visual step.
public static class CorpseRotationPatch
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string Owner="CoopCorpseRotationFix.LocalLandedBody.20261109";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static readonly object Gate=new();
    static FieldInfo LocalDead,LocalCine,CineHero,CineCorpse,Landed;
    static volatile bool Published;
    static long Checks,Changes,ReadFaults,WriteFaults;
    public static bool Ready=>Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static long LandedChecks=>Interlocked.Read(ref Checks);
    public static long Corrections=>Interlocked.Read(ref Changes);
    public static long InspectionErrors=>Interlocked.Read(ref ReadFaults);
    public static long RotationErrors=>Interlocked.Read(ref WriteFaults);
    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static IntPtr Pointer(HaxeProxyBase value)=>value?.HashlinkPointer??IntPtr.Zero;
    static FieldInfo Field(Type type,string name,Type fieldType)
    {
        var field=type.GetField(name,I|BindingFlags.DeclaredOnly);
        if(field==null||field.FieldType!=fieldType)throw new InvalidOperationException("Pinned field mismatch: "+type.FullName+"."+name);
        return field;
    }
    public static void Install()
    {
        lock(Gate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier corpse rotation initialization failed: "+InitializationError);
            Harmony harmony=null;MethodInfo target=null;
            try
            {
                var original=typeof(ModEntry).Assembly;
                if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||
                    Hash(Path.Combine(Path.GetDirectoryName(typeof(HeroDeadCorpse).Assembly.Location),"hlboot.dat"))!=NativePin)
                    throw new InvalidOperationException("CoopCorpseRotationFix requires exact community0.5.1/native35.13.5");
                CorpseRotationProxyCompatibility.ValidateLoaded(typeof(HeroDeadCorpse).Assembly);
                LocalDead=Field(typeof(ModEntry),"_localFakeDead",typeof(bool));
                LocalCine=Field(typeof(ModEntry),"_localDeadCine",typeof(DeadBase));
                CineHero=Field(typeof(DeadBase),"_hero",typeof(Hero));
                CineCorpse=Field(typeof(DeadBase),"_corpse",typeof(HeroDeadCorpse));
                Landed=Field(typeof(DeadBase),"_anchoredGroundedPoseApplied",typeof(bool));
                target=typeof(DeadBase).GetMethod("PinAnchoredGroundedCorpse",I|BindingFlags.DeclaredOnly,null,new[]{typeof(HeroDeadCorpse)},null);
                if(target?.ReturnType!=typeof(void)||target.IsStatic)throw new InvalidOperationException("Pinned corpse landing signature changed");
                Disabled=Environment.GetEnvironmentVariable("COOP_CORPSE_ROTATION_DISABLE")=="1";
                harmony=new Harmony(Owner);
                harmony.Patch(target,postfix:new HarmonyMethod(typeof(CorpseRotationPatch).GetMethod(nameof(AfterPin),S)));
                var installed=Harmony.GetPatchInfo(target);
                if(installed==null||installed.Postfixes.Count(p=>p.owner==Owner)!=1)
                    throw new InvalidOperationException("Corpse rotation postfix was not installed exactly once");
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;
                if(harmony!=null&&target!=null)try{harmony.Unpatch(target,HarmonyPatchType.All,Owner);}
                    catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    // Successful original landing/pinning completes first. No prefix or native
    // hook changes its result, exception, animation, position, gravity or loot.
    static void AfterPin(DeadBase __instance,HeroDeadCorpse corpse)
    {
        if(!Published||Disabled)return;
        HSprite sprite;double angle;
        try
        {
            sprite=Capture(__instance,corpse);
            if(sprite==null)return;
            Interlocked.Increment(ref Checks);
            angle=sprite.rotation;
            if(!double.IsFinite(angle)||angle==0.0)return;
        }
        catch(Exception error){if(Interlocked.Increment(ref ReadFaults)==1)Warn("inspection",error);return;}
        try
        {
            // Match native fixedUpdate #12947 ops283-286. GameProxy setters
            // write raw fields, so invalidate the transform explicitly first.
            sprite.posChanged=true;
            sprite.rotation=0.0;
            Interlocked.Increment(ref Changes);
        }
        catch(Exception error){if(Interlocked.Increment(ref WriteFaults)==1)Warn("rotation",error);}
    }
    static HSprite Capture(DeadBase cine,HeroDeadCorpse corpse)
    {
        var mod=ModEntry.Instance;var hero=ModEntry.me;var net=ModEntry._net;
        if(mod==null||hero==null||net==null||!net.IsAlive||cine==null||corpse==null||
            !(bool)LocalDead.GetValue(mod)||!ReferenceEquals(LocalCine.GetValue(mod),cine)||
            !ReferenceEquals(CineHero.GetValue(cine),hero)||!ReferenceEquals(CineCorpse.GetValue(cine),corpse)||
            !(bool)Landed.GetValue(cine))return null;
        if(Pointer(cine)==IntPtr.Zero||Pointer(hero)==IntPtr.Zero||Pointer(corpse)==IntPtr.Zero||
            cine.destroyed||hero.destroyed||corpse.destroyed||corpse.hasGravity)return null;
        var level=hero._level;
        if(level==null||level.destroyed||Pointer(level)==IntPtr.Zero||Pointer(corpse._level)!=Pointer(level))return null;
        var sprite=corpse.spr;
        return sprite!=null&&Pointer(sprite)!=IntPtr.Zero&&sprite.groupName?.ToString()=="lethalSlam"?sprite:null;
    }
    static void Warn(string phase,Exception error)
    {
        try{Serilog.Log.Warning("[CoopCorpseRotationFix] Preserved original landing; {Phase} failed: {Error}",phase,error.Message);}catch{}
    }
}
