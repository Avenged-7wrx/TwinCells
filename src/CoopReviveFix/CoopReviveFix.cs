using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using Entity=dc.Entity;
using dc.en;
using dc.tool.atk;
using DeadCellsMultiplayerMod;
using ModCore.Mods;
using Serilog;

// Optional standalone candidate. The original multiplayer assembly is never modified.
public sealed class CoopReviveFix : ModBase
{
    const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    const string ProxyPin="2A1803BA27DB053A21CD854CA4F720C5F5D874FE384F72603CA524F21BB98259";
    const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly object InitGate=new object();
    static readonly FieldInfo FakeDead=typeof(ModEntry).GetField("_localFakeDead",F);
    static readonly FieldInfo Role=typeof(ModEntry).GetField("_netRole",F);
    static Delegate Handler;
    static EventInfo Hook;
    static bool InstallAttempted;
    public static bool Ready {get;private set;}
    public static bool Disabled {get;private set;}
    public static string InitializationError {get;private set;}
    public static long Suppressed,Forwarded,PredicateErrors,ObserverErrors;
    public CoopReviveFix(ModInfo info):base(info){}

    public override void Initialize()
    {
        lock(InitGate)
        {
            if(Ready || Disabled)return;
            if(InstallAttempted)throw new InvalidOperationException("CoopReviveFix initialization previously failed");
            InstallAttempted=true;
            if(Environment.GetEnvironmentVariable("COOP_REVIVE_BOUNDARY_DISABLE")=="1")
            {Disabled=true;Log.Information("[CoopReviveFix] Disabled by COOP_REVIVE_BOUNDARY_DISABLE");return;}
            bool addAttempted=false;
            try
            {
                VerifyPinnedFiles();
                if(FakeDead==null || FakeDead.FieldType!=typeof(bool) || Role==null || !Role.FieldType.IsEnum)throw new InvalidOperationException("Pinned fake-death fields changed");
                Hook=typeof(Entity).Assembly.GetType("dc.Hook_Entity",true).GetEvent("applyAttackResult",F);
                var abi=ReviveNativeAbi.Validate(Hook);
                var boundary=new ReviveDamageBoundary(IsCurrentLocalFakeDead,ReviveNativeAbi.MakeForwarder(abi.Original),Observe,ObservePredicateError);
                Handler=ReviveNativeAbi.MakeHandler(abi.Handler,boundary);
                addAttempted=true;Hook.AddEventHandler(null,Handler);
                Ready=true;
                Log.Information("[CoopReviveFix] Ready: source-less damage to the current local downed hero is fenced before health subtraction");
            }
            catch(Exception ex)
            {
                Ready=false;InitializationError=ex.ToString();
                if(addAttempted && Hook!=null && Handler!=null)
                {try{Hook.RemoveEventHandler(null,Handler);}catch(Exception rollback){InitializationError+="\nRollback: "+rollback;}}
                Handler=null;throw;
            }
        }
    }
    static void VerifyPinnedFiles()
    {
        var original=typeof(ModEntry).Assembly;var proxy=typeof(Entity).Assembly;
        if(original.GetName().Version!=new Version(0,5,1,0) || Hash(original.Location)!=BasePin ||
            Hash(Path.Combine(Path.GetDirectoryName(proxy.Location),"hlboot.dat"))!=NativePin)
            throw new InvalidOperationException("CoopReviveFix requires the pinned original 0.5.1 multiplayer and 35.13.5 native runtime");
        ReviveProxyCompatibility.ValidateLoaded(proxy);
    }
    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static long Ptr(object value)=>value is Entity entity?entity.HashlinkPointer.ToInt64():0;
    static bool IsCurrentLocalFakeDead(Entity target)
    {
        if(!Ready || Environment.GetEnvironmentVariable("COOP_REVIVE_BOUNDARY_DISABLE")=="1" || target==null)return false;
        var owner=ModEntry.Instance;if(owner==null || !(FakeDead.GetValue(owner) is true) || Convert.ToInt32(Role.GetValue(owner))==0)return false;
        var net=ModEntry._net;var hero=ModEntry.me;
        if(net==null || !net.IsAlive || hero==null || hero.destroyed)return false;
        long targetPointer=Ptr(target),heroPointer=Ptr(hero);
        if(targetPointer==0 || targetPointer!=heroPointer)return false;
        var game=dc.pr.Game.Class.ME;var level=hero._level;var targetLevel=target._level;var current=game?.curLevel;
        var snapshot=new ReviveIdentity {
            Downed=true,ActiveNet=true,ActiveRole=true,HeroValid=!hero.destroyed,
            Target=targetPointer,Hero=heroPointer,GameHero=Ptr(game?.hero),
            TargetLevel=targetLevel?.HashlinkPointer.ToInt64()??0,HeroLevel=level?.HashlinkPointer.ToInt64()??0,CurrentLevel=current?.HashlinkPointer.ToInt64()??0,
            HeroGame=level?.game?.HashlinkPointer.ToInt64()??0,CurrentGame=game?.HashlinkPointer.ToInt64()??0,
            StableOwner=ReferenceEquals(ModEntry.Instance,owner) && ReferenceEquals(ModEntry._net,net) && net.IsAlive && FakeDead.GetValue(owner) is true && Convert.ToInt32(Role.GetValue(owner))!=0
        };
        return ReviveIdentity.ShouldSuppress(snapshot);
    }
    static void Observe(bool suppressed){if(suppressed)Interlocked.Increment(ref Suppressed);else Interlocked.Increment(ref Forwarded);}
    static void ObservePredicateError(Exception ex)
    {Interlocked.Increment(ref PredicateErrors);try{Log.Warning("[CoopReviveFix] Classification failed; native damage is forwarded: {Error}",ex.Message);}catch{Interlocked.Increment(ref ObserverErrors);}}
}

