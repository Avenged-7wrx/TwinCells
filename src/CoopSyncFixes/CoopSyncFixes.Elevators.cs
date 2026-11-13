using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using dc.en.inter;
using dc.en.inter.elevator;
using dc.pr;

public sealed partial class CoopSyncFixes
{
    // First supported variant: ordinary SimpleElevator. MultiFloor/Purple elevators have
    // additional native state and activation semantics and deliberately keep their base path.
    static readonly List<Delegate> ElevatorNativeHooks=new();
    static long ElevatorRiderCorrections,ElevatorAppliedStates,ElevatorRejectedStates;
    static long ElevatorCallbackFailures;
    static bool ElevatorModuleInitialized;
    static readonly ConditionalWeakTable<Level,ElevatorScan> ElevatorScans=new();
    static long ElevatorEntityScans,ElevatorEntityVisits,ElevatorEntityMutation;
    sealed class ElevatorScan
    {
        public IntPtr Pointer,Entities;public int Build,Length;public long Frame,Mutation;
        public readonly List<Elevator> Values=new();public readonly List<int> Indices=new();public readonly List<string> Tracks=new();
    }
    // Alpha2 U11 (PLAN-ALPHA2.md issue 4, jerky elevator). (A) RIDE: the client tells the host which SimpleElevator it
    // stands on (optional token +csfelev2); while that report is fresh and the host's own lift moves, the host draws the
    // client's avatar with its feet on the host platform (X stays the client's POS). (B) Client STATE deadband plus a
    // per-STATE error histogram. (C) One log line per ride start/stop (any elevator type, so a playtest names the lift).
    // Switches: COOP_SYNC_DISABLE_ELEVATOR_RIDE=1 (A and C off, no +csfelev2), COOP_SYNC_DISABLE_ELEVATOR_DEADBAND=1 (B off,
    // the histogram stays); COOP_SYNC_DISABLE_ELEVATORS=1 still switches the whole module off. Every RIDE body runs in
    // try/catch behind its own fault gate (CoopSyncFixesElevatorRide.FaultLimit faults switch RIDE off, never STATE/ACT).
    static bool ElevatorDeadbandEnabled,ElevatorRideInitialized,ElevatorRideTripped;
    static int ElevatorRideFaults,ElevatorRideLogUsed;
    static long ElevatorDeadbandSkips,ElevatorRideSent,ElevatorRideAccepted,ElevatorRideRejected,ElevatorRidePinnedFrames,ElevatorRideHeldFrames;
    static long ElevatorRideLogWindow,ElevatorRideLogSuppressed;
    // Host diagnostics (silent counters, read by the QA probe): the age of the partner's last RIDE report at the latest
    // presentation frame (-1 while none is on) and the largest such age on a pinned frame.
    static long ElevatorRideReportAgeMs=-1,ElevatorRideMaxReportAgeMs;
    static double ElevatorStateErrorMax,ElevatorStateErrorRideMax;
    static readonly long[] ElevatorStateErrorBuckets=new long[CoopSyncFixesElevatorRide.ErrorBucketCount];
    static MethodInfo ElevatorRideClientIndex,ElevatorRideHeadDirty;
    static PropertyInfo ElevatorRideTransitionActive,ElevatorRideCreationBlocked;
    sealed class ElevatorRideScan {public IntPtr Pointer;public int Build;public long At;public readonly List<Elevator> Values=new();}
    static readonly ConditionalWeakTable<Level,ElevatorRideScan> ElevatorRideScans=new();
    static void InitializeExperimentalElevators(Harmony harmony)
    {
        if(ElevatorModuleInitialized || Disabled("ELEVATORS"))return;
        if(ModEntry._net?.IsAlive==true)throw new InvalidOperationException("Initialize elevator capability before connecting peers");
        ElevatorDeadbandEnabled=!Disabled("ELEVATOR_DEADBAND");
        InitializeElevatorProtocol(harmony);InitializeElevatorSynchronization(harmony);
        InitializeElevatorRide(harmony);
    }
    static void InitializeElevatorSynchronization(Harmony harmony)
    {
        var sync=typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.Interaction.InteractionSync",true);
        var tick=sync.GetMethods(ElevatorInstanceFlags).Single(m=>m.Name.EndsWith("IOnHeroUpdate.OnHeroUpdate",StringComparison.Ordinal));
        harmony.Patch(tick,postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(TryPumpElevators),Flags)));
        foreach(var method in new[]{"TryApplyElevatorRemoteActivation","TryApplyElevatorRemoteState"})
            harmony.Patch(sync.GetMethod(method,ElevatorInstanceFlags|BindingFlags.Static),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(SkipLegacySimpleElevator),Flags)));
        RegisterElevatorNativeHook("dc.en.inter.Hook_Elevator","fixedUpdate",false);
        RegisterElevatorNativeHook("dc.en.inter.elevator.Hook_SimpleElevator","onActivate",true);
        foreach(var name in new[]{"registerEntity","unregisterEntity"})
        {
            var ev=typeof(Level).Assembly.GetType("dc.pr.Hook_Level",true).GetEvent(name);
            var hook=CreateElevatorMembershipHook(ev.EventHandlerType);ElevatorNativeHooks.Add(hook);ev.AddEventHandler(null,hook);
        }
        ElevatorModuleInitialized=true;
    }
    // RIDE install. Pins every base-mod method it patches or calls by IL length + SHA-256 (release 0.5.1 378BE6B0) and
    // resolves its reflection targets before the first patch; any failure leaves RIDE off (no +csfelev2 advertised, so
    // the partner never sends RIDE) while the rest of the elevator module keeps running.
    static void InitializeElevatorRide(Harmony harmony)
    {
        if(ElevatorRideInitialized || !ElevatorModuleInitialized || Disabled("ELEVATOR_RIDE"))return;
        try
        {
            const BindingFlags any=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
            var receive=typeof(ModEntry).GetMethod("ReceiveGhostCoords",any,null,Type.EmptyTypes,null);
            var index=typeof(ModEntry).GetMethod("TryGetClientIndex",any,null,new[]{typeof(int),typeof(int),typeof(int).MakeByRefType()},null);
            var dirty=typeof(ModEntry).GetMethod("MarkGhostHeadDirty",any,null,new[]{typeof(int),typeof(bool)},null);
            var reject=typeof(NetNode).GetMethod("RejectIncompatiblePeer",any,null,new[]{typeof(string),typeof(string)},null);
            var hello=typeof(NetNode).GetMethod("BuildHelloLine",any,null,Type.EmptyTypes,null);
            var welcome=typeof(NetNode).GetMethod("BuildWelcomeLine",any,null,Type.EmptyTypes,null);
            var transition=typeof(ModEntry).GetProperty("IsRemoteKingTransitionActive",any);
            var blocked=typeof(ModEntry).GetProperty("IsRemoteKingCreationBlocked",any);
            if(receive==null || receive.IsStatic || receive.ReturnType!=typeof(void) || index==null || !index.IsStatic || index.ReturnType!=typeof(bool) ||
                dirty==null || !dirty.IsStatic || reject==null || reject.IsStatic || reject.ReturnType!=typeof(bool) || hello==null || welcome==null ||
                hello.ReturnType!=typeof(string) || welcome.ReturnType!=typeof(string) || transition?.PropertyType!=typeof(bool) || blocked?.PropertyType!=typeof(bool) ||
                transition.GetMethod?.IsStatic!=true || blocked.GetMethod?.IsStatic!=true)
                throw new InvalidOperationException("pinned ghost/handshake members changed");
            foreach(var (method,owner,name) in new (MethodBase,string,string)[]{(receive,"ModEntry","ReceiveGhostCoords"),(index,"ModEntry","TryGetClientIndex"),
                (dirty,"ModEntry","MarkGhostHeadDirty"),(reject,"NetNode","RejectIncompatiblePeer"),(hello,"NetNode","BuildHelloLine"),(welcome,"NetNode","BuildWelcomeLine")})
                if(!CoopSyncFixesElevatorRide.PinMatches(owner,name,method.GetMethodBody()?.GetILAsByteArray()))throw new InvalidOperationException("pinned IL changed: "+owner+"."+name);
            ElevatorRideClientIndex=index;ElevatorRideHeadDirty=dirty;ElevatorRideTransitionActive=transition;ElevatorRideCreationBlocked=blocked;
            harmony.Patch(reject,postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ObserveElevatorRideHandshake),Flags)));
            harmony.Patch(receive,postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(AfterReceiveGhostCoords),Flags)));
            // Advertised last: a peer only ever sends RIDE once this postfix ran, so every path above is already in place.
            foreach(var line in new[]{hello,welcome})
                harmony.Patch(line,postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(AdvertiseElevatorRide),Flags)){priority=HarmonyLib.Priority.Last});
            ElevatorRideInitialized=true;
            Serilog.Log.Information("[CoopSyncFixes] Elevator ride presentation ready: token {Token}, fresh {Fresh} ms, stop hold {Hold}-{HoldMax} ms, deadband {Deadband}",
                CoopSyncFixesElevatorRide.Token,CoopSyncFixesElevatorRide.FreshMs,CoopSyncFixesElevatorRide.StopHoldMs,CoopSyncFixesElevatorRide.StopHoldMaxMs,ElevatorDeadbandEnabled?"on":"off");
        }
        catch(Exception error)
        {
            ElevatorRideInitialized=false;
            try{Serilog.Log.Error("[CoopSyncFixes] Elevator ride presentation not installed: {Reason}",error.GetBaseException().Message);}catch{}
        }
    }
    static bool ElevatorRideReady() => ElevatorRideInitialized && ElevatorModuleInitialized && !System.Threading.Volatile.Read(ref ElevatorRideTripped);
    static void ElevatorRideFault(Exception error)
    {
        var count=System.Threading.Interlocked.Increment(ref ElevatorRideFaults);
        try
        {
            if(count<=CoopSyncFixesElevatorRide.FaultLimit)Serilog.Log.Warning(error,"[CoopSyncFixes] Elevator ride presentation fault {Count}",count);
            if(CoopSyncFixesElevatorRide.FaultTrips(count) && !ElevatorRideTripped)
            {
                System.Threading.Volatile.Write(ref ElevatorRideTripped,true);
                Serilog.Log.Error("[CoopSyncFixes] Elevator ride presentation switched off after {Count} faults (STATE/ACT elevator sync unchanged)",count);
            }
        }
        catch{ /* Logging must never break the frame. */ }
    }
    static void LogElevatorRide(string template,params object[] values)
    {
        try
        {
            if(!CoopSyncFixesElevatorRide.LogAllowed(Environment.TickCount64,ref ElevatorRideLogWindow,ref ElevatorRideLogUsed)){ElevatorRideLogSuppressed++;return;}
            var all=new object[values.Length+1];Array.Copy(values,all,values.Length);all[values.Length]=ElevatorRideLogSuppressed;ElevatorRideLogSuppressed=0;
            Serilog.Log.Information(template+" suppressed={Suppressed}",all);
        }
        catch{ /* Logging must never break the frame. */ }
    }
    static bool IsSupportedElevator(Elevator elevator) => elevator!=null && elevator.GetType()==typeof(SimpleElevator);
    static bool SkipLegacySimpleElevator(Elevator elevator) => !ElevatorModuleInitialized || !IsSupportedElevator(elevator) || ModEntry._net==null || !ModEntry._net.IsAlive;
    static void RegisterElevatorNativeHook(string name,string eventName,bool activation)
    {
        var type=typeof(Elevator).Assembly.GetType(name,true);var ev=type.GetEvent(eventName);
        var hook=CreateElevatorNativeHook(ev.EventHandlerType,eventName,activation);
        ElevatorNativeHooks.Add(hook);ev.AddEventHandler(null,hook);
    }
    // Shared by runtime registration and isolated forwarding checks; creating this delegate installs no hook.
    static Delegate CreateElevatorNativeHook(Type handlerType,string eventName,bool activation)
    {
        var signature=handlerType.GetMethod("Invoke");var args=signature.GetParameters().Select(p=>p.ParameterType).ToArray();
        var expected=activation?new[]{typeof(SimpleElevator),typeof(dc.en.Hero),typeof(bool)}:new[]{typeof(Elevator)};
        if(signature.ReturnType!=typeof(void) || !args.Skip(1).SequenceEqual(expected) ||
            args[0].GetMethod("Invoke").ReturnType!=typeof(void) ||
            !args[0].GetMethod("Invoke").GetParameters().Select(p=>p.ParameterType).SequenceEqual(expected))
            throw new InvalidOperationException("Unexpected pinned native elevator delegate signature");
        var adapter=new DynamicMethod("CoopElevator_"+eventName,signature.ReturnType,args,typeof(CoopSyncFixes),true);var il=adapter.GetILGenerator();
        if(activation)
        {
            var run=il.DefineLabel();il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(TryAllowElevatorActivation),Flags));il.Emit(OpCodes.Brtrue,run);il.Emit(OpCodes.Ret);il.MarkLabel(run);
        }
        else {il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(TryBeforeElevatorPhysics),Flags));}
        for(int i=0;i<args.Length;i++)il.Emit(OpCodes.Ldarg,(short)i);
        il.Emit(OpCodes.Callvirt,args[0].GetMethod("Invoke"));
        if(!activation){il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(TryAfterElevatorPhysics),Flags));}
        il.Emit(OpCodes.Ret);var hook=adapter.CreateDelegate(handlerType);RuntimeHelpers.PrepareDelegate(hook);return hook;
    }
    static void InvalidateElevatorScans()=>System.Threading.Interlocked.Increment(ref ElevatorEntityMutation);
    static Delegate CreateElevatorMembershipHook(Type handlerType)
    {
        var invoke=handlerType.GetMethod("Invoke");var args=invoke.GetParameters().Select(p=>p.ParameterType).ToArray();
        var expected=new[]{typeof(Level),typeof(dc.Entity)};
        if(invoke.ReturnType!=typeof(void) || !args.Skip(1).SequenceEqual(expected) ||
            args[0].GetMethod("Invoke").ReturnType!=typeof(void) ||
            !args[0].GetMethod("Invoke").GetParameters().Select(p=>p.ParameterType).SequenceEqual(expected))
            throw new InvalidOperationException("Unexpected pinned level entity membership delegate signature");
        var dm=new DynamicMethod("CoopElevatorEntityMembership",typeof(void),args,typeof(CoopSyncFixes),true);var il=dm.GetILGenerator();
        var invalidate=typeof(CoopSyncFixes).GetMethod(nameof(InvalidateElevatorScans),Flags);
        il.Emit(OpCodes.Call,invalidate);il.BeginExceptionBlock();
        for(int i=0;i<args.Length;i++)il.Emit(OpCodes.Ldarg,(short)i);
        il.Emit(OpCodes.Callvirt,args[0].GetMethod("Invoke"));
        il.BeginFinallyBlock();il.Emit(OpCodes.Call,invalidate);il.EndExceptionBlock();il.Emit(OpCodes.Ret);
        var hook=dm.CreateDelegate(handlerType);RuntimeHelpers.PrepareDelegate(hook);return hook;
    }
    static void ElevatorCallbackError(Exception error)
    {
        try{if(System.Threading.Interlocked.Increment(ref ElevatorCallbackFailures)<=5)
            Serilog.Log.Warning(error,"[CoopSyncFixes] Experimental elevator callback failed");}catch{ /* Logging must never break native forwarding. */ }
    }
    static void TryBeforeElevatorPhysics(object value){if(!ElevatorModuleInitialized)return;try{BeforeElevatorPhysics(value);}catch(Exception e){ElevatorCallbackError(e);}}
    static void TryAfterElevatorPhysics(object value){if(!ElevatorModuleInitialized)return;try{AfterElevatorPhysics(value);}catch(Exception e){ElevatorCallbackError(e);}}
    static bool TryAllowElevatorActivation(object value,object actor)
    {if(!ElevatorModuleInitialized)return true;try{return AllowElevatorActivation(value,actor);}catch(Exception e){ElevatorCallbackError(e);return ModEntry._net?.IsHost!=false;}}
    static void TryPumpElevators(){if(!ElevatorModuleInitialized)return;try{PumpElevators();}catch(Exception e){ElevatorCallbackError(e);}}
    // gc-opt P04: the same reads as the old expression on every call, compared by value; the text is built (with the old
    // expression's formatting) only when one of them changed. Two differences, neither observable: level.map is read once
    // instead of three times (no game code runs between the old expression's three reads; every caller is on the game
    // thread), and formatting is deferred (the old code formatted SessionId, RunId and Sequence each before reading the next;
    // a RunLaunchDescriptor is sealed with read-only fields, and Guid / long / int ToString have no side effect). Otherwise
    // the relative order is the old one: ready flag, current level, the descriptor through its getter (CsfRun: the same
    // call, the same TargetInvocationException boundary), its three values, the map id and its text, the seed, the token last.
    static string ElevatorFloor(NetNode net,Level level)
    {
        var map=level?.map;
        if(map==null || !(bool)Field("s_levelIdentityReady") || (Field("currentLevel") as Level)?.HashlinkPointer!=level.HashlinkPointer)return null;
        var host=net.IsHost;
        var descriptor=(host?CsfRun.HostDescriptor:CsfRun.RemoteDescriptor)();
        if(descriptor==null)return null;
        var session=CsfRun.SessionId(descriptor);var run=CsfRun.RunId(descriptor);var sequence=CsfRun.Sequence(descriptor);
        var id=map.id?.ToString();var seed=map.seed;
        return CsfElevatorFloors.Text(host?0:1,session,run,sequence,id,seed,(int)Field("s_levelIdentityToken"));
    }
    static ElevatorSession RefreshElevatorSession(NetNode net,Level level)
    {
        var floor=ElevatorFloor(net,level);if(floor==null)return null;
        var session=ElevatorSessions.GetValue(net,_=>new ElevatorSession());var build=(int)Field("s_levelIdentityGeneration");
        if(session.Pointer!=level.HashlinkPointer || session.Build!=build || session.Floor!=floor)
        {
            session.Pointer=level.HashlinkPointer;session.Build=build;session.Floor=floor;session.Token=Guid.NewGuid().ToString("N");
            session.Epoch++;session.LocalBuildSerial++;session.RemoteEpoch=0;session.RemoteBoot=null;session.Latest.Clear();session.AcceptedSequences.Clear();session.Peers.Clear();session.LastSubscribe=0;
            session.RideTrack=null;session.LastRideSend=0; // A new floor starts without a ride; the host's peers reset with its own floor.
        }
        return session;
    }
    // gc-opt P04: the five values are read on every call (same order); the text is the kept one while they are the same.
    static string ElevatorTrack(Elevator elevator) => CsfElevatorTracks.Text(elevator.xLeft,elevator.xRight,elevator.yTop,elevator.yBottom,elevator.cWid);
    static List<Elevator> CurrentSimpleElevators(Level level)
    {
        if(level?.entities==null)return new List<Elevator>();
        var entities=level.entities;var scan=ElevatorScans.GetValue(level,_=>new ElevatorScan());
        var pointer=level.HashlinkPointer;var build=(int)Field("s_levelIdentityGeneration");
        var frame=BitConverter.DoubleToInt64Bits(level.ftime);var length=entities.length;
        var mutation=System.Threading.Volatile.Read(ref ElevatorEntityMutation);
        bool reuse=SameElevatorScan(scan,pointer,entities.HashlinkPointer,build,frame,length,mutation);
        if(reuse)for(int i=0;i<scan.Values.Count;i++)
        {
            var e=scan.Values[i];
            if(e.destroyed || e._level?.HashlinkPointer!=pointer || ElevatorTrack(e)!=scan.Tracks[i] ||
                entities.getDyn(scan.Indices[i]) is not Elevator slot || slot.HashlinkPointer!=e.HashlinkPointer){reuse=false;break;}
        }
        if(reuse)return scan.Values;
        scan.Values.Clear();scan.Indices.Clear();scan.Tracks.Clear();ElevatorEntityScans++;
        for(int i=0;i<length;i++)
        {
            ElevatorEntityVisits++;
            if(entities.getDyn(i) is Elevator e && IsSupportedElevator(e) && !e.destroyed && e._level?.HashlinkPointer==pointer)
            {scan.Values.Add(e);scan.Indices.Add(i);scan.Tracks.Add(ElevatorTrack(e));}
        }
        scan.Pointer=pointer;scan.Entities=entities.HashlinkPointer;scan.Build=build;scan.Frame=frame;scan.Length=length;scan.Mutation=mutation;
        return scan.Values;
    }
    static bool SameElevatorScan(ElevatorScan scan,IntPtr pointer,IntPtr entities,int build,long frame,int length,long mutation) =>
        scan.Pointer==pointer && scan.Entities==entities && scan.Build==build && scan.Frame==frame && scan.Length==length && scan.Mutation==mutation;
    // Every Elevator of the level (any type), for the per-frame rider test and the ride log. Lifts are level geometry:
    // the list is rebuilt when the level or its identity build changes and at most every RescanMs; between rebuilds each
    // entry is re-validated (destroyed, still in this level) on every use, so the per-frame cost is a few field reads.
    static List<Elevator> ElevatorRideCandidates(Level level,long now)
    {
        var scan=ElevatorRideScans.GetValue(level,_=>new ElevatorRideScan());var build=(int)Field("s_levelIdentityGeneration");
        if(scan.Pointer==level.HashlinkPointer && scan.Build==build && now>=scan.At && now-scan.At<CoopSyncFixesElevatorRide.RescanMs)
        {
            for(int i=scan.Values.Count-1;i>=0;i--){var e=scan.Values[i];if(e.destroyed || e._level?.HashlinkPointer!=level.HashlinkPointer)scan.Values.RemoveAt(i);}
            return scan.Values;
        }
        scan.Values.Clear();var entities=level.entities;
        if(entities!=null)for(int i=0,n=entities.length;i<n;i++)
            if(entities.getDyn(i) is Elevator e && !e.destroyed && e._level?.HashlinkPointer==level.HashlinkPointer)scan.Values.Add(e);
        scan.Pointer=level.HashlinkPointer;scan.Build=build;scan.At=now;return scan.Values;
    }
    // gc-opt P04: a typed call (entry point, so a Harmony patch on it still runs; TargetInvocationException as before).
    static bool LocalElevatorPlayerDowned() => CsfElevatorMod.LocalPlayerDowned();
    // gc-opt P04: the same non-generic lookup, with a shared box for the key (the dictionary keeps no key it is asked for).
    static object ElevatorDownedRecord(int id) => ModEntry.Instance==null?null:
        ((System.Collections.IDictionary)typeof(ModEntry).GetField("_remoteDowned",ElevatorInstanceFlags).GetValue(ModEntry.Instance))[CsfElevatorBoxes.Int(id)];
    static bool RemoteElevatorPlayerDowned(int id,string level)
    {
        // A death before the first SUB is still authoritative. Neither a new connection
        // nor positive legacy HP proves a revive; keep this gate conservative until
        // native revival removes the downed record. Never clear shared life state here.
        var record=ElevatorDownedRecord(id);if(record==null)return false;
        var downedLevel=(string)record.GetType().GetField("LevelId",ElevatorInstanceFlags).GetValue(record);
        return string.IsNullOrEmpty(downedLevel) || string.Equals(downedLevel,level,StringComparison.OrdinalIgnoreCase);
    }
    static bool FiniteElevator(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    static bool ValidElevatorState(ElevatorMessage value)
    {
        if(value==null || value.Kind!="STATE" || value.Epoch<=0 || value.Sequence<=0 || string.IsNullOrEmpty(value.Track) || value.Track.Length>200 ||
            !Guid.TryParseExact(value.Boot,"N",out _) || value.Left>value.Right || value.Top>value.Bottom || value.Width<=0 || value.Width>100 ||
            value.Cx<value.Left-2 || value.Cx>value.Right+2 || value.Cy<value.Top-1 || value.Cy>value.Bottom+1 ||
            !FiniteElevator(value.Xr) || !FiniteElevator(value.Yr) || !FiniteElevator(value.Ty) || !FiniteElevator(value.Dy) || !FiniteElevator(value.Bdy) || !FiniteElevator(value.Speed) ||
            value.Xr<0 || value.Xr>1 || value.Yr<0 || value.Yr>1 || value.Ty<(value.Top+1.0)*24 || value.Ty>(value.Bottom+1.0)*24 ||
            Math.Abs(value.Dy)>20 || Math.Abs(value.Bdy)>20 || value.Speed<=0 || value.Speed>20 ||
            value.MoveDir!=1 && value.MoveDir!=-1 && !(value.MoveDir==0 && value.Ty==(value.Cy+value.Yr)*24))return false;
        return value.Track==CsfElevatorTracks.Text(value.Left,value.Right,value.Top,value.Bottom,value.Width);   // the same text, kept
    }
    static void StampElevator(ElevatorMessage value,ElevatorSession session,string kind,string token)
    {value.Kind=kind;value.Token=token;value.Floor=session.Floor;value.Boot=session.Boot;value.Epoch=session.Epoch;}
    static ElevatorMessage CaptureElevator(Elevator elevator,ElevatorSession session)
    {
        return new ElevatorMessage{Kind="STATE",Floor=session.Floor,Boot=session.Boot,Epoch=session.Epoch,Sequence=++session.Sequence,
            Track=ElevatorTrack(elevator),Left=elevator.xLeft,Right=elevator.xRight,Top=elevator.yTop,Bottom=elevator.yBottom,Width=elevator.cWid,
            Cx=elevator.cx,Cy=elevator.cy,Xr=elevator.xr,Yr=elevator.yr,Ty=elevator.ty,MoveDir=elevator.moveDir,Dy=elevator.dy,Bdy=elevator.bdy,Speed=elevator.speed};
    }
    static void AfterElevatorPhysics(object value)
    {
        var elevator=value as Elevator;var net=ModEntry._net;if(!IsSupportedElevator(elevator) || net==null || !net.IsHost || !net.IsAlive || elevator.destroyed)return;
        var level=ModEntry.me?._level;if(level?.HashlinkPointer!=elevator._level?.HashlinkPointer)return;
        var session=RefreshElevatorSession(net,level);if(session==null)return;
        session.Latest[ElevatorTrack(elevator)]=CaptureElevator(elevator,session);
    }
    static void RecordElevatorStateError(double error)
    {
        var size=Math.Abs(error);ElevatorStateErrorBuckets[CoopSyncFixesElevatorRide.ErrorBucket(size)]++;
        if(size>ElevatorStateErrorMax)ElevatorStateErrorMax=size;
        if(size>ElevatorStateErrorRideMax)ElevatorStateErrorRideMax=size;
    }
    static void BeforeElevatorPhysics(object value)
    {
        var elevator=value as Elevator;var net=ModEntry._net;if(!IsSupportedElevator(elevator) || net==null || net.IsHost || !net.IsAlive || elevator.destroyed)return;
        var level=ModEntry.me?._level;if(level?.HashlinkPointer!=elevator._level?.HashlinkPointer)return;
        var session=RefreshElevatorSession(net,level);if(session==null || !session.Latest.TryGetValue(ElevatorTrack(elevator),out var state))return;
        session.Latest.Remove(state.Track);
        if(!CurrentElevatorNode(net) || !FreshElevatorMessage(state,Environment.TickCount64) || state.Token!=session.Token || state.BuildSerial!=session.LocalBuildSerial || state.Floor!=session.Floor || state.Epoch!=session.RemoteEpoch || state.Boot!=session.RemoteBoot || !ValidElevatorState(state))return;
        // Track bounds alone are not sufficient if a native level contains duplicate tracks.
        int sameTrack=0;foreach(var e in CurrentSimpleElevators(level))if(ElevatorTrack(e)==state.Track)sameTrack++;   // Count(predicate), no closure
        if(sameTrack!=1){ElevatorRejectedStates++;return;}
        var hero=ModEntry.me;double oldFoot=(elevator.cy+elevator.yr)*24;
        // U11 (B): every accepted STATE is measured against the local lift before it would be applied. A STATE that
        // only repeats the running motion (same track, direction and target) within the deadband is skipped: the
        // native lift keeps moving the rider smoothly. Stops, starts, reversals and larger errors still hard-snap.
        double error=(state.Cy+state.Yr)*24-oldFoot;RecordElevatorStateError(error);
        if(ElevatorDeadbandEnabled && CoopSyncFixesElevatorRide.SkipState(state.Track,ElevatorTrack(elevator),state.MoveDir,elevator.moveDir,state.Ty,elevator.ty,error,elevator.dy+elevator.bdy))
        {ElevatorDeadbandSkips++;return;}
        bool riding=hero!=null && !hero.destroyed && hero.life>0 && !LocalElevatorPlayerDowned() && hero._level?.HashlinkPointer==level.HashlinkPointer &&
            CoopSyncFixesElevatorRide.RiderOn(hero.cx,(hero.cy+hero.yr)*24,hero.dy+hero.bdy,elevator.cx,elevator.cWid,oldFoot);
        // Preserve the exact native tuple, including yr=1 at endpoints. Never normalize
        // to floor(Y/24), and never overwrite lastCy: orig updates collision/groundYr rows.
        elevator.cx=state.Cx;elevator.cy=state.Cy;elevator.xr=state.Xr;elevator.yr=state.Yr;
        elevator.ty=state.Ty;elevator.moveDir=state.MoveDir;elevator.dy=state.Dy;elevator.bdy=state.Bdy;elevator.speed=state.Speed;
        if(riding){hero.cy=state.Cy;hero.yr=state.Yr;ElevatorRiderCorrections++;}
        ElevatorAppliedStates++;
    }
    static bool AllowElevatorActivation(object value,object actor)
    {
        var elevator=value as Elevator;var net=ModEntry._net;if(!IsSupportedElevator(elevator) || net==null || !net.IsAlive || net.IsHost)return true;
        var hero=ModEntry.me;if(hero==null || actor is not dc.Entity entity || entity.HashlinkPointer!=hero.HashlinkPointer)return true;
        if(!CurrentElevatorNode(net) || hero.destroyed || hero.life<=0 || LocalElevatorPlayerDowned())return false;
        var session=RefreshElevatorSession(net,hero._level);
        if(session==null || session.RemoteEpoch<=0 || session.RemoteBoot==null)return false;
        ElevatorSendHost(net,new ElevatorMessage{Kind="ACT",Token=session.Token,Floor=session.Floor,Boot=session.RemoteBoot,Epoch=session.RemoteEpoch,Track=ElevatorTrack(elevator),Request=++session.Request});
        return false;
    }
    static bool ReadElevatorRemotePosition(NetNode net,int id,out object record,out long sequence,out double x,out double y,out string level,out int life)
    {
        record=null;sequence=0;x=y=0;level=null;life=0;
        var gate=typeof(NetNode).GetField("_sync",ElevatorInstanceFlags).GetValue(net);
        lock(gate)
        {
            // gc-opt P04: the same non-generic lookup with a shared key box; the record's fields through typed readers of
            // RemotePlayerState (the sealed value type of _remotes: what record.GetType() named), so nothing is boxed. A null
            // record still fails with NullReferenceException at its first field.
            var remotes=(System.Collections.IDictionary)typeof(NetNode).GetField("_remotes",ElevatorInstanceFlags).GetValue(net);var key=CsfElevatorBoxes.Int(id);
            if(!remotes.Contains(key))return false;record=remotes[key];var localRecord=record;
            T Get<T>(Func<object,T> read)=>read(localRecord);
            // All accesses in this scope are managed records. No native API under this lock.
            if(!Get(CsfElevatorRemote.HasRemote) || !Get(CsfElevatorRemote.HasPosition))return false;
            sequence=Get(CsfElevatorRemote.LastPositionSequence);x=Get(CsfElevatorRemote.X);y=Get(CsfElevatorRemote.Y);level=Get(CsfElevatorRemote.LevelId);life=Get(CsfElevatorRemote.Life);return true;
        }
    }
    static bool RemoteCanActivateElevator(NetNode net,int id,ElevatorPeer peer,Elevator elevator,Level level)
    {
        if(!ReadElevatorRemotePosition(net,id,out var record,out var sequence,out var x,out var y,out var remoteLevel,out var life))return false;
        if(!ReferenceEquals(peer.PositionRecord,record) || sequence>peer.PositionSequence)
        {peer.PositionRecord=record;peer.PositionSequence=sequence;peer.PositionSeenAt=Environment.TickCount64;}
        if(peer.PositionSeenAt==0 || Environment.TickCount64-peer.PositionSeenAt>1500 || life<=0 || RemoteElevatorPlayerDowned(id,level.map.id?.ToString()) || !FiniteElevator(x) || !FiniteElevator(y) || remoteLevel!=level.map.id?.ToString())return false;
        return x>=(elevator.cx-elevator.cWid*.5-.35)*24 && x<(elevator.cx+elevator.cWid*.5+.35)*24 && Math.Abs(y-(elevator.cy+elevator.yr)*24)<=48;
    }
    static void ObserveElevatorRemotePosition(NetNode net,int id,ElevatorPeer peer)
    {
        if(ReadElevatorRemotePosition(net,id,out var record,out var sequence,out _,out _,out _,out _) &&
            (!ReferenceEquals(peer.PositionRecord,record) || sequence>peer.PositionSequence))
        {peer.PositionRecord=record;peer.PositionSequence=sequence;peer.PositionSeenAt=Environment.TickCount64;}
    }
    // Pure managed acceptance boundary, shared by the live pump and isolated mutation probes.
    static bool AcceptElevatorClientMessage(ElevatorSession session,ElevatorMessage message)
    {
        if(message.Floor!=session.Floor || message.Token!=session.Token || message.BuildSerial!=session.LocalBuildSerial)return false;
        if(message.Kind=="ACK")
        {
            if(!Guid.TryParseExact(message.Boot,"N",out _) || message.Epoch<=0 ||
                session.RemoteBoot!=null && (session.RemoteBoot!=message.Boot || message.Epoch<session.RemoteEpoch))return false;
            if(session.RemoteEpoch!=message.Epoch){session.Latest.Clear();session.AcceptedSequences.Clear();}
            session.RemoteBoot=message.Boot;session.RemoteEpoch=message.Epoch;return true;
        }
        if(!ValidElevatorState(message) || message.Boot!=session.RemoteBoot || message.Epoch!=session.RemoteEpoch ||
            session.AcceptedSequences.TryGetValue(message.Track,out var accepted) && message.Sequence<=accepted)return false;
        session.AcceptedSequences[message.Track]=message.Sequence;session.Latest[message.Track]=message;return true;
    }
    static void PumpElevators()
    {
        if(!ElevatorModuleInitialized)return;
        var net=ModEntry._net;var level=ModEntry.me?._level;if(!CurrentElevatorNode(net) || level==null)return;
        var session=RefreshElevatorSession(net,level);if(session==null)return;
        if(net.IsHost)foreach(var pair in session.Peers)ObserveElevatorRemotePosition(net,pair.Key,pair.Value);
        int budget=256;
        while(budget-->0 && session.Incoming.TryDequeue(out var message))
        {
            System.Threading.Interlocked.Decrement(ref session.Queued);
            if(!FreshElevatorMessage(message,Environment.TickCount64) || message.Floor!=session.Floor)continue;
            if(net.IsHost)
            {
                var connection=ElevatorConnection(net,message.Sender,out var steam);if(connection==null || !ReferenceEquals(connection,message.ReceivedConnection))continue;
                // Explicit dispatch by Kind (Alpha1 treated any non-SUB message as an activation request).
                var kind=CoopSyncFixesElevatorRide.HostKind(message.Kind,ElevatorRideReady());
                if(kind==CoopSyncFixesElevatorRide.HostSub)
                {
                    session.Peers.TryGetValue(message.Sender,out var peer);
                    if(peer!=null && ReferenceEquals(connection,peer.Connection) && (message.BuildSerial<peer.BuildSerial || message.BuildSerial==peer.BuildSerial && peer.Token!=message.Token))continue;
                    if(peer==null || !ReferenceEquals(connection,peer.Connection) || peer.Token!=message.Token)
                    {
                        session.Peers[message.Sender]=peer=new ElevatorPeer{Connection=connection,Steam=steam,Token=message.Token,Floor=message.Floor,BuildSerial=message.BuildSerial};
                        if(ReadElevatorRemotePosition(net,message.Sender,out var record,out var sequence,out _,out _,out _,out _))
                        {peer.PositionRecord=record;peer.PositionSequence=sequence;} // First ACT requires a post-subscription position update.
                    }
                    peer.LastSeen=Math.Max(peer.LastSeen,message.ReceivedAt);
                    var ack=new ElevatorMessage();StampElevator(ack,session,"ACK",message.Token);ElevatorSendPeer(net,message.Sender,peer,ack);
                }
                else if(kind==CoopSyncFixesElevatorRide.HostAct && session.Peers.TryGetValue(message.Sender,out var peer) && ReferenceEquals(connection,peer.Connection) && peer.Token==message.Token &&
                    message.BuildSerial==peer.BuildSerial && Environment.TickCount64-peer.LastSeen<=3000 &&
                    message.Boot==session.Boot && message.Epoch==session.Epoch && message.Request>peer.LastRequest)
                {
                    peer.LastRequest=message.Request;
                    // Where(track).ToArray() without the closure: every lift's track is read, the first match kept.
                    Elevator match=null;int matches=0;
                    foreach(var e in CurrentSimpleElevators(level))if(ElevatorTrack(e)==message.Track && matches++==0)match=e;
                    if(matches==1 && RemoteCanActivateElevator(net,message.Sender,peer,match,level) && match.isReady())
                        ((SimpleElevator)match).onActivate(ModEntry.me,false);
                }
                else if(kind==CoopSyncFixesElevatorRide.HostRide)TryAcceptElevatorRide(session,message,connection,level);
            }
            else
            {
                AcceptElevatorClientMessage(session,message);
            }
        }
        var now=Environment.TickCount64;
        if(net.IsHost)TryObserveHostElevatorRide(session,level,now);
        if(!net.IsHost)
        {
            TryPumpElevatorRide(net,session,level,now);
            if(now-session.LastSubscribe>=1000){session.LastSubscribe=now;ElevatorSendHost(net,new ElevatorMessage{Kind="SUB",Token=session.Token,Floor=session.Floor});}
            return;
        }
        if(now-session.LastSend<100)return;session.LastSend=now;
        // gc-opt P04: GroupBy(ElevatorTrack).Where(one).Select(Single).ToArray() and Peers.ToArray() into lists the session keeps
        // (taken out while in use, put back cleared): one track read per lift in list order, the lifts whose track occurs
        // once in list order, then a snapshot of the peers in enumeration order.
        var elevators=CurrentSimpleElevators(level);
        var tracks=session.SendTracks??new List<string>();session.SendTracks=null;tracks.Clear();
        var unique=session.SendUnique??new List<Elevator>();session.SendUnique=null;unique.Clear();
        // (through the cached method-group delegate GroupBy was given: it keeps the compiler's cached delegate fields as they were)
        Func<Elevator,string> track=ElevatorTrack;foreach(var e in elevators)tracks.Add(track(e));
        for(int i=0;i<tracks.Count;i++)
        {
            int same=0;for(int j=0;j<tracks.Count;j++)if(tracks[j]==tracks[i])same++;
            if(same==1)unique.Add(elevators[i]);
        }
        var peers=session.SendPeers??new List<KeyValuePair<int,ElevatorPeer>>();session.SendPeers=null;peers.Clear();peers.AddRange(session.Peers);
        foreach(var pair in peers)
        {
            if(now-pair.Value.LastSeen>3000)continue;
            foreach(var elevator in unique)
            {
                if(!session.Latest.TryGetValue(ElevatorTrack(elevator),out var state))continue;
                state.Token=pair.Value.Token;ElevatorSendPeer(net,pair.Key,pair.Value,state);
            }
        }
        peers.Clear();session.SendPeers=peers;unique.Clear();session.SendUnique=unique;tracks.Clear();session.SendTracks=tracks;
    }
    // ---- U11 (A) client side: RIDE sender and ride log (game thread, from the elevator pump) ----
    static void TryPumpElevatorRide(NetNode net,ElevatorSession session,Level level,long now)
    {if(!ElevatorRideReady())return;try{PumpElevatorRide(net,session,level,now);}catch(Exception e){ElevatorRideFault(e);}}
    // The lift the local hero stands on (a SimpleElevator first), with the number of SimpleElevators that pass the test.
    // The same test as BeforeElevatorPhysics (Alpha1 Elevators.cs:202-204), every frame, on every lift of the level.
    static Elevator FindRiddenElevator(Level level,long now,out int supported)
    {
        var hero=ModEntry.me;Elevator ridden=null;supported=0;
        if(hero==null || hero.destroyed || hero.life<=0 || hero._level?.HashlinkPointer!=level.HashlinkPointer || LocalElevatorPlayerDowned())return null;
        double foot=(hero.cy+hero.yr)*24,vy=hero.dy+hero.bdy;
        foreach(var lift in ElevatorRideCandidates(level,now))
        {
            if(!CoopSyncFixesElevatorRide.RiderOn(hero.cx,foot,vy,lift.cx,lift.cWid,(lift.cy+lift.yr)*24))continue;
            bool simple=IsSupportedElevator(lift);if(simple)supported++;
            if(ridden==null || simple && !IsSupportedElevator(ridden))ridden=lift;
        }
        return ridden;
    }
    // (C) on the host: its own rides are logged too (the host lift is authoritative, nothing is sent).
    static void TryObserveHostElevatorRide(ElevatorSession session,Level level,long now)
    {
        if(!ElevatorRideReady())return;
        try{var ridden=FindRiddenElevator(level,now,out _);ObserveElevatorRideEpisode(session,ridden,level,now,ridden==null?null:IsSupportedElevator(ridden)?"host-authority":"unsupported-type");}
        catch(Exception e){ElevatorRideFault(e);}
    }
    static void PumpElevatorRide(NetNode net,ElevatorSession session,Level level,long now)
    {
        var hero=ModEntry.me;var ridden=FindRiddenElevator(level,now,out var supported);
        bool negotiated=session.RemoteRide && session.WelcomeAccepted && session.RemoteEpoch>0 && session.RemoteBoot!=null;
        string track=ridden!=null && supported==1 && IsSupportedElevator(ridden)?ElevatorTrack(ridden):null;
        ObserveElevatorRideEpisode(session,ridden,level,now,ridden==null?null:!IsSupportedElevator(ridden)?"unsupported-type":supported!=1?"ambiguous":
            !session.RemoteRide?"host-without-csfelev2":negotiated?"ride":"not-subscribed");
        var action=CoopSyncFixesElevatorRide.SendAction(negotiated?track:null,session.RideTrack,now,session.LastRideSend);
        if(action==CoopSyncFixesElevatorRide.SendOn)
        {
            ElevatorSendHost(net,new ElevatorMessage{Kind="RIDE",Token=session.Token,Floor=session.Floor,Boot=session.RemoteBoot,Epoch=session.RemoteEpoch,Track=track,
                Ride=1,Offset=(hero.cx+hero.xr-ridden.cx-ridden.xr)*24,Sequence=++session.RideSequence});
            session.RideTrack=track;session.LastRideSend=now;ElevatorRideSent++;
        }
        else if(action==CoopSyncFixesElevatorRide.SendOff)
        {
            ElevatorSendHost(net,new ElevatorMessage{Kind="RIDE",Token=session.Token,Floor=session.Floor,Boot=session.RemoteBoot,Epoch=session.RemoteEpoch,Track=session.RideTrack,
                Ride=0,Sequence=++session.RideSequence});
            session.RideTrack=null;session.LastRideSend=now;ElevatorRideSent++;
        }
    }
    static void ObserveElevatorRideEpisode(ElevatorSession session,Elevator ridden,Level level,long now,string sync)
    {
        var lift=ridden?.HashlinkPointer??IntPtr.Zero;
        if(lift!=IntPtr.Zero && lift==session.RideLogLift && level.HashlinkPointer==session.RideLogLevel)
        {
            if(CoopSyncFixesElevatorRide.LiftMoving((ridden.cy+ridden.yr)*24,ridden.ty,ridden.dy,ridden.bdy))session.RideLogMoved=true;
            return;
        }
        // (B) evidence without a probe (two-PC playtest): the client's STATEs of this ride, skipped ones, the largest error
        // and the error histogram delta (|px| <=1/3/6/12/24/48/more). All zero on the host (it never applies a STATE).
        if(session.RideLogLift!=IntPtr.Zero)
        {
            var buckets=new long[ElevatorStateErrorBuckets.Length];
            for(int i=0;i<buckets.Length;i++)buckets[i]=ElevatorStateErrorBuckets[i]-(session.RideLogBuckets!=null && i<session.RideLogBuckets.Length?session.RideLogBuckets[i]:0);
            LogElevatorRide("[CoopSyncFixes] Elevator ride stop type={Type} level={Level} track={Track} seconds={Seconds} moved={Moved} rideMessages={Sent} states={States} skipped={Skipped} maxErrorPx={MaxError} errors={Buckets}",
                session.RideLogType,session.RideLogLevelId,session.RideLogTrack,Math.Round((now-session.RideLogStart)/1000.0,1),session.RideLogMoved,ElevatorRideSent-session.RideLogSent,
                ElevatorAppliedStates-session.RideLogApplied,ElevatorDeadbandSkips-session.RideLogSkipped,Math.Round(ElevatorStateErrorRideMax,1),string.Join("/",buckets));
        }
        session.RideLogLift=lift;session.RideLogLevel=level.HashlinkPointer;if(ridden==null)return;
        session.RideLogType=ridden.GetType().FullName;session.RideLogLevelId=level.map?.id?.ToString();session.RideLogTrack=ElevatorTrack(ridden);
        session.RideLogStart=now;session.RideLogMoved=false;session.RideLogSent=ElevatorRideSent;
        session.RideLogApplied=ElevatorAppliedStates;session.RideLogSkipped=ElevatorDeadbandSkips;session.RideLogBuckets=(long[])ElevatorStateErrorBuckets.Clone();ElevatorStateErrorRideMax=0;
        LogElevatorRide("[CoopSyncFixes] Elevator ride start type={Type} level={Level} track={Track} sync={Sync}",session.RideLogType,session.RideLogLevelId,session.RideLogTrack,sync);
    }
    // ---- U11 (A) host side: RIDE acceptance (pump) and avatar presentation (ReceiveGhostCoords postfix) ----
    static void TryAcceptElevatorRide(ElevatorSession session,ElevatorMessage message,object connection,Level level)
    {
        try
        {
            if(!session.Peers.TryGetValue(message.Sender,out var peer) || !ReferenceEquals(connection,peer.Connection) || peer.Token!=message.Token ||
                message.BuildSerial!=peer.BuildSerial || Environment.TickCount64-peer.LastSeen>3000 || message.Boot!=session.Boot || message.Epoch!=session.Epoch ||
                !CoopSyncFixesElevatorRide.ValidRide(message.Track,message.Ride,message.Offset,message.Sequence) || message.Sequence<=peer.LastRide)
            {ElevatorRideRejected++;return;}
            peer.LastRide=message.Sequence;ElevatorRideAccepted++;
            if(message.Ride==1)
            {
                if(peer.RidePresenting && peer.RideTrack!=message.Track)EndElevatorRidePresentation(message.Sender,peer,level,"track");
                peer.RideTrack=message.Track;peer.RideOn=true;peer.RideOffset=message.Offset;peer.RideAt=message.ReceivedAt;
            }
            else
            {
                if(peer.RidePresenting)EndElevatorRidePresentation(message.Sender,peer,level,"off");
                peer.RideOn=false;
            }
        }
        catch(Exception e){ElevatorRideFault(e);}
    }
    static void AfterReceiveGhostCoords()
    {
        if(!ElevatorRideReady())return;
        try{PresentElevatorRiders();}catch(Exception e){ElevatorRideFault(e);}
    }
    static void PresentElevatorRiders()
    {
        var net=ModEntry._net;if(net==null || !net.IsHost || !CurrentElevatorNode(net) || !ElevatorSessions.TryGetValue(net,out var session) || session.Peers.Count==0)return;
        var level=ModEntry.me?._level;if(level?.map==null || session.Pointer!=level.HashlinkPointer)return;
        var now=Environment.TickCount64;
        foreach(var pair in session.Peers)
        {
            var peer=pair.Value;
            if(!peer.RideOn || !CoopSyncFixesElevatorRide.RideFresh(now,peer.RideAt))
            {ElevatorRideReportAgeMs=-1;if(peer.RidePresenting)EndElevatorRidePresentation(pair.Key,peer,level,peer.RideOn?"stale":"off");continue;}
            if(!peer.RidePresenting)
            {
                peer.RidePresenting=true;peer.RidePinned=0;peer.RideMaxCorrection=0;peer.RideLastMoving=0;peer.RideStartedAt=now;peer.RideMaxAge=0;peer.RideMaxHold=0;
                LogElevatorRide("[CoopSyncFixes] Elevator ride presentation start peer={Peer} type={Type} level={Level} track={Track}",
                    pair.Key,typeof(SimpleElevator).FullName,level.map.id?.ToString(),peer.RideTrack);
            }
            var age=now-peer.RideAt;ElevatorRideReportAgeMs=age;
            if(PinElevatorRider(net,level,pair.Key,peer,now))
            {
                peer.RidePinned++;ElevatorRidePinnedFrames++;
                if(age>peer.RideMaxAge)peer.RideMaxAge=age;if(age>ElevatorRideMaxReportAgeMs)ElevatorRideMaxReportAgeMs=age;
            }
            else ElevatorRideHeldFrames++;
        }
    }
    static bool PinElevatorRider(NetNode net,Level level,int id,ElevatorPeer peer,long now)
    {
        Elevator lift=null;int count=0;
        foreach(var e in ElevatorRideCandidates(level,now))if(IsSupportedElevator(e) && ElevatorTrack(e)==peer.RideTrack){lift=e;count++;}
        if(count!=1)return false;
        double liftFoot=(lift.cy+lift.yr)*24;
        bool moving=CoopSyncFixesElevatorRide.LiftMoving(liftFoot,lift.ty,lift.dy,lift.bdy);if(moving)peer.RideLastMoving=now;
        // The outer bound first (the longest, unsettled stop hold), before any reflection or lock.
        if(!CoopSyncFixesElevatorRide.PinWindow(moving,now,peer.RideLastMoving,false))return false;
        if((bool)ElevatorRideTransitionActive.GetValue(null) || (bool)ElevatorRideCreationBlocked.GetValue(null))return false;
        if(!ReadElevatorRemotePosition(net,id,out _,out _,out var x,out var y,out var remoteLevel,out var life))return false;
        var levelId=level.map.id?.ToString();
        if(life<=0 || remoteLevel!=levelId || RemoteElevatorPlayerDowned(id,levelId) || !CoopSyncFixesElevatorRide.RemoteOnLift(x,y,lift.cx,lift.cWid,lift.yTop,lift.yBottom))return false;
        // After the host lift stopped: hold at least StopHoldMs, and on until the partner's own POS stands on the platform
        // (its lift arrives one or two hops later, more after a stall of its game), so the release never shows a drop.
        if(!CoopSyncFixesElevatorRide.PinWindow(moving,now,peer.RideLastMoving,CoopSyncFixesElevatorRide.Settled(y,liftFoot)))return false;
        if(!moving && now-peer.RideLastMoving>peer.RideMaxHold)peer.RideMaxHold=now-peer.RideLastMoving;
        var args=new object[]{net.id,id,0};if(!(bool)ElevatorRideClientIndex.Invoke(null,args))return false;
        var slot=(int)args[2];if(slot<0 || slot>=ModEntry.clients.Length)return false;
        var ghost=ModEntry.clients[slot];
        if(ghost==null || ghost.destroyed || ghost.life<=0 || ghost._level?.HashlinkPointer!=level.HashlinkPointer)return false;
        // X stays the client's POS (the base mod's own value); only the feet move onto the host's platform.
        var target=CoopSyncFixesElevatorRide.PinnedY(liftFoot);var before=(ghost.cy+ghost.yr)*24;
        ghost.setPosPixel(x,target);
        var correction=Math.Abs(target-(y-CoopSyncFixesElevatorRide.PinLiftPx));if(correction>peer.RideMaxCorrection)peer.RideMaxCorrection=correction;
        if(Math.Abs(before-target)>0.01)ElevatorRideHeadDirty.Invoke(null,new object[]{slot,true});
        return true;
    }
    static void EndElevatorRidePresentation(int id,ElevatorPeer peer,Level level,string reason)
    {
        peer.RidePresenting=false;
        // maxReportAgeMs: the oldest RIDE report a pinned frame relied on (a partner stall shows here before it shows as
        // reason=stale); maxHoldMs: the longest pin after the host lift stopped (the partner's arrival lag).
        LogElevatorRide("[CoopSyncFixes] Elevator ride presentation stop peer={Peer} level={Level} track={Track} reason={Reason} seconds={Seconds} pinnedFrames={Pinned} maxCorrectionPx={Max} maxReportAgeMs={Age} maxHoldMs={Hold}",
            id,level?.map?.id?.ToString(),peer.RideTrack,reason,Math.Round((Environment.TickCount64-peer.RideStartedAt)/1000.0,1),peer.RidePinned,Math.Round(peer.RideMaxCorrection,1),peer.RideMaxAge,peer.RideMaxHold);
    }

    // ---- gc-opt P04: what the elevator code keeps between calls (PATTERN.md: nested Csf* holders, no outer member) ----
    // A kept text is handed out only while every value it was made from is the same, compared by value on every call;
    // int and long texts depend on nothing else but NumberFormatInfo.CurrentInfo.NegativeSign (Int32/Int64.ToString()),
    // which is part of the comparison. Entries are immutable and published whole, so racing threads only build twice.
    private sealed class CsfElevatorFloor
    {
        internal readonly Guid Session;internal readonly long Run;internal readonly int Sequence,Seed,Token;internal readonly string Map,Negative,Text;
        internal CsfElevatorFloor(Guid session,long run,int sequence,string map,int seed,int token,string negative,string text)
        {Session=session;Run=run;Sequence=sequence;Map=map;Seed=seed;Token=token;Negative=negative;Text=text;}
    }
    private static class CsfElevatorFloors
    {
        internal static readonly CsfElevatorFloor[] Last=new CsfElevatorFloor[2];   // [0] host, [1] client
        internal static string Text(int slot,Guid session,long run,int sequence,string map,int seed,int token)
        {
            var negative=System.Globalization.NumberFormatInfo.CurrentInfo.NegativeSign;
            var last=System.Threading.Volatile.Read(ref Last[slot]);
            if(last!=null && last.Session==session && last.Run==run && last.Sequence==sequence && last.Seed==seed && last.Token==token &&
                string.Equals(last.Map,map,StringComparison.Ordinal) && string.Equals(last.Negative,negative,StringComparison.Ordinal))return last.Text;
            // The text of the old expression: Guid "D", the numbers with ToString(), a null map id as nothing.
            var text=session.ToString()+":"+run.ToString()+":"+sequence.ToString()+":"+map+":"+BitConverter.DoubleToInt64Bits(seed).ToString()+":"+token.ToString();
            System.Threading.Volatile.Write(ref Last[slot],new CsfElevatorFloor(session,run,sequence,map,seed,token,negative,text));
            return text;
        }
    }
    private sealed class CsfElevatorTrack
    {
        internal readonly int Left,Right,Top,Bottom,Width;internal readonly string Negative,Text;
        internal CsfElevatorTrack(int left,int right,int top,int bottom,int width,string negative,string text)
        {Left=left;Right=right;Top=top;Bottom=bottom;Width=width;Negative=negative;Text=text;}
    }
    private static class CsfElevatorTracks
    {
        internal static readonly CsfElevatorTrack[] Slots=new CsfElevatorTrack[64];
        internal static string Text(int left,int right,int top,int bottom,int width)
        {
            var negative=System.Globalization.NumberFormatInfo.CurrentInfo.NegativeSign;
            var slot=(int)(((uint)left*2654435761u^(uint)right*2246822519u^(uint)top*3266489917u^(uint)bottom*668265263u^(uint)width*374761393u)>>26);
            var known=System.Threading.Volatile.Read(ref Slots[slot]);
            if(known!=null && known.Left==left && known.Right==right && known.Top==top && known.Bottom==bottom && known.Width==width &&
                string.Equals(known.Negative,negative,StringComparison.Ordinal))return known.Text;
            var text=left+":"+right+":"+top+":"+bottom+":"+width;   // the old ElevatorTrack expression
            System.Threading.Volatile.Write(ref Slots[slot],new CsfElevatorTrack(left,right,top,bottom,width,negative,text));
            return text;
        }
    }
    // Boxes of small ints for the non-generic dictionary lookups (IDictionary.Contains / [key] unbox the key and keep nothing).
    private static class CsfElevatorBoxes
    {
        internal static readonly object[] Small=Make();
        private static object[] Make(){var all=new object[256];for(int i=0;i<all.Length;i++)all[i]=i;return all;}
        internal static object Int(int value)=>(uint)value<(uint)Small.Length?Small[value]:value;
    }
    // The fields of NetNode.RemotePlayerState that ReadElevatorRemotePosition reads (sealed class: the value type of _remotes).
    private static class CsfElevatorRemote
    {
        private static readonly Type State=typeof(NetNode).GetNestedType("RemotePlayerState",BindingFlags.NonPublic)??throw new TypeLoadException("NetNode.RemotePlayerState");
        internal static readonly Func<object,bool> HasRemote=CsfStubs.Reader<bool>(State,"HasRemote"),HasPosition=CsfStubs.Reader<bool>(State,"HasPosition");
        internal static readonly Func<object,long> LastPositionSequence=CsfStubs.Reader<long>(State,"LastPositionSequence");
        internal static readonly Func<object,double> X=CsfStubs.Reader<double>(State,"X"),Y=CsfStubs.Reader<double>(State,"Y");
        internal static readonly Func<object,string> LevelId=CsfStubs.Reader<string>(State,"LevelId");
        internal static readonly Func<object,int> Life=CsfStubs.Reader<int>(State,"Life");
    }
    // HandshakeComplete of the two connection classes of NetNode (sealed; property getters through their entry points, a
    // getter's exception inside TargetInvocationException as PropertyInfo.GetValue gave it).
    private static class CsfElevatorConnections
    {
        internal static readonly Func<object,bool> ClientReady=CsfStubs.Reader<bool>(Connection("ClientConnection"),"HandshakeComplete");
        internal static readonly Func<object,bool> SteamReady=CsfStubs.Reader<bool>(Connection("SteamClientConnection"),"HandshakeComplete");
        private static Type Connection(string name)=>typeof(NetNode).GetNestedType(name,BindingFlags.NonPublic)??throw new TypeLoadException("NetNode."+name);
    }
    private static class CsfElevatorMod
    {
        internal static readonly Func<bool> LocalPlayerDowned=CsfStubs.Typed<Func<bool>>(typeof(ModEntry),"IsLocalPlayerDowned");
    }
}

