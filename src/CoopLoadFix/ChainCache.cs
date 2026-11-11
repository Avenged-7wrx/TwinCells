using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using Hashlink.Proxy.Clousre;
using Hashlink.Reflection.Types;

// Builds the handler chain of a hooked game function once instead of on every call.
//
// The mod framework (DCCM 35.13.5) enters ModCore.Hooks.HashlinkHookManager.HookEntry for every call of a hooked
// game function. The pinned method is:
//
//     HashlinkClosure prev = origClosure;
//     for (int i = 0; i < hooks.Count; i++) {
//         object prevDel = prev;
//         var fpt = hooks[i].GetType().GetDelegateInvoke().GetParameters()[0].ParameterType;
//         if (fpt != typeof(HashlinkClosure)) prevDel = prev.CreateDelegate(fpt);
//         prev = new HookOrigClosure(funcType, hooks[i].Bind(prevDel));
//     }
//     return prev.DynamicInvoke(args);          // TargetInvocationException is unwrapped and rethrown
//
// Everything the loop creates (adapter delegates, their info objects, the "orig" closure and its callback) is
// written only by constructors and holds no argument, result, thread or stack data: the arguments enter only at
// the last line. One input is not an object: for a handler with a target object the framework stores the current
// entry address of the handler method, and the runtime gives a different address before and after that method
// first runs (found by this module's own comparison in the game). The chain is therefore a function of
// (origClosure, the handler objects in order, those entry addresses). Measured: with one handler a dispatch
// costs about 4.5 microseconds and 0.9 KB of garbage, almost all of it this rebuild; the mod's per-enemy hooks
// cause 35-45 thousand dispatches per second.
//
// This patch keeps one built chain per hook manager and reuses it while the manager's handler list holds exactly
// the same delegate objects in the same order and every handler method still has the entry address the chain was
// built with (both compared on every call, so any way of changing the list is noticed and the stored addresses
// are always the ones the framework would read now). The last line is unchanged: the same DynamicInvoke on the same kind of closure, the same exception
// unwrapping. A list in which a later handler takes the closure object itself as its first parameter is never
// cached. Every sampled call (the first 8 after a build, then every 2048th) builds a fresh chain and compares the
// two object graphs; one difference switches the cache off for the session. Before the chain is invoked every
// problem falls back to the framework's own code; once it is invoked nothing is retried, so no handler runs twice.
//
// Known differences: (1) a handler receives the same "orig" delegate object on every call instead of a new,
// equal-behaving one per call (nothing in the mod or the add-ons compares or stores it); (2) the stack trace text
// of an exception that passes through a hook shows this method and Harmony's wrapper where the framework shows
// HookEntry. Exception object, type, message and inner exception are the same. (3) After the last handler of a
// function is removed, the chain built for the old list stays referenced (one per function) until that function
// gets handlers again; it is never used for a different list.
public static class ChainCache
{
    public const string HashlinkSharpPin="207FC8DD54D8A0A804E4051F90D2375F2C58CF6E69EFC26EF3E7EC502DFE8B63";
    public const string ModCorePin="630FBBCEDFE8B2E66C5F75234BE80C2EC42FB178C30706E7398583D9FA1A14AE";
    public const string HookEntryIlPin="D737AC90833184F5A3E9EB2DC10B09C865350E71A6525332A344098A9B42FD48";
    // The prefix relies on this Harmony build's rules (field injection, __runOriginal, a prefix returning false).
    public const string HarmonyPin="70E7647876B6C3D874EA99B56627A95D1DCE2E1DD791981A002578A2FEBC9BBD";
    public const int HookEntryIlLength=154;
    public const string Owner="CoopLoadFix.ChainCache.20261110";
    const BindingFlags Declared=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
    const BindingFlags Statics=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;

