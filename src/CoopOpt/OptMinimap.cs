using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Threading;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.MultiplayerModUI.Minimap;
using HarmonyLib;
using R=OptMinimapRules;
using GhostKing=DeadCellsMultiplayerMod.Ghost.GhostBase.GhostKing;
using MiniMap=dc.ui.hud.MiniMap;
using MMTracker=dc.ui.hud.MMTracker;
using Graphics=dc.h2d.Graphics;
using ArrayObj=dc.hl.types.ArrayObj;
using HookMiniMap=dc.ui.hud.Hook_MiniMap;

// CoopOpt part OptMinimap: the community mod's minimap marker loop without the per-tracker dynamic scan (both roles).
// Design: host-load-20261110\design design-other-hooks D3 / design-mod-call-sites D4, with every correction of
// round2\minimap\skeptic.md (verdict go-with-changes).
//
// The release handler Minimapreveal.Hook_MiniMap_postUpdate (0.5.1 IL 88207-88352) runs, every frame, orig(self), then
// for EVERY minimap tracker getDyn + three C# dynamic call sites + MMTracker.e + HL Std.is(e, KingSkin.Class), and
// for every tracker that holds a KingSkin it writes the same x / y into every king marker (one per client slot).
// The writes depend only on (GetPrimaryClient() != null) && (some tracker holds a KingSkin) and the clients' cx / cy.
//
// Fast path (Harmony prefix, Priority.Last, on the plain managed class Minimapreveal : System.Object; returns false):
//   orig(self)                                   the release's first statement, unguarded: its exceptions are the game's
//   kmap = kingcircle; if null return            IL_0007-IL_000F
//   king = GetPrimaryClient(); n = self.trackers.length      same calls, same order (IL_0014-IL_0027)
//   decide (OptMinimapRules.Decide): king == null -> no writes; else the remembered index is tested again in this
//     frame (getDyn + cast + Std.is, statically typed, the same HL calls) -> yes; else the negative memo (array id,
//     length, track counter) -> no; else a full typed scan with early exit.
//   yes -> the marker writes ONCE (the release repeats identical raw field writes once per king tracker).
// A positive answer is never taken from memory (skeptic 7); the negative memo is the only exact-if part: trackers are
// pushed only inside MiniMap.track, whose mod handler ModEntry.Hook_MiniMap_track gets a PREFIX (Priority.First) that
// bumps the counter before anything else (skeptic 4). It is used only while the hook manager of f@3307 holds exactly
// that one handler, and it is switched off for the session when the same array is seen to grow without a bump.
//
// Fallback (skeptic 2): a fault of the fast path after orig ran runs the release body with a no-op orig delegate
// instead (the release method itself, called with a bypass flag so this prefix lets it through: same IL, IL_0000-
// IL_0006 then call a delegate that does nothing, so native postUpdate never runs twice). If that body runs clean the
// part is switched off for the session; if it throws too, its exception goes on to the game exactly as without
// CoopOpt (the original throws there as well). Once off, the prefix returns true at once and the original runs once.
// A prefix of someone else that already replaced the call (__runOriginal false, e.g. one patched in after install,
// before the 2-s check) is respected: this prefix then returns false at once without calling orig, and the part
// switches itself off.
//
// Validation (ChainCache model, skeptic 3/5): the first 4096 loop frames, then every 256th (COOP_OPT_SHADOW=all: every
// one) are shadow frames; the negative-memo route has warm-ups of its own (every such frame until 4096 were compared,
// and the first 64 uses of every new negative memo and after every king null -> non-null transition; see
// OptMinimapRules.ShadowSampler). In a shadow frame the fast path decides and plans its writes without applying them;
// the markers' x / y are snapshotted and set to a sentinel; the release body runs (no-op orig); every marker is
// compared bit for bit (written by both with the same values, or by neither); every coordinate still holding the
// sentinel gets its old value back, so the frame ends in exactly the release's state. The decision is also compared
// with a fresh full scan. One difference (or a release exception the fast path would have hidden) switches the part
// off for the session. The sequencing (decision, plain path, shadow frame, fallback, bypass flag) is
// OptMinimapRules.MinimapState, run by the managed checks and the mutants; this file supplies the release's calls.
//
// Install (one transaction, Unpatch rollback): pins of the mod / hlboot (OptCore) and of the DCCM files the part reads
// through, IL pins of the two patched bodies and GetPrimaryClient, exact signatures, shapes of the generated proxy
// members used, no foreign Harmony patch on either target, exactly one prefix of ours on each. Every 2 s the timer
// re-checks both (a foreign or missing patch switches the part off). Calls off DCCM's main thread run the original.
// Switches: COOP_OPT_MINIMAP_DISABLE=1 / COOP_OPT_DISABLE=1 (CoopOpt.cs: nothing is patched),
// COOP_OPT_MINIMAP_MEMO_DISABLE=1 (no negative memo), COOP_OPT_SHADOW=all, COOP_OPT_STATS=1 (a "[CoopOpt] minimap"
// line every 5 s; timed / origUs / restUs / restB cover plain frames only, i.e. neither shadow nor fallback frames,
// which are timed apart as mineUs / releaseUs). Coexistence: no installed add-on patches or hooks these methods (CoopSyncFixes, CoopLoadFix,
// CoopFrameFix, CoopPerfFix sources: no minimap reference); CoopLoadFix's chain cache sees at most one refresh when
// the patch moves the handler's entry; the prefix never stores or compares orig.
public static class OptMinimap
{
    public const string Id="CoopOpt.Minimap.20261111";
    // DCCM files this part reads through (same pins as CoopLoadFix ChainCache and OptHost).
    const string HashlinkSharpPin="207FC8DD54D8A0A804E4051F90D2375F2C58CF6E69EFC26EF3E7EC502DFE8B63";
    const string ModCorePin="630FBBCEDFE8B2E66C5F75234BE80C2EC42FB178C30706E7398583D9FA1A14AE";
    const string HarmonyPin="70E7647876B6C3D874EA99B56627A95D1DCE2E1DD791981A002578A2FEBC9BBD";
    const string HaxeProxyPin="0DF9697335FFDBA4461E599CEE063BC41E4B76C881CCAEDB08AE16DC4A27A8B2";
    const int StateOff=0,StateOn=1;

