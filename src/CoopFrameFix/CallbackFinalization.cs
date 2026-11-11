using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;

// Removes the main cause of garbage-collector hitches in modded co-op play.
//
// Measured (two real game processes, level with about 140 mobs): the mod framework (DCCM 35.13.5) dispatches
// 60-75 thousand hooked game-function calls per second (sprite draw, mob update, hit tests ...). For every
// handler of every call ModCore.Hooks.HashlinkHookManager.HookEntry builds a HookOrigClosure, whose
// constructor creates a new Hashlink.Wrapper.Callbacks.HlCallback. HlCallback has a finalizer, so every one
// of the about 100 000 objects per second survives its first collection together with everything it
// references, is promoted, waits for the finalizer thread and dies one generation later. That promotion is
// what makes each collection take 20-35 ms.
//
// The finalizer only releases the native thunk that HlCallback.get_NativePointer allocates lazily:
//     ~HlCallback() { if (precode != 0) { memoryManager.Free(precode); precode = 0; } }
// The callback of a hook "orig" closure normally never gets that thunk (HookOrigClosure.HashlinkPointer is the
// constant 0; only an explicit HashlinkClosure.FunctionPtr read would create one), so its finalizer has nothing
// to do.
//
// This patch makes finalization lazy for exactly those callbacks and keeps it exact:
//   * after HookOrigClosure..ctor: GC.SuppressFinalize(closure.callback)
//   * before get_NativePointer allocates a thunk (routerPtr == 0), for ANY callback:
//         GC.SuppressFinalize(callback); GC.ReRegisterForFinalize(callback);
//     which leaves the object registered exactly once whether or not it had been suppressed.
// Callbacks created anywhere else (hook managers, proto overrides, delegates handed to the game) are never
// touched at construction and keep the framework's ordinary finalization. A callback that owns a thunk is
// finalized as before; one that never gets a thunk is ordinary garbage. Same objects, same calls, same results.
public static class CallbackFinalization
{
    public const string HashlinkSharpPin="207FC8DD54D8A0A804E4051F90D2375F2C58CF6E69EFC26EF3E7EC502DFE8B63";
    public const string ModCorePin="630FBBCEDFE8B2E66C5F75234BE80C2EC42FB178C30706E7398583D9FA1A14AE";
    public const string Owner="CoopFrameFix.CallbackFinalization.20261110";
    const BindingFlags Declared=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
    static readonly object Gate=new();
    static Func<object,bool> ThunkMissing;
    static Func<object,object> ClosureCallback;
    static long SuppressedCount,ReRegisteredCount,FaultCount;
    static volatile bool Published;
    static Timer SelfCheck;
    public static bool Ready=>Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static long Suppressed=>Interlocked.Read(ref SuppressedCount);
    public static long ReRegistered=>Interlocked.Read(ref ReRegisteredCount);
    public static long Faults=>Interlocked.Read(ref FaultCount);

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static Assembly Loaded(string name,string pin)
    {
        var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name==name)??throw new InvalidOperationException(name+" is not loaded");
        if(string.IsNullOrEmpty(assembly.Location)||Hash(assembly.Location)!=pin)throw new InvalidOperationException("CoopFrameFix callback finalization requires the exact "+name+" of DCCM 35.13.5");
        return assembly;
    }

    public static void Install()
    {
        lock(Gate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier callback finalization initialization failed: "+InitializationError);
            Disabled=Environment.GetEnvironmentVariable("COOP_FRAME_CALLBACK_DISABLE")=="1";
            if(Disabled){Published=true;return;}                       // switched off: nothing is checked, nothing is patched
            Harmony harmony=null;ConstructorInfo origCtor=null;MethodInfo getter=null;bool getterPatched=false,origPatched=false;
            try
            {
                var callbackType=Loaded("HashlinkSharp",HashlinkSharpPin).GetType("Hashlink.Wrapper.Callbacks.HlCallback",true);
                var orig=Loaded("ModCore",ModCorePin).GetType("ModCore.Hooks.HookOrigClosure",true);
                // Shape of the pinned classes.
                var router=callbackType.GetField("routerPtr",Declared);var precode=callbackType.GetField("precode",Declared);
                if(router==null||router.FieldType!=typeof(IntPtr)||precode==null)throw new InvalidOperationException("Pinned HlCallback fields changed");
                getter=callbackType.GetMethod("get_NativePointer",Declared,null,Type.EmptyTypes,null);
                if(getter==null||getter.ReturnType!=typeof(IntPtr))throw new InvalidOperationException("Pinned HlCallback.NativePointer changed");
                if(callbackType.GetMethod("Finalize",Declared,null,Type.EmptyTypes,null)==null)throw new InvalidOperationException("Pinned HlCallback finalizer missing");
                var origCtors=orig.GetConstructors(Declared);
                if(origCtors.Length!=1||origCtors[0].GetParameters().Length!=2)throw new InvalidOperationException("Pinned HookOrigClosure constructor changed");
                origCtor=origCtors[0];
                FieldInfo closureCallback=null;
                for(var t=orig.BaseType;t!=null&&closureCallback==null;t=t.BaseType)closureCallback=t.GetField("callback",Declared);
                if(closureCallback==null||closureCallback.FieldType!=callbackType||closureCallback.DeclaringType?.FullName!="Hashlink.Proxy.Clousre.HashlinkClosure")
                    throw new InvalidOperationException("Pinned HashlinkClosure.callback changed");
                var probe=new DynamicMethod("CoopFrameFix_ThunkMissing",typeof(bool),new[]{typeof(object)},typeof(CallbackFinalization).Module,true);
                var il=probe.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,callbackType);il.Emit(OpCodes.Ldfld,router);
                il.Emit(OpCodes.Ldsfld,typeof(IntPtr).GetField(nameof(IntPtr.Zero)));
                il.Emit(OpCodes.Call,typeof(IntPtr).GetMethod("op_Equality",new[]{typeof(IntPtr),typeof(IntPtr)}));
                il.Emit(OpCodes.Ret);
                ThunkMissing=(Func<object,bool>)probe.CreateDelegate(typeof(Func<object,bool>));
                var read=new DynamicMethod("CoopFrameFix_ClosureCallback",typeof(object),new[]{typeof(object)},typeof(CallbackFinalization).Module,true);
                il=read.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,closureCallback.DeclaringType);il.Emit(OpCodes.Ldfld,closureCallback);il.Emit(OpCodes.Ret);
                ClosureCallback=(Func<object,object>)read.CreateDelegate(typeof(Func<object,object>));
                harmony=new Harmony(Owner);
                HarmonyMethod M(string name,int priority)=>new HarmonyMethod(typeof(CallbackFinalization).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)){priority=priority};
                // The re-registration first: nothing is suppressed before what undoes it is in place.
                harmony.Patch(getter,prefix:M(nameof(BeforeNativePointer),Priority.First));getterPatched=true;
                harmony.Patch(origCtor,postfix:M(nameof(AfterOrigClosure),Priority.Last));origPatched=true;
                var a=Harmony.GetPatchInfo(getter);var b=Harmony.GetPatchInfo(origCtor);
                if(a==null||a.Prefixes.Count(p=>p.owner==Owner)!=1||b==null||b.Postfixes.Count(p=>p.owner==Owner)!=1)
                    throw new InvalidOperationException("Callback finalization patches were not installed exactly once");
                Published=true;
                // The patch does nothing (harmlessly) if the constructor was already compiled into its caller. Say so once.
                SelfCheck=new Timer(_=>
                {
                    try{if(Suppressed==0)Serilog.Log.Warning("[CoopFrameFix] Lazy callback finalization saw no hook callback in 30 s; the framework's original behaviour is in effect");}catch{}
                },null,30000,Timeout.Infinite);
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;
                if(harmony!=null)
                {
                    // Stop suppressing first. The re-registration is removed only when that succeeded: "suppress without
                    // re-register" is the one state that could leak a thunk.
                    bool suppressionGone=!origPatched;
                    if(origPatched)try{harmony.Unpatch(origCtor,HarmonyPatchType.All,Owner);suppressionGone=true;}catch(Exception rollback){InitializationError+="; rollback closure: "+rollback.Message;}
                    if(getterPatched&&suppressionGone)try{harmony.Unpatch(getter,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback getter: "+rollback.Message;}
                }
                throw;
            }
        }
    }

    // A freshly built hook "orig" closure: its callback has no thunk yet.
    static void AfterOrigClosure(object __instance)
    {
        if(__instance==null)return;
        object callback;
        try{callback=ClosureCallback(__instance);}
        catch{Interlocked.Increment(ref FaultCount);return;}              // cannot read it: leave the ordinary finalization
        if(callback==null)return;
        GC.SuppressFinalize(callback);
        Interlocked.Increment(ref SuppressedCount);
    }

    // Runs before every NativePointer read of any callback. Only the first read (routerPtr still 0) allocates the
    // thunk the finalizer frees.
    static void BeforeNativePointer(object __instance)
    {
        if(__instance==null)return;
        bool missing;
        try{missing=ThunkMissing(__instance);}
        catch{missing=true;Interlocked.Increment(ref FaultCount);}       // when in doubt keep the original finalization
        if(!missing)return;
        // Suppress + re-register = registered exactly once, whether the object had been suppressed or not
        // (re-registering an unsuppressed object would add a second queue entry).
        GC.SuppressFinalize(__instance);
        GC.ReRegisterForFinalize(__instance);
        Interlocked.Increment(ref ReRegisteredCount);
    }
}