// Scalar classification is independently testable without native objects or game initialization.
public struct ReviveIdentity
{
    public bool Downed,ActiveNet,ActiveRole,HeroValid,StableOwner;
    public long Target,Hero,GameHero,TargetLevel,HeroLevel,CurrentLevel,HeroGame,CurrentGame;
    public static bool ShouldSuppress(ReviveIdentity s)=>s.Downed && s.ActiveNet && s.ActiveRole && s.HeroValid && s.StableOwner && s.Target!=0 && s.Target==s.Hero && s.Hero==s.GameHero &&
        s.HeroLevel!=0 && s.TargetLevel==s.HeroLevel && s.HeroLevel==s.CurrentLevel && s.HeroGame!=0 && s.HeroGame==s.CurrentGame;
}
public sealed class ReviveDamageBoundary
{
    readonly Func<Entity,bool> Predicate;
    readonly Action<object,Entity,AttackData> Forward;
    readonly Action<bool> Observer;
    readonly Action<Exception> PredicateError;
    public ReviveDamageBoundary(Func<Entity,bool> predicate,Action<object,Entity,AttackData> forward,Action<bool> observer,Action<Exception> predicateError)
    {Predicate=predicate??throw new ArgumentNullException(nameof(predicate));Forward=forward??throw new ArgumentNullException(nameof(forward));Observer=observer;PredicateError=predicateError;}
    public void Invoke(object original,Entity target,AttackData attack)
    {
        bool suppress=false;
        try{suppress=Predicate(target);}catch(Exception ex){try{PredicateError?.Invoke(ex);}catch{}}
        try{Observer?.Invoke(suppress);}catch{}
        if(suppress)return;
        // Keep original outside every observer/predicate catch: preserve its exact exception object.
        Forward(original,target,attack);
    }
}
public static class ReviveNativeAbi
{
    public sealed class Shape {public Type Handler,Original;}
    public static Shape Validate(EventInfo hook)
    {
        if(hook==null || hook.AddMethod==null || hook.RemoveMethod==null || !hook.AddMethod.IsStatic || !hook.RemoveMethod.IsStatic)throw new InvalidOperationException("Pinned Entity.applyAttackResult hook is absent");
        var handler=hook.EventHandlerType;var invoke=handler?.GetMethod("Invoke");var p=invoke?.GetParameters();
        if(invoke==null || invoke.ReturnType!=typeof(void) || p.Length!=3 || p[1].ParameterType!=typeof(Entity) || p[2].ParameterType!=typeof(AttackData) || !typeof(Delegate).IsAssignableFrom(p[0].ParameterType))
            throw new InvalidOperationException("Pinned native applyAttackResult handler ABI changed");
        var original=p[0].ParameterType;var call=original.GetMethod("Invoke");var args=call?.GetParameters();
        if(call==null || call.ReturnType!=typeof(void) || args.Length!=2 || args[0].ParameterType!=typeof(Entity) || args[1].ParameterType!=typeof(AttackData))
            throw new InvalidOperationException("Pinned native applyAttackResult original ABI changed");
        return new Shape{Handler=handler,Original=original};
    }
    public static Action<object,Entity,AttackData> MakeForwarder(Type original)
    {
        var dm=new DynamicMethod("CoopReviveFix_Original",typeof(void),new[]{typeof(object),typeof(Entity),typeof(AttackData)},typeof(ReviveNativeAbi),true);var il=dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,original);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Callvirt,original.GetMethod("Invoke"));il.Emit(OpCodes.Ret);
        return (Action<object,Entity,AttackData>)dm.CreateDelegate(typeof(Action<object,Entity,AttackData>));
    }
    public static Delegate MakeHandler(Type handler,ReviveDamageBoundary boundary)
    {
        var p=handler.GetMethod("Invoke").GetParameters();var dm=new DynamicMethod("CoopReviveFix_Boundary",typeof(void),new[]{typeof(ReviveDamageBoundary),p[0].ParameterType,p[1].ParameterType,p[2].ParameterType},typeof(ReviveNativeAbi),true);var il=dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Ldarg_3);il.Emit(OpCodes.Callvirt,typeof(ReviveDamageBoundary).GetMethod("Invoke"));il.Emit(OpCodes.Ret);
        return dm.CreateDelegate(handler,boundary);
    }
}