    sealed class Chain
    {
        public readonly Delegate[] Handlers;         // the objects the chain was built from, in order
        public readonly HashlinkClosure Last;        // null: this handler list is left to the framework's own code
        // For a handler with one target object the framework stores the handler method's entry address, which it
        // reads again on every call and which the runtime may change (before / after the method first runs).
        // Methods[i] is that method (null for the other handlers), Entries[i] the address the chain was built with.
        public readonly MethodBase[] Methods;
        public readonly IntPtr[] Entries;
        public long Calls;
        public Chain(Delegate[] handlers,HashlinkClosure last,MethodBase[] methods,IntPtr[] entries){Handlers=handlers;Last=last;Methods=methods;Entries=entries;}
    }
    static readonly MethodBase[] NoMethods=Array.Empty<MethodBase>();
    static readonly IntPtr[] NoEntries=Array.Empty<IntPtr>();

    static readonly object Gate=new();
    static ConditionalWeakTable<object,Chain> Cache=new();
    static volatile bool Off;
    static bool ShadowAll;
    static volatile bool Published;
    static Func<Type,MethodInfo> DelegateInvoke;
    static Func<Delegate,object,Type,Delegate> Bind;
    static Func<HashlinkFuncType,Delegate,HashlinkClosure> NewOrigClosure;
    static Func<DynamicMethod,RuntimeMethodHandle> DynamicHandle;
    static FieldInfo ClosureCallback;
    static PropertyInfo CallbackTarget;
    static Assembly HashlinkSharp;
    static Type OrigClosureType;
    static readonly Dictionary<Type,FieldInfo[]> InfoFields=new();
    static long HitCount,BuildCount,RefreshCount,UncachedCount,BuildFaultCount,SampleCount,MismatchCount,FaultCount;
    static Timer SelfCheck;
    public static bool Ready=>Published;
    public static bool Disabled{get;private set;}
    public static bool SwitchedOff=>Off;
    public static string InitializationError{get;private set;}
    public static string SwitchOffReason{get;private set;}
    public static long Hits=>Interlocked.Read(ref HitCount);
    public static long Builds=>Interlocked.Read(ref BuildCount);
    public static long Refreshes=>Interlocked.Read(ref RefreshCount);
    public static long Uncached=>Interlocked.Read(ref UncachedCount);
    public static long BuildFaults=>Interlocked.Read(ref BuildFaultCount);
    public static long Samples=>Interlocked.Read(ref SampleCount);
    public static long Mismatches=>Interlocked.Read(ref MismatchCount);
    public static long Faults=>Interlocked.Read(ref FaultCount);

    static string Hex(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    static Assembly Loaded(string name,string pin)
    {
        var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name==name)??throw new InvalidOperationException(name+" is not loaded");
        if(string.IsNullOrEmpty(assembly.Location)||global::CoopPackRuntime.PackPins.Sha256(assembly.Location)!=pin)throw new InvalidOperationException("CoopLoadFix hook chain cache requires the exact "+name+" of DCCM 35.13.5");
        return assembly;
    }