// Pure rules of the U11 elevator fix (PLAN-ALPHA2.md issue 4): the RIDE presentation (A), the client STATE deadband and
// its error histogram (B), the optional +csfelev2 handshake token and the IL pins of the base-mod methods RIDE patches or
// calls. Primitives only (no game, mod or Harmony type), so alpha2-elevator\Test-Managed.ps1 checks them, and its
// mutants, without a game. Internal and part-prefixed (gate G3).
internal static class CoopSyncFixesElevatorRide
{
    public const string Token="+csfelev2";
    // A RIDE "on" report stays valid this long after it arrived: the bound FreshElevatorMessage already puts on every
    // non-SUB elevator message (an older one is dropped from the queue anyway). The plan proposed 250 ms, the first U11
    // build 400 ms; the first native run (a2-U11-elevator-2) dropped the pin mid-ride ("reason=stale", up to 130 px for 1-5
    // frames) even without injected delay, because the partner's game stalls 300-500 ms at times: then neither its RIDE
    // nor its POS moves. That is exactly when the pin is needed: when it resumes, its own lift snaps to the host STATE and
    // its hero rides along, so the host platform is where it will be. Every pinned frame is still bounded by the per-frame
    // gates (host lift moving or in its stop hold, partner POS on that lift's columns and in its shaft, alive, not downed,
    // same level, no ghost transition), and the explicit RIDE off (same ordered stream as POS) still ends a ride at once.
    public const long FreshMs=1500;
    public const long SendIntervalMs=100;     // the client repeats RIDE about 10 times per second while it rides
    public const long StopHoldMs=600;         // the pin outlives the host lift's stop at least by two hops plus jitter ...
    public const long StopHoldMaxMs=1500;     // ... and until the partner's own POS stands on the platform, at most this long
    public const long RescanMs=2000;          // rider-test lift list rebuild period (lifts are level geometry)
    public const double FootTolerancePx=2;    // rider test: feet within 2 px of the platform top (Alpha1 Elevators.cs:204)
    public const double PinLiftPx=0.2;        // the base mod draws a partner 0.2 px above its POS (release IL 37509-37512)
    public const double DeadbandFloorPx=6;    // plan: skip STATEs with an error of 6 px or less ...
    public const double DeadbandStepMarginPx=2;  // ... widened to one native tick of the moving lift (+2 px)
    public const double DeadbandCapPx=24;     // never more than one cell
    public const int FaultLimit=3;
    public const int ErrorBucketCount=7;      // |error| <=1, <=3, <=6, <=12, <=24, <=48, more (or not finite) px
    public const int LogBurst=20;
    public const long LogWindowMs=60000;
    public const int SendNone=0,SendOn=1,SendOff=2;
    public const int HostDrop=0,HostSub=1,HostAct=2,HostRide=3;