    static readonly object InstallSync=new();
    static volatile bool _installed;
    static int State;static string Reason="not installed";
    static bool ShadowAll,Stats,MemoSwitchOff;
    static Harmony Owner;
    static MethodInfo PostUpdateMethod,TrackMethod,PrimaryMethod;
    static Func<GhostKing> PrimaryClient;
    static Func<Minimapreveal,Dictionary<int,Graphics>> KingMarkers;
    // dc.ui.hud.Hook_MiniMap.orig_postUpdate / orig_track are resolved by reflection (CS0012, see OptMinimapRules).
    static Type OrigPostUpdateType,OrigTrackType;
    static Action<object,MiniMap> InvokeOrig;                  // orig.Invoke(self)
    static Action<Minimapreveal,object,MiniMap> ReleaseBody;   // the handler method itself, entered with R.InRelease set
    static object NoopOrig;                                    // an orig_postUpdate that does nothing
    static void NoOrig(MiniMap self){}
    static readonly OptFaultGate Gate=new("OptMinimap");

    // ---- game-thread state ----
    static long TrackVersion;                       // Interlocked (prefix on the mod's track handler)
    static readonly R.MinimapState<Graphics> St=new();   // memo, plan, shadow frame, sampler, loop frame count
    static bool MemoAllowed,MemoDecided;
    static string MemoReason="not checked yet";
    static List<Delegate> TrackHooks;static Delegate TrackHandler;
    static int TrkMin=int.MaxValue,TrkMax=-1;

    enum C{Calls,NoCircle,Loop,KingNull,IndexHit,NegMemo,ScanFound,ScanNone,Tested,Writes,RIndexMiss,RNoMemo,RMemoOff,RId,RLength,RVersion,
        TrkSum,Shadows,ShNeg,ShadowOk,Mismatches,Faults,BothThrew,OffThread,ForeignSkip,Timed,OrigTicks,RestTicks,RestBytes,ShTimed,MineTicks,MineBytes,RelTicks,RelBytes}
    static readonly R.CounterSet K=new(Enum.GetNames(typeof(C)));
    static void Inc(C c)=>K.V[(int)c]++;
    static void Add(C c,long v)=>K.V[(int)c]+=v;

    public static void Install()
    {
        lock(InstallSync)
        {
            if(_installed)return;
            OptCore.CheckPins();
            ShadowAll=R.ShadowAllMode(Environment.GetEnvironmentVariable("COOP_OPT_SHADOW"));
            Stats=R.SwitchSet(Environment.GetEnvironmentVariable("COOP_OPT_STATS"));
            MemoSwitchOff=R.SwitchSet(Environment.GetEnvironmentVariable(R.MemoSwitch));
            var harmony=new Harmony(Id);var patched=new List<MethodBase>();
            try
            {
                Bind();
                foreach(var m in new[]{TrackMethod,PostUpdateMethod}){var f=Foreign(m);if(f!=null)throw new InvalidOperationException(f);}
                patched.Add(TrackMethod);      // before Patch: a throw half way is still rolled back (Unpatch by owner)
                harmony.Patch(TrackMethod,prefix:new HarmonyMethod(Own(nameof(BeforeTrack))){priority=Priority.First});
                patched.Add(PostUpdateMethod);
                harmony.Patch(PostUpdateMethod,prefix:new HarmonyMethod(Own(nameof(BeforePostUpdate))){priority=Priority.Last});
                foreach(var m in patched){var r=PatchProblem(m);if(r!=null)throw new InvalidOperationException(r);}
                // The release body for the fallback and the shadow: the patched method itself, entered with R.InRelease set.
                ReleaseBody=R.BodyCaller<Minimapreveal,MiniMap>(PostUpdateMethod,OrigPostUpdateType);
                Owner=harmony;
                Reason="on";Volatile.Write(ref State,StateOn);
                Ticker=new Timer(OnTick,null,1000,1000);
                _installed=true;
                OptCore.Info("minimap installed: prefix on Minimapreveal.Hook_MiniMap_postUpdate (Priority.Last), counter prefix on ModEntry.Hook_MiniMap_track (Priority.First); shadow={Shadow}; stats={Stats}; memo={Memo}",
                    ShadowAll?"all":"first 4096 loop frames, then 1/256",Stats?"5 s":"off",MemoSwitchOff?"switched off by "+R.MemoSwitch+"=1":"checked on the first loop frame");
            }
            catch
            {
                Volatile.Write(ref State,StateOff);Reason="not installed";
                foreach(var t in patched)try{harmony.Unpatch(t,HarmonyPatchType.All,Id);}catch(Exception r){OptCore.Warn("minimap rollback of {Target} failed: {Error}",t.Name,r.Message);}
                throw;
            }
        }
    }