    public static void Install()
    {
        lock(Gate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier hook chain cache initialization failed: "+InitializationError);
            Disabled=Environment.GetEnvironmentVariable("COOP_LOAD_CHAIN_DISABLE")=="1";
            if(Disabled){Published=true;return;}                       // switched off: nothing is checked, nothing is patched
            ShadowAll=Environment.GetEnvironmentVariable("COOP_LOAD_CHAIN_SHADOW")=="all";
            Harmony harmony=null;MethodInfo entry=null;
            try
            {
                HashlinkSharp=Loaded("HashlinkSharp",HashlinkSharpPin);
                var modCore=Loaded("ModCore",ModCorePin);
                var harmonyAssembly=typeof(Harmony).Assembly;
                if(string.IsNullOrEmpty(harmonyAssembly.Location)||global::CoopPackRuntime.PackPins.Sha256(harmonyAssembly.Location)!=HarmonyPin)throw new InvalidOperationException("CoopLoadFix hook chain cache requires the exact Harmony of DCCM 35.13.5");
                var manager=modCore.GetType("ModCore.Hooks.HashlinkHookManager",true);
                var entries=manager.GetMethods(Declared).Where(m=>m.Name=="HookEntry").ToArray();
                if(entries.Length!=1)throw new InvalidOperationException("Pinned HookEntry not found exactly once");
                entry=entries[0];
                var parameters=entry.GetParameters();
                if(parameters.Length!=1||parameters[0].ParameterType!=typeof(object[])||parameters[0].Name!="args"||entry.ReturnType!=typeof(object))throw new InvalidOperationException("Pinned HookEntry signature changed");
                var il=entry.GetMethodBody()?.GetILAsByteArray();
                if(il==null||il.Length!=HookEntryIlLength||Hex(il)!=HookEntryIlPin)throw new InvalidOperationException("Pinned HookEntry body changed");
                // The three fields the replacement reads, with the exact types of the pinned class.
                var hooks=manager.GetField("hooks",Declared);var orig=manager.GetField("origClosure",Declared);var funcType=manager.GetField("funcType",Declared);
                if(hooks==null||hooks.FieldType!=typeof(List<Delegate>)||orig==null||orig.FieldType!=typeof(HashlinkClosure)||funcType==null||funcType.FieldType!=typeof(HashlinkFuncType))
                    throw new InvalidOperationException("Pinned HashlinkHookManager fields changed");
                // The three calls of the loop, bound once. None of them goes through MethodInfo.Invoke.
                var utils=HashlinkSharp.GetType("Hashlink.Utils",true);
                var getInvoke=utils.GetMethod("GetDelegateInvoke",Statics,null,new[]{typeof(Type)},null);
                if(getInvoke==null||getInvoke.ReturnType!=typeof(MethodInfo))throw new InvalidOperationException("Pinned Utils.GetDelegateInvoke changed");
                DelegateInvoke=(Func<Type,MethodInfo>)getInvoke.CreateDelegate(typeof(Func<Type,MethodInfo>));
                var delegates=HashlinkSharp.GetType("Hashlink.UnsafeUtilities.UtilityDelegates",true);
                var bind=delegates.GetMethod("Bind",Statics,null,new[]{typeof(Delegate),typeof(object),typeof(Type)},null);
                if(bind==null||bind.ReturnType!=typeof(Delegate))throw new InvalidOperationException("Pinned UtilityDelegates.Bind changed");
                Bind=(Func<Delegate,object,Type,Delegate>)bind.CreateDelegate(typeof(Func<Delegate,object,Type,Delegate>));
                // How the framework reads the entry address of a handler that is a dynamic method (DelegateInfo's constructor).
                var dynamicHandle=delegates.GetMethod("GetDynamicMethodHandle",Statics,null,new[]{typeof(DynamicMethod)},null);
                if(dynamicHandle==null||dynamicHandle.ReturnType!=typeof(RuntimeMethodHandle))throw new InvalidOperationException("Pinned UtilityDelegates.GetDynamicMethodHandle changed");
                DynamicHandle=(Func<DynamicMethod,RuntimeMethodHandle>)dynamicHandle.CreateDelegate(typeof(Func<DynamicMethod,RuntimeMethodHandle>));
                OrigClosureType=modCore.GetType("ModCore.Hooks.HookOrigClosure",true);
                var ctors=OrigClosureType.GetConstructors(Declared);
                if(ctors.Length!=1)throw new InvalidOperationException("Pinned HookOrigClosure constructor changed");
                var ctorParameters=ctors[0].GetParameters();
                if(ctorParameters.Length!=2||ctorParameters[0].ParameterType!=typeof(HashlinkFuncType)||ctorParameters[1].ParameterType!=typeof(Delegate)||!typeof(HashlinkClosure).IsAssignableFrom(OrigClosureType))
                    throw new InvalidOperationException("Pinned HookOrigClosure constructor changed");
                var make=new DynamicMethod("CoopLoadFix_NewOrigClosure",typeof(HashlinkClosure),new[]{typeof(HashlinkFuncType),typeof(Delegate)},typeof(ChainCache).Module,true);
                var gen=make.GetILGenerator();
                gen.Emit(OpCodes.Ldarg_0);gen.Emit(OpCodes.Ldarg_1);gen.Emit(OpCodes.Newobj,ctors[0]);gen.Emit(OpCodes.Ret);
                NewOrigClosure=(Func<HashlinkFuncType,Delegate,HashlinkClosure>)make.CreateDelegate(typeof(Func<HashlinkFuncType,Delegate,HashlinkClosure>));
                // Used only by the sampled comparison.
                ClosureCallback=typeof(HashlinkClosure).GetField("callback",Declared);
                CallbackTarget=ClosureCallback?.FieldType.GetProperty("Target",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                if(ClosureCallback==null||CallbackTarget==null||!typeof(Delegate).IsAssignableFrom(CallbackTarget.PropertyType))throw new InvalidOperationException("Pinned HashlinkClosure.callback changed");

                harmony=new Harmony(Owner);
                // Last among prefixes: a measuring prefix of another module starts before the dispatch this one performs.
                harmony.Patch(entry,prefix:new HarmonyMethod(typeof(ChainCache).GetMethod(nameof(BeforeHookEntry),BindingFlags.Static|BindingFlags.NonPublic)){priority=Priority.Last});
                var info=Harmony.GetPatchInfo(entry);
                if(info==null||info.Prefixes.Count(p=>p.owner==Owner)!=1)throw new InvalidOperationException("Hook chain cache patch was not installed exactly once");
                Published=true;
                // Only a hint for the log: by this time the game loop normally runs and hooked functions are called.
                SelfCheck=new Timer(_=>
                {
                    try{if(Hits==0&&Uncached==0&&!Off)Serilog.Log.Warning("[CoopLoadFix] Hook chain cache has served no call in the first 3 minutes (no hooked function with handlers was called, or the patch is not in effect)");}catch{}
                },null,180000,Timeout.Infinite);
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;Off=true;
                // Also when Patch itself threw: Harmony may have registered the prefix before it failed.
                if(harmony!=null&&entry!=null)try{harmony.Unpatch(entry,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }

    static void SwitchOff(string reason)
    {
        if(Off)return;
        Off=true;SwitchOffReason=reason;
        Cache=new ConditionalWeakTable<object,Chain>();
        try{Serilog.Log.Warning("[CoopLoadFix] Hook chain cache switched off, the framework's original dispatch is in effect: {Reason}",reason);}catch{}
    }

    // Replaces the body of HookEntry when a chain for the current handler list is available.
    // Returning true = the framework's own code serves this call.
    static bool BeforeHookEntry(object __instance,List<Delegate> ___hooks,HashlinkClosure ___origClosure,HashlinkFuncType ___funcType,object[] args,ref object __result,bool __runOriginal)
    {
        if(!__runOriginal)return false;                  // another prefix already replaced this call: do not dispatch a second time
        if(Off)return true;
        Chain chain;
        try
        {
            var hooks=___hooks;
            if(hooks==null||hooks.Count==0)return true;  // nothing to build: the original is a single call
            bool known=Cache.TryGetValue(__instance,out chain)&&ChainRules.SameHandlers(hooks,chain.Handlers);
            if(known&&!EntriesCurrent(chain)){known=false;RefreshCount++;}
            if(!known)
            {
                chain=Build(hooks,___origClosure,___funcType,true);
                if(chain==null){UncachedCount++;return true;}    // an entry address moved while building: this call is the framework's, the next one builds again
                Cache.AddOrUpdate(__instance,chain);
            }
            if(chain.Last==null){UncachedCount++;return true;}
            long call=++chain.Calls;
            if(ChainRules.ShouldSample(call,ShadowAll)&&!MatchesFreshChain(__instance,chain,hooks,___origClosure,___funcType,out string difference))
            {
                if(difference==null){UncachedCount++;return true;}   // the inputs moved under the comparison: this call is the framework's, the next one builds again
                MismatchCount++;
                SwitchOff("a cached chain differs from a freshly built one; "+Describe(__instance)+"; handlers="+chain.Handlers.Length+"; call="+call+"; "+difference);
                return true;
            }
        }
        catch(Exception error)
        {
            // Nothing of the game has run yet: the framework's own code serves this call and all later ones.
            FaultCount++;
            SwitchOff("unexpected "+error.GetType().Name+" before dispatch: "+error.Message);
            return true;
        }
        HitCount++;
        // The last line of the pinned method, with its exception handling.
        try{__result=chain.Last.DynamicInvoke(args);}
        catch(TargetInvocationException wrapped){ExceptionDispatchInfo.Capture(wrapped.InnerException!).Throw();throw;}
        return false;
    }

    // The entry address the framework's DelegateInfo constructor stores for a handler method, read the same way.
    static IntPtr EntryOf(MethodBase method)=>method is DynamicMethod dynamic?DynamicHandle(dynamic).GetFunctionPointer():method.MethodHandle.GetFunctionPointer();
    // Are the addresses the chain was built with still the ones the framework would read now?
    static bool EntriesCurrent(Chain chain)
    {
        var methods=chain.Methods;var entries=chain.Entries;
        for(int i=0;i<methods.Length;i++)if(methods[i]!=null&&EntryOf(methods[i])!=entries[i])return false;
        return true;
    }

    // The loop of the pinned HookEntry, on a snapshot of the handler list. Never throws.
    // Returns null when an entry address moved during the build (nothing is stored; the next call builds again).
    static Chain Build(List<Delegate> live,HashlinkClosure orig,HashlinkFuncType funcType,bool count)
    {
        Delegate[] handlers;
        try{handlers=live.ToArray();}catch{if(count)BuildFaultCount++;return new Chain(Array.Empty<Delegate>(),null,NoMethods,NoEntries);}
        try
        {
            if(!ChainRules.CacheableCount(handlers.Length))return new Chain(handlers,null,NoMethods,NoEntries);
            var methods=new MethodBase[handlers.Length];var entries=new IntPtr[handlers.Length];
            HashlinkClosure prev=orig;
            for(int i=0;i<handlers.Length;i++)
            {
                object prevDel=prev;
                var handler=handlers[i];
                var first=DelegateInvoke(handler.GetType()).GetParameters()[0].ParameterType;
                bool closureParameter=first==typeof(HashlinkClosure);
                if(!ChainRules.Cacheable(i,closureParameter))return new Chain(handlers,null,NoMethods,NoEntries);
                if(ChainRules.HasOwnEntryAddress(handler.HasSingleTarget,handler.Target!=null)){methods[i]=handler.Method;entries[i]=EntryOf(methods[i]);}
                if(!closureParameter)prevDel=prev.CreateDelegate(first);
                prev=NewOrigClosure(funcType,Bind(handler,prevDel,null));
            }
            // The framework read the same addresses inside Bind. If one moved in between (the handler method ran for
            // the first time on another thread), the chain may hold either value: do not keep it.
            for(int i=0;i<methods.Length;i++)if(methods[i]!=null&&EntryOf(methods[i])!=entries[i])return null;
            if(count)BuildCount++;
            return new Chain(handlers,prev,methods,entries);
        }
        catch
        {
            // Whatever made the build fail, the framework's own loop will meet it on this call and report it its own way.
            if(count)BuildFaultCount++;
            return new Chain(handlers,null,NoMethods,NoEntries);
        }
    }

    // ---- sampled comparison: the cached object graph against one built now from the same inputs ----
    // Returns false with a text: the two graphs differ (the cache is wrong). Returns false with difference == null:
    // the handler list or an entry address changed between the validity check and this build (possible only when
    // another thread is involved); the stored chain is dropped and nothing is concluded.
    static bool MatchesFreshChain(object manager,Chain chain,List<Delegate> hooks,HashlinkClosure orig,HashlinkFuncType funcType,out string difference)
    {
        SampleCount++;difference=null;
        var fresh=Build(hooks,orig,funcType,false);
        if(fresh==null||!ChainRules.SameHandlers(hooks,chain.Handlers)||!SameSnapshot(fresh.Handlers,chain.Handlers)||(fresh.Last!=null&&!ChainRules.SameEntryAddresses(chain.Entries,fresh.Entries)))
        {
            Cache.Remove(manager);
            return false;
        }
        if(fresh.Last==null){difference="a fresh chain could not be built";return false;}
        var a=new List<object>(64);var b=new List<object>(64);
        Walk(chain.Last,orig,a,0);Walk(fresh.Last,orig,b,0);
        if(ChainRules.SameFingerprint(a,b))return true;
        // Only for the log line: where the two walks part.
        try
        {
            int at=0;int common=Math.Min(a.Count,b.Count);
            while(at<common&&ChainRules.SameFingerprint(new[]{a[at]},new[]{b[at]}))at++;
            difference="walk lengths "+a.Count+"/"+b.Count+", first difference at "+at+": cached "+(at<a.Count?Text(a[at]):"(end)")+" vs fresh "+(at<b.Count?Text(b[at]):"(end)")
                +"; before it: "+string.Join(" > ",a.Take(at).Skip(Math.Max(0,at-6)).Select(Text));
        }
        catch(Exception error){difference="difference could not be described: "+error.Message;}
        return false;
    }
    static bool SameSnapshot(Delegate[] a,Delegate[] b)
    {
        if(a.Length!=b.Length)return false;
        for(int i=0;i<a.Length;i++)if(!ReferenceEquals(a[i],b[i]))return false;
        return true;
    }
    static string Text(object x)
    {
        if(x==null)return "null";
        if(x is string s)return "'"+s+"'";
        if(x is Type t)return "type "+t.FullName;
        if(x is MethodBase m)return "method "+m.DeclaringType?.FullName+"."+m.Name+"#"+RuntimeHelpers.GetHashCode(m).ToString("X");
        if(x is IntPtr p)return "ptr 0x"+p.ToString("X");
        if(x is Delegate d)return "delegate "+d.GetType().FullName+" -> "+d.Method?.DeclaringType?.FullName+"."+d.Method?.Name;
        return x.GetType().FullName+"#"+RuntimeHelpers.GetHashCode(x).ToString("X");
    }
    // Which hooked function a manager serves (log text only).
    static string Describe(object manager)
    {
        try
        {
            var function=manager.GetType().GetField("function",Declared)?.GetValue(manager);
            if(function==null)return "function unknown";
            string name=null;object index=null;
            foreach(var property in function.GetType().GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
            {
                if(property.GetIndexParameters().Length!=0)continue;
                if(property.Name=="Name")try{name=Convert.ToString(property.GetValue(function));}catch{}
                if(property.Name=="FunctionIndex")try{index=property.GetValue(function);}catch{}
            }
            return "function #"+index+" "+(name??function.ToString());
        }
        catch(Exception error){return "function not readable: "+error.Message;}
    }
    static FieldInfo[] FieldsOf(Type type)
    {
        lock(InfoFields)
        {
            if(!InfoFields.TryGetValue(type,out var fields))
            {
                var list=new List<FieldInfo>();
                for(var t=type;t!=null&&t!=typeof(object);t=t.BaseType)list.AddRange(t.GetFields(Declared));
                InfoFields[type]=fields=list.OrderBy(f=>f.DeclaringType.FullName+"."+f.Name,StringComparer.Ordinal).ToArray();
            }
            return fields;
        }
    }
    static void Walk(object x,HashlinkClosure orig,List<object> walk,int depth)
    {
        if(depth>48){walk.Add("too deep");return;}
        if(x==null){walk.Add(null);return;}
        if(ReferenceEquals(x,orig)){walk.Add("original closure");return;}
        if(x is HashlinkClosure closure)
        {
            if(x.GetType()!=OrigClosureType){walk.Add(x);return;}             // any other closure must be the very same object
            walk.Add(OrigClosureType);
            var callback=ClosureCallback.GetValue(closure);
            Walk(callback==null?null:CallbackTarget.GetValue(callback),orig,walk,depth+1);
            return;
        }
        if(x is Delegate d)
        {
            walk.Add(d.GetType());walk.Add(d.Method);
            var targets=d.GetInvocationList();
            if(targets.Length!=1){walk.Add(x);return;}                        // a combined delegate must be the very same object
            Walk(d.Target,orig,walk,depth+1);
            return;
        }
        var type=x.GetType();
        if(type.Assembly==HashlinkSharp&&(type.Name=="DelegateInfo"||type.Name=="ClosureInfo"||type.Name=="WrapperInfo"))
        {
            walk.Add(type);
            foreach(var field in FieldsOf(type))
            {
                var value=field.GetValue(x);
                if(value==null)walk.Add(null);
                else if(ChainRules.IsValueLike(value))walk.Add(value);
                else Walk(value,orig,walk,depth+1);
            }
            return;
        }
        walk.Add(x);                                                           // everything else must be the very same object
    }
}
