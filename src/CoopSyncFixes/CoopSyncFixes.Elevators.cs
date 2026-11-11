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
    static void InitializeExperimentalElevators(Harmony harmony)
    {
        if(ElevatorModuleInitialized || Disabled("ELEVATORS"))return;
        if(ModEntry._net?.IsAlive==true)throw new InvalidOperationException("Initialize elevator capability before connecting peers");
        InitializeElevatorProtocol(harmony);InitializeElevatorSynchronization(harmony);
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
    static string ElevatorFloor(NetNode net,Level level)
    {
        if(level?.map==null || !(bool)Field("s_levelIdentityReady") || (Field("currentLevel") as Level)?.HashlinkPointer!=level.HashlinkPointer)return null;
        var descriptor=Coordinator.GetMethod(net.IsHost?"GetCurrentHostDescriptor":"GetCurrentRemoteDescriptor",Flags).Invoke(null,null);
        if(descriptor==null)return null;
        return Property(descriptor,"SessionId")+":"+Property(descriptor,"RunId")+":"+Property(descriptor,"Sequence")+":"+
            level.map.id+":"+BitConverter.DoubleToInt64Bits(level.map.seed)+":"+(int)Field("s_levelIdentityToken");
    }
    static ElevatorSession RefreshElevatorSession(NetNode net,Level level)
    {
        var floor=ElevatorFloor(net,level);if(floor==null)return null;
        var session=ElevatorSessions.GetValue(net,_=>new ElevatorSession());var build=(int)Field("s_levelIdentityGeneration");
        if(session.Pointer!=level.HashlinkPointer || session.Build!=build || session.Floor!=floor)
        {
            session.Pointer=level.HashlinkPointer;session.Build=build;session.Floor=floor;session.Token=Guid.NewGuid().ToString("N");
            session.Epoch++;session.LocalBuildSerial++;session.RemoteEpoch=0;session.RemoteBoot=null;session.Latest.Clear();session.AcceptedSequences.Clear();session.Peers.Clear();session.LastSubscribe=0;
        }
        return session;
    }
    static string ElevatorTrack(Elevator elevator) => elevator.xLeft+":"+elevator.xRight+":"+elevator.yTop+":"+elevator.yBottom+":"+elevator.cWid;
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
    static bool LocalElevatorPlayerDowned() => (bool)typeof(ModEntry).GetMethod("IsLocalPlayerDowned",Flags).Invoke(null,null);
    static object ElevatorDownedRecord(int id) => ModEntry.Instance==null?null:
        ((System.Collections.IDictionary)typeof(ModEntry).GetField("_remoteDowned",ElevatorInstanceFlags).GetValue(ModEntry.Instance))[id];
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
        return value.Track==value.Left+":"+value.Right+":"+value.Top+":"+value.Bottom+":"+value.Width;
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
    static void BeforeElevatorPhysics(object value)
    {
        var elevator=value as Elevator;var net=ModEntry._net;if(!IsSupportedElevator(elevator) || net==null || net.IsHost || !net.IsAlive || elevator.destroyed)return;
        var level=ModEntry.me?._level;if(level?.HashlinkPointer!=elevator._level?.HashlinkPointer)return;
        var session=RefreshElevatorSession(net,level);if(session==null || !session.Latest.TryGetValue(ElevatorTrack(elevator),out var state))return;
        session.Latest.Remove(state.Track);
        if(!CurrentElevatorNode(net) || !FreshElevatorMessage(state,Environment.TickCount64) || state.Token!=session.Token || state.BuildSerial!=session.LocalBuildSerial || state.Floor!=session.Floor || state.Epoch!=session.RemoteEpoch || state.Boot!=session.RemoteBoot || !ValidElevatorState(state))return;
        // Track bounds alone are not sufficient if a native level contains duplicate tracks.
        if(CurrentSimpleElevators(level).Count(e=>ElevatorTrack(e)==state.Track)!=1){ElevatorRejectedStates++;return;}
        var hero=ModEntry.me;double oldFoot=(elevator.cy+elevator.yr)*24;
        bool riding=hero!=null && !hero.destroyed && hero.life>0 && !LocalElevatorPlayerDowned() && hero._level?.HashlinkPointer==level.HashlinkPointer &&
            hero.cx>=elevator.cx-elevator.cWid*.5 && hero.cx<elevator.cx+elevator.cWid*.5 &&
            Math.Abs((hero.cy+hero.yr)*24-oldFoot)<=2 && hero.dy+hero.bdy>=0;
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
            var remotes=(System.Collections.IDictionary)typeof(NetNode).GetField("_remotes",ElevatorInstanceFlags).GetValue(net);
            if(!remotes.Contains(id))return false;record=remotes[id];var localRecord=record;var type=record.GetType();
            object Get(string name)=>type.GetField(name,ElevatorInstanceFlags).GetValue(localRecord);
            // All accesses in this scope are managed records. No native API under this lock.
            if(!(bool)Get("HasRemote") || !(bool)Get("HasPosition"))return false;
            sequence=(long)Get("LastPositionSequence");x=(double)Get("X");y=(double)Get("Y");level=(string)Get("LevelId");life=(int)Get("Life");return true;
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
                if(message.Kind=="SUB")
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
                else if(session.Peers.TryGetValue(message.Sender,out var peer) && ReferenceEquals(connection,peer.Connection) && peer.Token==message.Token &&
                    message.BuildSerial==peer.BuildSerial && Environment.TickCount64-peer.LastSeen<=3000 &&
                    message.Boot==session.Boot && message.Epoch==session.Epoch && message.Request>peer.LastRequest)
                {
                    peer.LastRequest=message.Request;
                    var matches=CurrentSimpleElevators(level).Where(e=>ElevatorTrack(e)==message.Track).ToArray();
                    if(matches.Length==1 && RemoteCanActivateElevator(net,message.Sender,peer,matches[0],level) && matches[0].isReady())
                        ((SimpleElevator)matches[0]).onActivate(ModEntry.me,false);
                }
            }
            else
            {
                AcceptElevatorClientMessage(session,message);
            }
        }
        var now=Environment.TickCount64;
        if(!net.IsHost)
        {
            if(now-session.LastSubscribe>=1000){session.LastSubscribe=now;ElevatorSendHost(net,new ElevatorMessage{Kind="SUB",Token=session.Token,Floor=session.Floor});}
            return;
        }
        if(now-session.LastSend<100)return;session.LastSend=now;
        var elevators=CurrentSimpleElevators(level);var unique=elevators.GroupBy(ElevatorTrack).Where(g=>g.Count()==1).Select(g=>g.Single()).ToArray();
        foreach(var pair in session.Peers.ToArray())
        {
            if(now-pair.Value.LastSeen>3000)continue;
            foreach(var elevator in unique)
            {
                if(!session.Latest.TryGetValue(ElevatorTrack(elevator),out var state))continue;
                state.Token=pair.Value.Token;ElevatorSendPeer(net,pair.Key,pair.Value,state);
            }
        }
    }
}