    // The rider test of Alpha1 Elevators.cs:202-204: the hero's cell column lies on the platform, its feet are within
    // 2 px of the platform top, and it is not moving up (no jump).
    public static bool RiderOn(int heroCx,double heroFoot,double heroVy,int liftCx,int liftWidth,double liftFoot)=>
        heroCx>=liftCx-liftWidth*.5 && heroCx<liftCx+liftWidth*.5 && Math.Abs(heroFoot-liftFoot)<=FootTolerancePx && heroVy>=0;
    public static bool RideFresh(long now,long receivedAt)=>receivedAt>0 && now>=receivedAt && now-receivedAt<=FreshMs;
    public static bool LiftMoving(double foot,double ty,double dy,double bdy)=>Math.Abs(foot-ty)>0.5 || Math.Abs(dy+bdy)>0.05;
    // While the host lift moves; after its stop StopHoldMs, or up to StopHoldMaxMs while the partner's POS has not yet
    // settled on the platform (its arrival lag; also hides a late STATE's one-tick arrival bounce of its lift).
    public static bool PinWindow(bool moving,long now,long lastMoving,bool settled)=>
        moving || lastMoving>0 && now>=lastMoving && now-lastMoving<=(settled?StopHoldMs:StopHoldMaxMs);
    // The partner's reported feet stand on the platform top (the rider test's 2 px).
    public static bool Settled(double remoteFoot,double liftFoot)=>Finite(remoteFoot) && Finite(liftFoot) && Math.Abs(remoteFoot-liftFoot)<=FootTolerancePx;
    // The partner's POS lies on the platform's columns (same margin as the ACT check) and inside the shaft (+-2 cells).
    public static bool RemoteOnLift(double x,double y,int liftCx,int liftWidth,int yTop,int yBottom)=>
        Finite(x) && Finite(y) && x>=(liftCx-liftWidth*.5-.35)*24 && x<(liftCx+liftWidth*.5+.35)*24 && y>=(yTop+1.0)*24-48 && y<=(yBottom+1.0)*24+48;
    public static double PinnedY(double liftFoot)=>liftFoot-PinLiftPx;
    // Client: RIDE on every SendIntervalMs while riding (and at once on a new track), one RIDE off when the ride ends.
    public static int SendAction(string track,string lastTrack,long now,long lastSend)
    {
        if(track!=null)return track!=lastTrack || now<lastSend || now-lastSend>=SendIntervalMs?SendOn:SendNone;
        return lastTrack!=null?SendOff:SendNone;
    }
    public static int HostKind(string kind,bool rideReady)=>kind=="SUB"?HostSub:kind=="ACT"?HostAct:kind=="RIDE" && rideReady?HostRide:HostDrop;
    public static bool ValidRide(string track,int ride,double offset,long sequence)=>
        sequence>0 && (ride==0 || ride==1) && Finite(offset) && Math.Abs(offset)<=24*64 && ValidTrack(track);
    // xLeft:xRight:yTop:yBottom:cWid exactly as ElevatorTrack prints it.
    public static bool ValidTrack(string track)
    {
        if(string.IsNullOrEmpty(track) || track.Length>200)return false;
        var parts=track.Split(':');if(parts.Length!=5)return false;
        var v=new int[5];
        for(int i=0;i<5;i++)if(!int.TryParse(parts[i],System.Globalization.NumberStyles.AllowLeadingSign,System.Globalization.CultureInfo.InvariantCulture,out v[i]))return false;
        return v[0]<=v[1] && v[2]<=v[3] && v[4]>0 && v[4]<=100 && track==v[0]+":"+v[1]+":"+v[2]+":"+v[3]+":"+v[4];
    }
    // (B) Skip a STATE that repeats the running motion (same track, direction and target) and is off by at most the
    // deadband: 6 px, or one native tick of the lift plus 2 px when that is larger (tick-phase jitter), at most one cell.
    public static double DeadbandPx(double liftVelocity)=>Math.Min(DeadbandCapPx,Math.Max(DeadbandFloorPx,Math.Abs(liftVelocity)*24+DeadbandStepMarginPx));
    public static bool SkipState(string stateTrack,string liftTrack,int stateMoveDir,int liftMoveDir,double stateTy,double liftTy,double errorPx,double liftVelocity)=>
        stateTrack!=null && stateTrack==liftTrack && stateMoveDir==liftMoveDir && stateTy==liftTy && Finite(errorPx) && Finite(liftVelocity) &&
        Math.Abs(errorPx)<=DeadbandPx(liftVelocity);
    public static int ErrorBucket(double absPx)=>!(absPx<=48)?6:absPx<=1?0:absPx<=3?1:absPx<=6?2:absPx<=12?3:absPx<=24?4:5;
    public static bool FaultTrips(int faults)=>faults>=FaultLimit;
    public static bool LogAllowed(long now,ref long windowStart,ref int used)
    {
        if(windowStart==0 || now<windowStart || now-windowStart>=LogWindowMs){windowStart=now;used=0;}
        if(used>=LogBurst)return false;
        used++;return true;
    }
    // ---- optional capability token (third '|' field of HELLO/WELCOME) ----
    // Index of the one complete "+csfelev2" in a build field, or -1: absent, repeated, or only a longer token such as
    // "+csfelev20". A token that is not exactly one complete occurrence is left in place for the strict validators.
    public static int TokenIndex(string build)
    {
        if(build==null)return -1;
        int at=build.IndexOf(Token,StringComparison.Ordinal);
        if(at<0 || at+Token.Length<build.Length && build[at+Token.Length]!='+')return -1;
        return build.IndexOf(Token,at+Token.Length,StringComparison.Ordinal)>=0?-1:at;
    }
    public static bool HasToken(string line)
    {
        var parts=(line??"").TrimEnd('\r','\n').Split('|');
        return parts.Length==3 && TokenIndex(parts[2])>=0;
    }
    public static bool TryStripToken(ref string line)
    {
        var parts=(line??"").TrimEnd('\r','\n').Split('|');if(parts.Length!=3)return false;
        int at=TokenIndex(parts[2]);if(at<0)return false;
        parts[2]=parts[2].Remove(at,Token.Length);line=string.Join("|",parts);return true;
    }
    public static string AppendToken(string line)=>(line??"").TrimEnd('\r','\n')+Token+"\n";
    // ---- IL pins (release 0.5.1, 378BE6B0; computed offline from the release DLL with System.Reflection.Metadata) ----
    public static readonly (string Owner,string Name,int Length,string Sha)[] Pins={
        ("ModEntry","ReceiveGhostCoords",2176,"44B8066FB1BB74781031AB22ED6B052B6EC0CA0C5894122A572D39C8E1230FAD"),
        ("ModEntry","TryGetClientIndex",51,"A38BBF6F6139A9F953F4D7D39A0C3AA9619D4A3C642809843FC29BA84161437E"),
        ("ModEntry","MarkGhostHeadDirty",36,"F4A95FCF0E6BE7C8ADC00574D98545980DBBEC318C976692DBD0DE3D24B4A47C"),
        ("NetNode","RejectIncompatiblePeer",96,"D53169BEC368195B4B05BE406A87287C9AF885324BDA6732DD5BAF4E49196611"),
        ("NetNode","TryValidateHandshakeLine",298,"A72DE51F47A25E64CBCA44127846F4B8E584CCE2F91566080444775FA0A4E36F"),
        ("NetNode","BuildHelloLine",82,"706C482CF3CC932687EAD21ED799580EF9CB20C9F49191BB0BE883E6C59A0DF6"),
        ("NetNode","BuildWelcomeLine",83,"6737EB605E1DE4D8C813185F19AFC55CCFC1A413628961A29DAB45B542533AC4")};
    public static bool PinMatches(string owner,string name,byte[] il)
    {
        foreach(var p in Pins)
            if(p.Owner==owner && p.Name==name)
                return il!=null && il.Length==p.Length && p.Sha.Length==64 &&
                    string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(il)),p.Sha,StringComparison.Ordinal);
        return false;
    }
    static bool Finite(double value)=>!double.IsNaN(value) && !double.IsInfinity(value);
}