    // ---- binding (install time: reflection only, no game state is read) ----
    static void Bind()
    {
        PinFile(typeof(Hashlink.Marshaling.HashlinkMarshal).Assembly,HashlinkSharpPin);
        PinFile(typeof(ModCore.Mods.ModBase).Assembly,ModCorePin);
        PinFile(typeof(Harmony).Assembly,HarmonyPin);
        PinFile(typeof(HaxeProxy.Runtime.HaxeProxyBase).Assembly,HaxeProxyPin);
        var reveal=typeof(Minimapreveal);
        if(reveal.BaseType!=typeof(object))throw new InvalidOperationException("Minimapreveal is not a plain managed class");
        if(typeof(ModEntry).BaseType!=typeof(ModCore.Mods.ModBase))throw new InvalidOperationException("ModEntry base changed");
        var proxy=typeof(MiniMap).Assembly;
        OrigPostUpdateType=proxy.GetType("dc.ui.hud.Hook_MiniMap+orig_postUpdate",true);
        OrigTrackType=proxy.GetType("dc.ui.hud.Hook_MiniMap+orig_track",true);
        var origInit=proxy.GetType("dc.ui.hud.Hook_MiniMap+orig_initContainers",true);
        PostUpdateMethod=OptCore.Method(reveal,"Hook_MiniMap_postUpdate",false,typeof(void),OrigPostUpdateType,typeof(MiniMap));
        TrackMethod=OptCore.Method(typeof(ModEntry),"Hook_MiniMap_track",false,typeof(void),OrigTrackType,typeof(MiniMap),typeof(dc.Entity),
            typeof(int?),typeof(dc.String),typeof(int?),typeof(bool?),typeof(dc.h2d.Tile),typeof(dc.String),typeof(dc.String));
        PrimaryMethod=OptCore.Method(typeof(ModEntry),"GetPrimaryClient",true,typeof(GhostKing));
        var init=OptCore.Method(reveal,"Hook_MiniMap_initContainers",false,typeof(void),origInit,typeof(MiniMap),typeof(dc.haxe.io.Bytes));
        InvokeOrig=R.Invoker<MiniMap>(OrigPostUpdateType);
        NoopOrig=R.Noop<MiniMap>(OrigPostUpdateType,typeof(OptMinimap).GetMethod(nameof(NoOrig),BindingFlags.Static|BindingFlags.NonPublic));
        foreach(var m in new[]{PostUpdateMethod,TrackMethod,PrimaryMethod,init})
        {
            if(!R.TryPin(m.Name,out int len,out string sha)||!R.PinMatches(m.GetMethodBody()?.GetILAsByteArray(),len,sha))
                throw new InvalidOperationException("pinned IL changed: "+m.DeclaringType?.Name+"."+m.Name);
        }
        var kc=OptCore.Field(reveal,"kingcircle",typeof(Graphics),false);
        if(!kc.IsPublic)throw new InvalidOperationException("Minimapreveal.kingcircle changed");
        var km=OptCore.Field(reveal,"_kingMarkers",typeof(Dictionary<int,Graphics>),false);
        var clients=OptCore.Field(typeof(ModEntry),"clients",typeof(GhostKing[]),true);
        if(!clients.IsPublic)throw new InvalidOperationException("ModEntry.clients changed");
        KingMarkers=FieldReader(km);
        PrimaryClient=OptCore.Delegate<Func<GhostKing>>(PrimaryMethod);
        // The generated proxy members the fast path calls are the release's: plain field reads / writes, the class
        // globals, and ArrayObj.getDyn = HL f@71 reached through the virtual ArrayAccess.getDyn.
        Getter(typeof(MiniMap),"get_trackers","trackers","GetFieldById",typeof(ArrayObj));
        Getter(typeof(dc.hl.types.ArrayBase),"get_length","length","GetValueFieldById",typeof(int));
        Getter(typeof(MMTracker),"get_e","e","GetFieldById",typeof(dc.Entity));
        Getter(typeof(dc._Std),"get_is","is","GetFieldById",typeof(HaxeProxy.Runtime.HlFunc<bool,object,object>));
        Getter(typeof(dc.Entity),"get_cx","cx","GetValueFieldById",typeof(int));
        Getter(typeof(dc.Entity),"get_cy","cy","GetValueFieldById",typeof(int));
        Getter(typeof(dc.h2d.Object),"get_x","x","GetValueFieldById",typeof(double));
        Getter(typeof(dc.h2d.Object),"get_y","y","GetValueFieldById",typeof(double));
        Setter(typeof(dc.h2d.Object),"set_x","x",typeof(double));
        Setter(typeof(dc.h2d.Object),"set_y","y",typeof(double));
        Global(typeof(dc.Std),R.StdGlobal);
        Global(typeof(dc.en.KingSkin),R.KingSkinGlobal);
        var getDyn=typeof(ArrayObj).GetMethod("getDyn",OptCore.I,null,new[]{typeof(int)},null);
        var baseGetDyn=typeof(dc.hl.types.ArrayAccess).GetMethod("getDyn",OptCore.I,null,new[]{typeof(int)},null);
        if(getDyn==null||baseGetDyn==null||!getDyn.IsVirtual||!baseGetDyn.IsVirtual||getDyn.Attributes.HasFlag(MethodAttributes.NewSlot)||!Same(getDyn.GetBaseDefinition(),baseGetDyn)
           ||getDyn.ReturnType!=typeof(object)||!R.StartsWithFunction(getDyn.GetMethodBody()?.GetILAsByteArray(),R.ArrayObjGetDyn))
            throw new InvalidOperationException("ArrayObj.getDyn changed");
        if(typeof(Graphics).IsAssignableTo(typeof(dc.h2d.Object))==false||typeof(GhostKing).IsAssignableTo(typeof(dc.en.KingSkin))==false)throw new InvalidOperationException("proxy hierarchy changed");
        foreach(var (name,findex) in new[]{("add_track",R.TrackFunction),("add_postUpdate",R.PostUpdateFunction)})
        {
            var add=typeof(HookMiniMap).GetMethod(name,OptCore.S);
            if(add==null||!R.StartsWithFunction(add.GetMethodBody()?.GetILAsByteArray(),findex))throw new InvalidOperationException("Hook_MiniMap."+name+" changed");
        }
        BindHookManagers();
    }
    static void PinFile(Assembly a,string pin)
    {
        if(string.IsNullOrEmpty(a.Location)||global::CoopPackRuntime.PackPins.Sha256(a.Location)!=pin)throw new InvalidOperationException("pinned file changed: "+a.GetName().Name);
    }
    static void Getter(Type owner,string name,string field,string helper,Type ret)
    {
        var m=owner.GetMethod(name,OptCore.I,null,Type.EmptyTypes,null);
        var il=m?.GetMethodBody()?.GetILAsByteArray();
        if(m==null||m.IsVirtual||m.ReturnType!=ret||!R.FieldGetterShape(il)||m.Module.ResolveString(R.Int32At(il,R.GetterStringAt))!=field
           ||m.Module.ResolveMethod(R.Int32At(il,R.GetterHelperAt)) is not MethodBase h||h.Name!=helper||h.DeclaringType!=typeof(HaxeProxy.Runtime.Internals.HaxeProxyHelper))
            throw new InvalidOperationException("proxy getter changed: "+owner.Name+"."+name);
    }
    static void Setter(Type owner,string name,string field,Type arg)
    {
        var m=owner.GetMethod(name,OptCore.I,null,new[]{arg},null);
        var il=m?.GetMethodBody()?.GetILAsByteArray();
        if(m==null||m.IsVirtual||!R.FieldSetterShape(il)||m.Module.ResolveString(R.Int32At(il,R.SetterStringAt))!=field
           ||m.Module.ResolveMethod(R.Int32At(il,R.SetterHelperAt)) is not MethodBase h||h.Name!="SetValueFieldById"||h.DeclaringType!=typeof(HaxeProxy.Runtime.Internals.HaxeProxyHelper))
            throw new InvalidOperationException("proxy setter changed: "+owner.Name+"."+name);
    }
    static void Global(Type owner,int global)
    {
        var m=owner.GetMethod("get_Class",OptCore.S,null,Type.EmptyTypes,null);
        if(m==null||!R.GlobalGetterShape(m.GetMethodBody()?.GetILAsByteArray(),global))throw new InvalidOperationException("proxy class global changed: "+owner.Name);
    }
    static Func<Minimapreveal,Dictionary<int,Graphics>> FieldReader(FieldInfo f)
    {
        var dm=new DynamicMethod("CoopOpt_Minimap_Read_"+f.Name,typeof(Dictionary<int,Graphics>),new[]{typeof(Minimapreveal)},typeof(OptMinimap).Module,true);
        var il=dm.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,f);il.Emit(OpCodes.Ret);
        return (Func<Minimapreveal,Dictionary<int,Graphics>>)dm.CreateDelegate(typeof(Func<Minimapreveal,Dictionary<int,Graphics>>));
    }
    static MethodInfo Own(string name)=>typeof(OptMinimap).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public)??throw new MissingMethodException("OptMinimap",name);
    static IEnumerable<Patch> AllPatches(Patches i)=>i.Prefixes.Concat(i.Postfixes).Concat(i.Transpilers).Concat(i.Finalizers).Concat(i.ILManipulators);
    // Null when every patch on m is ours (or there is none); else who patches it.
    static string Foreign(MethodBase m)
    {
        var info=Harmony.GetPatchInfo(m);
        if(info==null)return null;
        var all=AllPatches(info).ToList();
        if(R.OnlyOwner(all.Select(p=>p.owner),Id))return null;
        var p0=all.First(p=>p.owner!=Id);
        return "foreign patch by "+p0.owner+" ("+p0.PatchMethod?.DeclaringType?.Name+"."+p0.PatchMethod?.Name+") on "+m.DeclaringType?.Name+"."+m.Name;
    }
    // Null when m carries exactly our one prefix and nothing else.
    static string PatchProblem(MethodBase m)
    {
        var f=Foreign(m);if(f!=null)return f;
        var info=Harmony.GetPatchInfo(m);
        int Ours(IEnumerable<Patch> ps)=>ps.Count(x=>x.owner==Id);
        if(info==null||!R.ExactlyOnce(Ours(info.Prefixes),Ours(info.Postfixes),Ours(info.Transpilers),Ours(info.Finalizers))||Ours(info.ILManipulators)!=0)
            return "minimap patch not installed exactly once on "+m.DeclaringType?.Name+"."+m.Name;
        return null;
    }

    // ---- the patches ----
    // ModEntry.Hook_MiniMap_track: every push into MiniMap.trackers goes through it; counted before anything else.
    static void BeforeTrack(){Interlocked.Increment(ref TrackVersion);}

    // Positional __0 / __1 (types checked at install; __0 is the orig_postUpdate delegate, taken as object).
    // __runOriginal false: a prefix that ran before this one already replaced the call (it called orig itself or
    // suppressed it); orig must not run a second time, so this prefix returns false at once.
    static bool BeforePostUpdate(Minimapreveal __instance,object __0,MiniMap __1,bool __runOriginal)
    {
        if(!__runOriginal){ForeignTookOver();return false;}      // as without CoopOpt: the other prefix's call stands
        if(R.InRelease)return true;                               // the release body, run by this part itself
        if(Volatile.Read(ref State)!=StateOn)return true;         // off: the original runs once
        if(!OnGameThread()){Interlocked.Increment(ref K.V[(int)C.OffThread]);return true;}
        Inc(C.Calls);
        long t0=0,t1=0,b1=0;
        if(Stats){t0=Stopwatch.GetTimestamp();}
        InvokeOrig(__0,__1);                                      // IL_0000-IL_0006, as the release: not guarded
        if(Stats){t1=Stopwatch.GetTimestamp();b1=GC.GetAllocatedBytesForCurrentThread();}
        Exception pass=null;bool plain=true;
        try{pass=Body(__instance,__1,ref plain);if(pass==null)Gate.Ok();}
        catch(Exception e){plain=false;pass=Fault(__instance,__1,e);}
        // Timed: plain frames only (no shadow, no fallback), so restUs / restB are the fast path's own cost.
        if(Stats&&plain){long t2=Stopwatch.GetTimestamp(),b2=GC.GetAllocatedBytesForCurrentThread();Inc(C.Timed);Add(C.OrigTicks,t1-t0);Add(C.RestTicks,t2-t1);Add(C.RestBytes,b2-b1);}
        if(pass!=null)ExceptionDispatchInfo.Capture(pass).Throw();   // the release body's own exception (see Fault / shadow)
        return false;
    }
    static void ForeignTookOver()
    {
        Interlocked.Increment(ref K.V[(int)C.ForeignSkip]);
        if(Volatile.Read(ref State)==StateOn)SwitchOff("another prefix on Minimapreveal.Hook_MiniMap_postUpdate replaced the call before this part's (__runOriginal false)");
    }

    // After orig. Returns an exception of the release body that must reach the game (shadow frames only), else null.
    // plain is set false for a shadow frame.
    static Exception Body(Minimapreveal inst,MiniMap self,ref bool plain)
    {
        var kmap=inst.kingcircle;                                  // IL_0007
        if(kmap is null){Inc(C.NoCircle);return null;}            // IL_000F brfalse (reference test, as every null test here)
        bool king=PrimaryClient() is not null;                     // IL_0014, IL_011F brfalse
        var trackers=self.trackers;                                // IL_001B
        int n=trackers.length;                                     // IL_0022
        long id=trackers.HashlinkPointer,version=Volatile.Read(ref TrackVersion);
        bool unseen=St.Observe(id,n,version,out long frame);
        Inc(C.Loop);Add(C.TrkSum,n);
        if(n<TrkMin)TrkMin=n;if(n>TrkMax)TrkMax=n;
        if(unseen&&MemoAllowed)MemoOff("the tracker array grew without a MiniMap.track call through the mod's handler");
        if(R.GuardDue(frame))GuardTrack();
        var t=new KingTrackers(trackers);var src=new PlanSource(inst);var acc=new MarkerAccess();var rel=new ReleaseCall(inst,self);
        var r=St.Run(king,n,id,version,MemoUsable(),ShadowAll,Stats,ref t,ref src,ref acc,ref rel);
        Count(r.Route,r.Why,r.Tested);
        if(!r.Shadowed){Add(C.Writes,r.Writes);return null;}
        plain=false;
        Inc(C.Shadows);if(r.Route==R.Route.NegativeMemo)Inc(C.ShNeg);
        if(Stats){Inc(C.ShTimed);Add(C.MineTicks,r.MineTicks);Add(C.MineBytes,r.MineBytes);Add(C.RelTicks,r.ReleaseTicks);Add(C.RelBytes,r.ReleaseBytes);}
        if(r.ShadowOk)Inc(C.ShadowOk);
        else
        {
            Inc(C.Mismatches);
            SwitchOff(string.Create(CultureInfo.InvariantCulture,
                $"shadow comparison differs at loop frame {frame}: route={R.RouteName(r.Route)} decided={r.Any} fullScan={r.Full}@{r.FullIndex} king={king} trackers={n} flagOk={r.FlagOk} markersOk={r.MarkersOk} planCovered={r.Covered} releaseWrote={r.Theirs} releaseThrew={(r.ReleaseError==null?"no":r.ReleaseError.GetType().Name)}"));
        }
        return r.ReleaseError;                                     // the release's own exception reaches the game
    }

    // A fault after orig: the release body runs instead, so this frame has the release's result.
    static Exception Fault(Minimapreveal inst,MiniMap self,Exception e)
    {
        Inc(C.Faults);
        var rel=new ReleaseCall(inst,self);
        var releaseError=St.Fault(ref rel,out var action);
        if(action==R.FaultAction.SwitchOff)
        {
            SwitchOff("fault after orig, the release body ran instead: "+e.GetBaseException().GetType().Name+": "+e.GetBaseException().Message);
            return null;
        }
        Inc(C.BothThrew);
        if(Gate.Fail(releaseError))SwitchOff("the release body itself keeps throwing: "+releaseError.GetBaseException().Message);
        return releaseError;
    }

    static void SwitchOff(string reason)
    {
        if(Interlocked.Exchange(ref State,StateOff)==StateOff)return;
        Reason=reason;
        OptCore.Warn("minimap switched off for this session, the original code is in effect: {Reason}",reason);
    }
    static void Count(R.Route route,R.Rescan why,int tested)
    {
        Add(C.Tested,tested);
        switch(route)
        {
            case R.Route.KingNull:Inc(C.KingNull);break;
            case R.Route.IndexHit:Inc(C.IndexHit);break;
            case R.Route.NegativeMemo:Inc(C.NegMemo);break;
            case R.Route.ScanFound:Inc(C.ScanFound);break;
            default:Inc(C.ScanNone);break;
        }
        switch(why)
        {
            case R.Rescan.IndexMiss:Inc(C.RIndexMiss);break;
            case R.Rescan.NoMemo:Inc(C.RNoMemo);break;
            case R.Rescan.MemoOff:Inc(C.RMemoOff);break;
            case R.Rescan.IdChanged:Inc(C.RId);break;
            case R.Rescan.LengthChanged:Inc(C.RLength);break;
            case R.Rescan.VersionChanged:Inc(C.RVersion);break;
        }
    }

    // ---- the release's per-tracker test, statically typed (same HL calls, same order) ----
    struct KingTrackers:R.IKingTrackers
    {
        readonly ArrayObj _a;
        public KingTrackers(ArrayObj a){_a=a;}
        public bool IsKingAt(int i)
        {
            object e=_a.getDyn(i);                                   // IL_0034 callvirt ArrayAccess::getDyn -> ArrayObj.getDyn (f@71)
            if(e is null)return false;                               // IL_00D6 IsTrue(Equal(e, null)): reference test (no operator)
            var mm=(MMTracker)e;                                     // IL_0118 Convert: identity conversion of an MMTracker proxy
            return dc.Std.Class.@is.Invoke(mm.e,dc.en.KingSkin.Class); // IL_0124-IL_0139, same evaluation order
        }
    }
    struct PlanSource:R.IPlanSource<Graphics>
    {
        readonly Minimapreveal _i;GhostKing[] _c;Dictionary<int,Graphics> _m;
        public PlanSource(Minimapreveal inst){_i=inst;_c=null;_m=null;}
        public void Open(){_c=ModEntry.clients;_m=KingMarkers(_i);}  // IL_0140 ldsfld clients, IL_0158 ldfld _kingMarkers (ctor-only field)
        public int ClientCount=>_c.Length;
        public bool HasClient(int k)=>_c[k] is not null;            // IL_0155 brfalse
        public bool TryMarker(int k,out Graphics m)=>_m.TryGetValue(k,out m);
        public double X(int k)=>_c[k].cx;                            // IL_016C get_cx, conv.r8
        public double Y(int k)=>_c[k].cy;                            // IL_017B get_cy, conv.r8
        public Dictionary<int,Graphics> Markers()=>KingMarkers(_i);  // the shadow frame's snapshot
    }
    struct ReleaseCall:R.IReleaseBody
    {
        readonly Minimapreveal _i;readonly MiniMap _s;
        public ReleaseCall(Minimapreveal inst,MiniMap self){_i=inst;_s=self;}
        public void Run()=>ReleaseBody(_i,NoopOrig,_s);             // the handler itself: IL_0000-IL_0006 call the no-op orig
    }
    struct MarkerAccess:R.IMarkerAccess<Graphics>
    {
        public double X(Graphics m)=>m.x;
        public double Y(Graphics m)=>m.y;
        public void SetX(Graphics m,double v)=>m.x=v;                // IL_0172 Object::set_x (raw field write)
        public void SetY(Graphics m,double v)=>m.y=v;                // IL_0181 Object::set_y
    }

    // ---- negative memo guard: the hook manager of MiniMap.track (f@3307) holds exactly the mod's handler ----
    static FieldInfo ManagersField,MgrHooks,MgrFunction;
    static void BindHookManagers()
    {
        var f=typeof(ModCore.Modules.HashlinkHooks).GetField("managers",OptCore.I)??throw new MissingFieldException("HashlinkHooks","managers");
        var t=f.FieldType;
        if(!t.IsGenericType||t.GetGenericTypeDefinition()!=typeof(Dictionary<,>)||t.GetGenericArguments()[0]!=typeof(IntPtr)||t.GetGenericArguments()[1].FullName!="ModCore.Hooks.HashlinkHookManager")
            throw new InvalidOperationException("pinned HashlinkHooks.managers changed");
        var mgr=t.GetGenericArguments()[1];
        MgrHooks=OptCore.Field(mgr,"hooks",typeof(List<Delegate>),false);
        MgrFunction=OptCore.Field(mgr,"function",typeof(Hashlink.Reflection.Members.HashlinkFunction),false);
        foreach(var x in new[]{f,MgrHooks,MgrFunction})if(!x.IsInitOnly)throw new InvalidOperationException("pinned hook manager field is not readonly: "+x.Name);
        ManagersField=f;
    }
    static bool Same(MethodInfo a,MethodInfo b)=>a!=null&&b!=null&&a is not DynamicMethod&&b is not DynamicMethod&&a.MetadataToken==b.MetadataToken&&a.Module==b.Module;   // a DynamicMethod (another module's hook adapter, e.g. a Boot.render tint) has no metadata token (MetadataToken throws) and is never a compiled method of the mod
    // Game thread, first loop frame and every 1024th.
    static void GuardTrack()
    {
        if(MemoDecided&&!MemoAllowed)return;
        try
        {
            var managers=(IDictionary)ManagersField.GetValue(ModCore.Modules.HashlinkHooks.Instance);
            List<Delegate> trackList=null;Delegate trackDel=null;int trackF=-1,postHandlers=-1,postF=-1;
            foreach(var mgr in managers.Values)
            {
                var hooks=(List<Delegate>)MgrHooks.GetValue(mgr);
                foreach(var d in hooks)
                {
                    if(Same(d.Method,TrackMethod)){trackList=hooks;trackDel=d;trackF=((Hashlink.Reflection.Members.HashlinkFunction)MgrFunction.GetValue(mgr)).FunctionIndex;}
                    else if(Same(d.Method,PostUpdateMethod)){postHandlers=hooks.Count;postF=((Hashlink.Reflection.Members.HashlinkFunction)MgrFunction.GetValue(mgr)).FunctionIndex;}
                }
            }
            bool sole=trackList!=null&&R.TrackHandlerSole(trackList.Count,trackDel.Target is ModEntry,trackF);
            if(!MemoDecided)
            {
                MemoDecided=true;
                OptCore.Info("minimap hook managers: MiniMap.track f@{TrackF} handlers={TrackN} (mod's: {Mod}); MiniMap.postUpdate f@{PostF} handlers={PostN}",
                    trackF,trackList?.Count??0,trackDel!=null,postF,postHandlers);
                if(MemoSwitchOff){MemoAllowed=false;MemoReason="switched off by "+R.MemoSwitch+"=1";return;}
                if(!sole){MemoOff("MiniMap.track does not have exactly the mod's handler");return;}
                TrackHooks=trackList;TrackHandler=trackDel;MemoAllowed=true;MemoReason="on";
                OptCore.Info("minimap negative memo on");
                return;
            }
            if(!sole||!ReferenceEquals(trackList,TrackHooks))MemoOff("the handlers of MiniMap.track changed");
        }
        catch(Exception e){MemoDecided=true;MemoOff("hook manager check failed: "+e.GetBaseException().Message);}
    }
    // Every use of the negative memo: the cached handler list still holds exactly the mod's handler.
    static bool MemoUsable()
    {
        if(!MemoAllowed)return false;
        var list=TrackHooks;
        if(list!=null&&list.Count==1&&ReferenceEquals(list[0],TrackHandler))return true;
        MemoOff("the handlers of MiniMap.track changed");
        return false;
    }
    // Called at most once per session (every caller first checks MemoAllowed or that the memo is still undecided).
    static void MemoOff(string reason)
    {
        MemoAllowed=false;MemoDecided=true;MemoReason=reason;
        St.Memo.NegValid=false;
        OptCore.Info("minimap negative memo off for this session: {Reason}",reason);
    }

    // ---- the game thread (DCCM's ModCore.Core.MainThread, read on first use) ----
    static int GameThread;
    static bool OnGameThread()
    {
        int g=GameThread;
        if(g==0)g=ResolveGameThread();
        return g>0&&Environment.CurrentManagedThreadId==g;
    }
    static int ResolveGameThread()
    {
        lock(InstallSync)
        {
            if(GameThread!=0)return GameThread;
            try
            {
                var core=typeof(ModCore.Mods.ModBase).Assembly.GetType("ModCore.Core",true);
                var p=core.GetProperty("MainThread",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
                if(p==null||p.PropertyType!=typeof(Thread))throw new InvalidOperationException("ModCore.Core.MainThread changed");
                var t=(Thread)p.GetValue(null)??throw new InvalidOperationException("ModCore.Core.MainThread is null");
                GameThread=t.ManagedThreadId;
            }
            catch(Exception e)
            {
                GameThread=-1;
                SwitchOff("the game thread is not known ("+e.GetBaseException().Message+")");
            }
            return GameThread;
        }
    }

    // ---- timer: patch checks every 2 s, stats every 5 s, one hint after 3 minutes ----
    static Timer Ticker;static long TickCount;
    static void OnTick(object _)
    {
        try
        {
            long n=Interlocked.Increment(ref TickCount);
            if(R.Due(n,R.VerifyPeriodSeconds)&&Volatile.Read(ref State)==StateOn)Verify();
            if(Stats&&R.Due(n,R.StatsPeriodSeconds))Report();
            if(n==R.QuietHintSeconds)QuietHint();
        }
        catch{}
    }
    static void Verify()
    {
        foreach(var m in new MethodBase[]{TrackMethod,PostUpdateMethod})
        {
            string r;
            try{r=PatchProblem(m);}catch(Exception e){r="patch check failed: "+e.GetBaseException().Message;}
            if(r!=null){SwitchOff(r);return;}
        }
    }
    static long _lastVersion;
    // One line per 5 s; every count is the change in that window (timer thread: reads counters only).
    static void Report()
    {
        try
        {
            var w=new long[K.Names.Length];
            for(int i=0;i<w.Length;i++)w[i]=K.Delta(i);
            long V(C c)=>w[(int)c];
            double us=1e6/Stopwatch.Frequency;
            long ver=Interlocked.Read(ref TrackVersion),bumps=ver-_lastVersion;_lastVersion=ver;
            int min=Interlocked.Exchange(ref TrkMin,int.MaxValue),max=Interlocked.Exchange(ref TrkMax,-1);
            long loops=V(C.Loop),timed=V(C.Timed),sh=V(C.ShTimed);
            bool on=Volatile.Read(ref State)==StateOn;
            var ic=CultureInfo.InvariantCulture;
            string text=string.Create(ic,
                $"state={(on?"on":"off")} calls={V(C.Calls)} noCircle={V(C.NoCircle)} loop={loops} kingNull={V(C.KingNull)} indexHit={V(C.IndexHit)} negMemo={V(C.NegMemo)} scanFound={V(C.ScanFound)} scanNone={V(C.ScanNone)} tested={V(C.Tested)} writes={V(C.Writes)}")
                +string.Create(ic,
                $" rescan=indexMiss:{V(C.RIndexMiss)},noMemo:{V(C.RNoMemo)},memoOff:{V(C.RMemoOff)},id:{V(C.RId)},length:{V(C.RLength)},version:{V(C.RVersion)} trackBumps={bumps}")
                +string.Create(ic,
                $" trackers={(max<0?"-":min.ToString(ic)+"/"+R.PerCall(V(C.TrkSum),loops,1).ToString("0.0",ic)+"/"+max.ToString(ic))} kingIndex={St.Memo.Index} memo={(MemoAllowed?"on":"off")}")
                +string.Create(ic,
                $" shadows={V(C.Shadows)} shadowsNegMemo={V(C.ShNeg)} shadowOk={V(C.ShadowOk)} mismatches={V(C.Mismatches)} faults={V(C.Faults)} bothThrew={V(C.BothThrew)} offThread={V(C.OffThread)} foreignSkip={V(C.ForeignSkip)} loopTotal={K.Total((int)C.Loop)} negMemoComparedTotal={St.Sampler.NegShadows}")
                +(timed>0?string.Create(ic,$" timed={timed} origUs={R.PerCall(V(C.OrigTicks),timed,us):0.00} restUs={R.PerCall(V(C.RestTicks),timed,us):0.00} restB={R.PerCall(V(C.RestBytes),timed,1):0}"):"")
                +(sh>0?string.Create(ic,$" shadowTimed={sh} mineUs={R.PerCall(V(C.MineTicks),sh,us):0.00} mineB={R.PerCall(V(C.MineBytes),sh,1):0} releaseUs={R.PerCall(V(C.RelTicks),sh,us):0.00} releaseB={R.PerCall(V(C.RelBytes),sh,1):0}"):"")
                +(on?"":" reason="+Reason)+(MemoAllowed?"":" memoReason="+MemoReason);
            OptCore.Info("minimap {Line:l}",text);
        }
        catch{}
    }
    static void QuietHint()
    {
        if(Volatile.Read(ref State)!=StateOn){OptCore.Warn("minimap is not active after 3 minutes: {Reason}",Reason);return;}
        if(K.Total((int)C.Calls)==0)OptCore.Info("minimap: the mod's MiniMap.postUpdate handler has not run in 3 minutes (Minimapreveal not registered, no minimap yet, or the patch is bypassed)");
    }
}
