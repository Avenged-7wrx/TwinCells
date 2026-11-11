using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using dc;
using dc.en;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using HaxeProxy.Runtime;
using File=System.IO.File;
using Path=System.IO.Path;
using Type=System.Type;

// Keep the original native track call/try/catch. Change only its captured target
// when the current local fake-death cinematic has a valid selected remote peer.
public static class DeadCameraPatch
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string Owner="CoopDeadCameraFix.CurrentDownedTarget.20261109";
    const BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly object Gate=new();
    static FieldInfo LocalDead,LocalCine,CineHero;
    static Func<Entity,Entity> Resolve;
    static volatile bool Published;
    static long Selections,ReadFaults;
    public static bool Ready=>Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static long RemoteSelections=>Interlocked.Read(ref Selections);
    public static long InspectionErrors=>Interlocked.Read(ref ReadFaults);
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
            if(InitializationError!=null)throw new InvalidOperationException("Earlier camera initialization failed: "+InitializationError);
            Harmony harmony=null;MethodInfo target=null;
            try
            {
                var original=typeof(ModEntry).Assembly;
                if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||Hash(Path.Combine(Path.GetDirectoryName(typeof(Hero).Assembly.Location),"hlboot.dat"))!=NativePin)
                    throw new InvalidOperationException("CoopDeadCameraFix requires exact community0.5.1/native35.13.5");
                DeadCameraProxyCompatibility.ValidateLoaded(typeof(Hero).Assembly);
                LocalDead=Field(typeof(ModEntry),"_localFakeDead",typeof(bool));LocalCine=Field(typeof(ModEntry),"_localDeadCine",typeof(DeadBase));CineHero=Field(typeof(DeadBase),"_hero",typeof(Hero));
                var resolver=typeof(ModEntry).GetMethod("ResolvePreferredCameraTarget",S|BindingFlags.DeclaredOnly,null,new[]{typeof(Entity)},null);
                if(resolver?.ReturnType!=typeof(Entity)||!resolver.IsStatic)throw new InvalidOperationException("Pinned preferred camera resolver changed");
                Resolve=(Func<Entity,Entity>)resolver.CreateDelegate(typeof(Func<Entity,Entity>));
                target=typeof(DeadBase).GetMethod("EnsureViewportTracksHero",I|BindingFlags.DeclaredOnly,null,new[]{typeof(bool)},null);
                if(target?.ReturnType!=typeof(void)||target.IsStatic)throw new InvalidOperationException("Pinned death camera signature changed");
                Disabled=Environment.GetEnvironmentVariable("COOP_SYNC_DISABLE_DEAD_CAMERA")=="1";
                harmony=new Harmony(Owner);harmony.Patch(target,transpiler:new HarmonyMethod(typeof(DeadCameraPatch).GetMethod(nameof(ReplaceTarget),S)));
                var installed=Harmony.GetPatchInfo(target);
                if(installed==null||installed.Transpilers.Count(p=>p.owner==Owner)!=1)throw new InvalidOperationException("Camera transpiler not installed exactly once");
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;
                if(harmony!=null&&target!=null)try{harmony.Unpatch(target,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    static IEnumerable<CodeInstruction> ReplaceTarget(IEnumerable<CodeInstruction> input,ILGenerator generator)
    {
        var code=input.ToList();var hero=typeof(DeadBase).GetField("_hero",I|BindingFlags.DeclaredOnly);
        var loads=Enumerable.Range(0,code.Count).Where(i=>code[i].LoadsField(hero)).ToArray();
        var tracked=typeof(Viewport).GetProperty("tracked",I).GetMethod;
        var track=typeof(Viewport).GetMethods(I).Single(m=>m.Name=="track"&&m.GetParameters().Length==2&&m.GetParameters()[0].ParameterType==typeof(Entity)&&m.GetParameters()[1].ParameterType==typeof(bool?));
        if(loads.Length!=5||code.Count(i=>i.Calls(track))!=1)throw new InvalidOperationException("Pinned death camera load/call count changed");
        int comparison=loads[3],argument=loads[4],insert=comparison-3;
        if(insert<0||code[insert].opcode!=OpCodes.Ldloc_0||!code[insert+1].Calls(tracked)||code[comparison-1].opcode!=OpCodes.Ldarg_0||
            (code[comparison+1].opcode!=OpCodes.Beq_S&&code[comparison+1].opcode!=OpCodes.Beq)||code[argument-1].opcode!=OpCodes.Ldarg_0||code[argument+1].opcode!=OpCodes.Ldarg_1)
            throw new InvalidOperationException("Pinned death camera comparison/track shape changed: "+string.Join("|",code.Skip(System.Math.Max(0,insert)).Take(9).Select(i=>i.ToString())));
        var selected=generator.DeclareLocal(typeof(Entity));
        // Transfer labels to the inserted capture, so the original non-null
        // viewport branch cannot bypass it. All original exception blocks stay.
        var first=new CodeInstruction(OpCodes.Ldarg_0);first.labels.AddRange(code[insert].labels);code[insert].labels.Clear();
        first.blocks.AddRange(code[insert].blocks);code[insert].blocks.Clear();
        foreach(int index in new[]{comparison,argument}){code[index-1].opcode=OpCodes.Ldloc;code[index-1].operand=selected;code[index].opcode=OpCodes.Nop;code[index].operand=null;}
        code.InsertRange(insert,new[]{first,new CodeInstruction(OpCodes.Ldarg_0),new CodeInstruction(OpCodes.Ldfld,hero),
            new CodeInstruction(OpCodes.Call,typeof(DeadCameraPatch).GetMethod(nameof(SelectTarget),S)),new CodeInstruction(OpCodes.Stloc,selected)});
        return code;
    }
    static Entity SelectTarget(DeadBase cine,Hero local)
    {
        if(!Published||Disabled)return local;
        try
        {
            var owner=ModEntry.Instance;var net=ModEntry._net;
            if(cine==null||local==null||owner==null||net==null||!net.IsAlive||!net.HasRemote||
                !(bool)LocalDead.GetValue(owner)||!ReferenceEquals(LocalCine.GetValue(owner),cine)||!ReferenceEquals(CineHero.GetValue(cine),local)||!ReferenceEquals(ModEntry.me,local)||
                Pointer(cine)==IntPtr.Zero||Pointer(local)==IntPtr.Zero||cine.destroyed||local.destroyed)return local;
            var level=local._level;if(level==null||level.destroyed||Pointer(level)==IntPtr.Zero)return local;
            // Use the original selector, including its downed/pending-disposal
            // filtering and invalid-selection reset, exactly once per call.
            var target=Resolve(local);
            if(target==null||target.destroyed||Pointer(target)==IntPtr.Zero||Pointer(target)==Pointer(local)||Pointer(target._level)!=Pointer(level))return local;
            if(!ReferenceEquals(owner,ModEntry.Instance)||!ReferenceEquals(net,ModEntry._net)||!ReferenceEquals(ModEntry.me,local)||
                !ReferenceEquals(LocalCine.GetValue(owner),cine)||!(bool)LocalDead.GetValue(owner)||Pointer(local._level)!=Pointer(level))return local;
            Interlocked.Increment(ref Selections);return target;
        }
        catch(Exception error)
        {
            if(Interlocked.Increment(ref ReadFaults)==1)try{Serilog.Log.Warning("[CoopDeadCameraFix] Preserved original local target after inspection error: {Error}",error.Message);}catch{}
            return local;
        }
    }
}
