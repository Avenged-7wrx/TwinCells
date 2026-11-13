using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using dc.pr;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using R=CoopTransitionGateRules;

// CoopTransitionGate (Alpha2 U10): the doors behind Guillain and the Collector wait for the partner (PLAN-ALPHA2.md issue 3,
// OWNER-DECISIONS OD2/Q3). Rules and wire: CoopTransitionGateRules.cs, CoopTransitionGateWire.cs.
// Native hooks (DCCM, typed GameProxy events, matched by shape; the generated delegate types are not named here):
//   dc.en.inter.npc.Hook_PerkMaster.setDoor (f@26022) and Hook_CollectorPanelNpc.setDoor (f@14449). DCCM hooks the native
//   function entry, so the direct call from fixedUpdate (Call2 26022 / 14449) goes through it. The adapter asks
//   BeforeSetDoor(npc, wantOpen, kind) and calls orig(self, wantOpen) unchanged unless the answer is "hold". It never calls
//   orig(self, false) by itself (closeFast does not push a player out of the doorway).
// Harmony: NetNode.HandleLine prefix (the wire), ModEntry.OnFrameUpdate postfix (capability heartbeat, done resends, partner
//   level tracking, opening a held door at once when it becomes releasable, the "waiting" message).
// Fail-open: any fault passes the call to the native function; 8 consecutive / 64 total faults switch the gate off for the
// session (then every door behaves as vanilla). Kill switch COOP_TRANSITION_GATE_DISABLE=1 (hooks stay, all pass through).
public static class CoopTransitionGatePatch
{
    public const string Owner="CoopTransitionGate.Doors.20261112";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    delegate bool ClientIndexFn(int localId,int remoteId,out int index);
    static readonly object InstallGate=new();
    static volatile bool Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static bool Ready=>Published;
    internal static readonly R.Gate Gate=new();
    public static bool Running=>Published&&!Disabled&&!Gate.Tripped;
    static readonly List<(EventInfo Event,Delegate Hook)> Hooks=new();
    static Func<int,bool> RemoteDowned;static ClientIndexFn ClientIndex;static Func<int,string> ClientLabel;static Func<bool> InActualRun;
    static Action<string,double,double> PushMessage;
    static MethodInfo BeforeSetDoorMethod;

    // ---- session / level state (game thread) ----
    sealed class Entry
    {
        public R.GateKey Key;public string Text;public int Kind,Cx,Cy;public IntPtr LevelPtr;public bool LocalDone,Released,Held,LastWantOpen,Noted;public string Reason;
        public long HeldAtMs,LastWaitMs,ReleasedAtMs;public int Holds;
    }
    static NetNode Session;static int PartnerId;static long PartnerSeenMs,CapSentMs,ResendMs;static string PartnerLevel;
    // Level instance = level id (LevelMap.id): a visit to a sub-level or secret room of the same map keeps the entries.
    static string LevelId;
    static readonly Dictionary<string,Entry> Entries=new(StringComparer.Ordinal);
    static readonly List<R.GateKey> PartnerDone=new();
    static readonly R.LeftLevel Left=new();
    static Type BranchApi;static Func<bool> BranchActive;static MethodInfo BranchLocal;static long BranchLookMs;
    static long HoldCalls,Releases,PassCalls,OpenNowCalls,Messages,LocalDoneCount,PartnerDoneCount,CellJitter;
    static readonly Dictionary<string,long> LogAt=new(StringComparer.Ordinal);static int LogLines;

    public static void Install()
    {
        lock(InstallGate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier transition gate initialization failed: "+InitializationError);
            Harmony harmony=null;var targets=new List<MethodBase>();
            try
            {
                var mod=typeof(ModEntry);
                CoopTransitionGatePins.RequireExactBinaries(mod.Assembly,typeof(dc.en.Hero).Assembly);
                CoopTransitionGatePins.VerifySymbolic();
                var handle=CoopTransitionGateWire.Bind();
                var frame=CoopTransitionGatePins.Base(mod,"OnFrameUpdate",false,new[]{typeof(double)},typeof(void),CoopTransitionGatePins.OnFrameUpdateIl,CoopTransitionGatePins.OnFrameUpdateSha);
                RemoteDowned=(Func<int,bool>)Delegate.CreateDelegate(typeof(Func<int,bool>),CoopTransitionGatePins.Base(mod,"IsRemotePlayerDowned",true,new[]{typeof(int)},typeof(bool),CoopTransitionGatePins.RemoteDownedIl,CoopTransitionGatePins.RemoteDownedSha));
                ClientIndex=(ClientIndexFn)Delegate.CreateDelegate(typeof(ClientIndexFn),CoopTransitionGatePins.Base(mod,"TryGetClientIndex",true,new[]{typeof(int),typeof(int),typeof(int).MakeByRefType()},typeof(bool),CoopTransitionGatePins.ClientIndexIl,CoopTransitionGatePins.ClientIndexSha));
                ClientLabel=(Func<int,string>)Delegate.CreateDelegate(typeof(Func<int,string>),CoopTransitionGatePins.Base(mod,"GetClientLabel",true,new[]{typeof(int)},typeof(string),CoopTransitionGatePins.ClientLabelIl,CoopTransitionGatePins.ClientLabelSha));
                var lobby=mod.Assembly.GetType("DeadCellsMultiplayerMod.LobbySession",true);
                InActualRun=(Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),CoopTransitionGatePins.Base(lobby,"IsInActualRun",true,Type.EmptyTypes,typeof(bool),CoopTransitionGatePins.InActualRunIl,CoopTransitionGatePins.InActualRunSha));
                var ui=mod.Assembly.GetType("DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI.MultiplayerUI",true);
                PushMessage=(Action<string,double,double>)Delegate.CreateDelegate(typeof(Action<string,double,double>),CoopTransitionGatePins.Base(ui,"PushSystemMessage",true,new[]{typeof(string),typeof(double),typeof(double)},typeof(void),CoopTransitionGatePins.PushMessageIl,CoopTransitionGatePins.PushMessageSha));
                CoopTransitionGatePins.Base(typeof(NetNode),"TryGetRemote",false,new[]{typeof(int).MakeByRefType(),typeof(double).MakeByRefType(),typeof(double).MakeByRefType()},typeof(bool),CoopTransitionGatePins.TryGetRemoteIl,CoopTransitionGatePins.TryGetRemoteSha);
                CoopTransitionGatePins.Base(typeof(NetNode),"TryGetRemotePosition",false,new[]{typeof(int),typeof(double).MakeByRefType(),typeof(double).MakeByRefType(),typeof(string).MakeByRefType()},typeof(bool),CoopTransitionGatePins.RemotePositionIl,CoopTransitionGatePins.RemotePositionSha);
                BeforeSetDoorMethod=typeof(CoopTransitionGatePatch).GetMethod(nameof(BeforeSetDoor),S);
                Disabled=Environment.GetEnvironmentVariable("COOP_TRANSITION_GATE_DISABLE")=="1";
                harmony=new Harmony(Owner);
                targets.Add(handle);
                harmony.Patch(handle,prefix:new HarmonyMethod(typeof(CoopTransitionGateWire).GetMethod("BeforeHandleLine",S)){priority=Priority.First});
                targets.Add(frame);
                harmony.Patch(frame,postfix:new HarmonyMethod(typeof(CoopTransitionGatePatch).GetMethod(nameof(AfterFrame),S)){priority=Priority.Normal});
                foreach(var t in targets)
                {
                    var info=Harmony.GetPatchInfo(t);
                    int pre=info?.Prefixes.Count(p=>p.owner==Owner)??0,post=info?.Postfixes.Count(p=>p.owner==Owner)??0;
                    if(pre+post!=1||info.Transpilers.Any(p=>p.owner==Owner)||info.Finalizers.Any(p=>p.owner==Owner))throw new InvalidOperationException("Transition gate patch on "+t.Name+" was not installed exactly once");
                }
                Subscribe("dc.en.inter.npc.Hook_PerkMaster",R.KindPerk);
                Subscribe("dc.en.inter.npc.Hook_CollectorPanelNpc",R.KindCollector);
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;
                foreach(var h in Hooks)try{h.Event.RemoveEventHandler(null,h.Hook);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                Hooks.Clear();
                if(harmony!=null)foreach(var t in targets)try{harmony.Unpatch(t,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    // hook_setDoor(orig_setDoor orig, <Npc> self, bool wantOpen) -> if(BeforeSetDoor(self,wantOpen,kind)) orig.Invoke(self,wantOpen)
    static void Subscribe(string hookType,int kind)
    {
        var ev=typeof(dc.en.Hero).Assembly.GetType(hookType,true).GetEvent("setDoor")??throw new InvalidOperationException("Native hook missing: "+hookType+".setDoor");
        var invoke=ev.EventHandlerType.GetMethod("Invoke");var types=invoke.GetParameters().Select(p=>p.ParameterType).ToArray();
        if(invoke.ReturnType!=typeof(void)||types.Length!=3||types[2]!=typeof(bool)||!typeof(dc.en.inter.Npc).IsAssignableFrom(types[1]))throw new InvalidOperationException("Native setDoor hook ABI changed: "+hookType);
        var orig=types[0].GetMethod("Invoke");
        if(orig?.ReturnType!=typeof(void)||!orig.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{types[1],typeof(bool)}))throw new InvalidOperationException("Native setDoor orig ABI changed: "+hookType);
        var dm=new DynamicMethod("CoopTransitionGate_"+types[1].Name+"_setDoor",typeof(void),types,typeof(CoopTransitionGatePatch),true);
        var il=dm.GetILGenerator();var skip=il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Ldc_I4,kind);il.Emit(OpCodes.Call,BeforeSetDoorMethod);il.Emit(OpCodes.Brfalse,skip);
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Callvirt,orig);
        il.MarkLabel(skip);il.Emit(OpCodes.Ret);
        var hook=dm.CreateDelegate(ev.EventHandlerType);RuntimeHelpers.PrepareDelegate(hook);
        ev.AddEventHandler(null,hook);Hooks.Add((ev,hook));
    }

    internal static void Fault(string what,Exception error)
    {
        bool before=Gate.Tripped,tripped=Gate.Fail();
        if(Gate.Faults==1)try{Serilog.Log.Warning("[CoopTransitionGate] {What} check failed; the door behaves as without the gate: {Error}",what,error.GetBaseException().Message);}catch{}
        if(tripped&&!before)try{Serilog.Log.Warning("[CoopTransitionGate] Transition gate switched off for this session after {Faults} faults",Gate.Faults);}catch{}
    }
    static void Info(string key,long cooldownMs,string template,params object[] values)
    {
        long now=Environment.TickCount64;
        if(LogLines>=600||(LogAt.TryGetValue(key,out var at)&&now-at<cooldownMs))return;
        LogAt[key]=now;LogLines++;
        try{Serilog.Log.Information("[CoopTransitionGate] "+template,values);}catch{}
    }
    static bool Russian()
    {
        try{var s=dc.Lang.Class.LANG?.ToString()??"en";int colon=s.IndexOf(':');if(colon>0)s=s.Substring(0,colon);return s.ToLowerInvariant()=="ru";}catch{return false;}
    }
    static string PartnerName(NetNode net)
    {
        try{if(net!=null&&PartnerId>0&&ClientIndex(net.id,PartnerId,out int slot)){var n=ClientLabel(slot);if(!string.IsNullOrWhiteSpace(n))return n;}}catch{}
        return null;
    }
    static void Say(string what,NetNode net)
    {
        var text=R.Text(what,Russian(),PartnerName(net));if(text==null)return;
        try{PushMessage(text,5.0,1.0);Messages++;}catch(Exception error){Fault("Gate message",error);}
    }

    // ---- branch key from CoopBranchVisibilityFix (U9), looked up by name; without it the map id rule of the rules file ----
    static string BranchOf(Level level)
    {
        bool sub=level.isSubLevel,secret=level.isSecret;
        long now=Environment.TickCount64;
        if(BranchApi==null&&(BranchLookMs==0||now-BranchLookMs>=5000))
        {
            BranchLookMs=now==0?1:now;
            Type found=null;int n=0;
            {Type t=null;try{t=global::CoopPackRuntime.PackParts.FindType("CoopBranchVisibilityFix","CoopBranchVisibilityApi");}catch{}if(t!=null){found=t;n++;}}
            var active=n==1?found.GetProperty("Active",S)?.GetGetMethod():null;
            var local=n==1?found.GetMethod("TryGetLocalBranch",S,null,new[]{typeof(string).MakeByRefType(),typeof(int).MakeByRefType()},null):null;
            if(active?.ReturnType==typeof(bool)&&local?.ReturnType==typeof(bool))
            {
                BranchApi=found;BranchActive=(Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),active);BranchLocal=local;
                Info("branchapi",60000,"Branch key from CoopBranchVisibilityFix");
            }
        }
        var hero=Game.Class.ME?.hero;
        if(BranchApi!=null&&BranchActive()&&hero?._level!=null&&hero._level.HashlinkPointer==level.HashlinkPointer)
        {
            var args=new object[]{null,0};
            if((bool)BranchLocal.Invoke(null,args)&&(string)args[0]==level.map?.id?.ToString())return R.Branch(true,(int)args[1],sub,secret);
        }
        return R.Branch(false,-1,sub,secret);
    }

    // ---- the decision (native hook adapter) ----
    public static bool BeforeSetDoor(dc.en.inter.Npc npc,bool wantOpen,int kind)
    {
        if(!Published)return true;
        try
        {
            if(npc==null||npc.HashlinkPointer==IntPtr.Zero)return true;
            var net=ModEntry._net;bool session=net!=null&&net.IsAlive;
            if(!wantOpen||!session||Disabled||Gate.Tripped)
            {
                if(!wantOpen)foreach(var e in Entries.Values)if(e.Kind==kind&&R.SameCell(e.Cx,e.Cy,npc.cx,npc.cy))e.LastWantOpen=false;
                PassCalls++;Gate.Ok();return true;
            }
            var level=npc._level;
            // Only the NPC of the level the local hero is in is gated (an NPC of another, inactive level: vanilla).
            if(level?.map==null||Game.Class.ME?.hero?._level?.HashlinkPointer!=level.HashlinkPointer){PassCalls++;Gate.Ok();return true;}
            EnsureSession(net);EnsureLevel(level.map.id?.ToString());
            if(!R.TryKey(level.map.id?.ToString(),BranchOf(level),R.NpcName(kind),npc.cx,npc.cy,out var key)){PassCalls++;Gate.Ok();return true;}
            // Anchored: a call whose NPC cell reads a cell off (R.CellTolerance) finds the existing entry and keeps its key.
            var entry=EntryFor(key,kind);
            if(entry==null){var t=key.ToString();Entries[t]=entry=new Entry{Key=key,Text=t,Kind=kind,Cx=npc.cx,Cy=npc.cy,LevelPtr=level.HashlinkPointer};}
            else if(entry.Cx!=npc.cx||entry.Cy!=npc.cy){CellJitter++;Info("jitter:"+entry.Text,60000,"NPC cell read {Cx}:{Cy} for the gate at {Gate}: same gate",npc.cx,npc.cy,entry.Text);}
            var text=entry.Text;key=entry.Key;
            entry.LevelPtr=level.HashlinkPointer;
            entry.LastWantOpen=true;
            long now=Environment.TickCount64;
            if(!entry.LocalDone)
            {
                entry.LocalDone=true;LocalDoneCount++;
                if(PartnerId>0)CoopTransitionGateWire.Send(net,PartnerId,R.Done(net.id,key));
                Info("done:"+text,60000,"Local player done at {Gate}",text);
            }
            var verdict=R.Decide(InputsFor(net,entry,true),out var reason);
            switch(verdict)
            {
                case R.Verdict.Hold:
                    HoldCalls++;entry.Holds++;
                    if(!entry.Held){entry.Held=true;entry.HeldAtMs=now;Info("hold:"+text,60000,"Holding the door at {Gate}: waiting for partner {Partner} (door closed={Closed})",text,PartnerId,DoorClosedText(level,npc));}
                    var hero=Game.Class.ME?.hero;
                    if(hero!=null&&R.ShowWait(entry.Held,entry.Released,R.Near(hero.cx,hero.cy,npc.cx,npc.cy),entry.LastWaitMs,now)){entry.LastWaitMs=now;Say("waiting",net);}
                    Gate.Ok();return false;
                case R.Verdict.Release:
                    Release(entry,reason,net,now);Gate.Ok();return true;
                default:
                    PassCalls++;Gate.Ok();return true;
            }
        }
        catch(Exception error){Fault("Door gate",error);return true;}
    }
    // The entry of this level instance for the same NPC (R.SameGate: same level, kind, branch or "-", cell within
    // R.CellTolerance), or null.
    static Entry EntryFor(R.GateKey key,int kind)
    {
        foreach(var e in Entries.Values)if(e.Kind==kind&&R.SameGate(e.Key,key))return e;
        return null;
    }
    static void Release(Entry entry,string reason,NetNode net,long now)
    {
        if(entry.Released)return;
        entry.Released=true;entry.Reason=reason;entry.ReleasedAtMs=now;Releases++;
        Info("release:"+entry.Text,60000,"Door released at {Gate}: {Reason} (held {Held:0.0} s)",entry.Text,reason,entry.Held?(now-entry.HeldAtMs)/1000.0:0.0);
        if(!entry.Noted&&R.NoteRelease(entry.Held,reason)){entry.Noted=true;Say(reason,net);}
    }
    static R.Inputs InputsFor(NetNode net,Entry entry,bool wantOpen)
    {
        bool connected=net.HasRemote&&PartnerId>0;
        bool single=!net.IsHost||NetNode.ConnectedClientCount<=1;
        bool done=false;foreach(var k in PartnerDone)if(R.SameGate(k,entry.Key)){done=true;break;}
        bool downed=false;try{downed=PartnerId>0&&RemoteDowned(PartnerId);}catch{}
        return new R.Inputs{WantOpen=wantOpen,Enabled=!Disabled&&!Gate.Tripped,Session=true,Connected=connected,SinglePartner=single,
            PartnerCapable=R.Capable(PartnerSeenMs,Environment.TickCount64),PartnerDowned=downed,PartnerLeftLevel=Left.Latched,PartnerDone=done,Released=entry.Released};
    }
    static string DoorClosedText(Level level,dc.en.inter.Npc npc)
    {
        try
        {
            var room=level.map.getRoomAt(npc.cx,npc.cy);var entities=level.entities;var states=new List<string>();
            if(entities!=null)for(int i=0;i<entities.length;i++)
                if(entities.getDyn(i) is dc.en.inter.door.TriggeredDoor door&&!door.destroyed&&level.map.getRoomAt(door.cx,door.cy)?.HashlinkPointer==room?.HashlinkPointer)
                    states.Add(door._closed?(door.broken?"broken":"yes"):"no");
            return states.Count==0?"none":string.Join(",",states);
        }
        catch{return "?";}
    }
    static void EnsureSession(NetNode net)
    {
        if(ReferenceEquals(net,Session))return;
        Session=net;PartnerId=0;PartnerSeenMs=0;CapSentMs=0;ResendMs=0;PartnerLevel=null;PartnerDone.Clear();Entries.Clear();Left.Reset();LevelId=null;
        CoopTransitionGateWire.Clear();
    }
    static void EnsureLevel(string levelId)
    {
        if(string.IsNullOrWhiteSpace(levelId)||string.Equals(levelId,LevelId,StringComparison.Ordinal))return;
        LevelId=levelId;Entries.Clear();Left.Reset();ResendMs=0;
    }

    // ---- frame tick ----
    static void AfterFrame()
    {
        if(!Published)return;
        if(Disabled||Gate.Tripped){CoopTransitionGateWire.Clear();return;}
        try
        {
            var net=ModEntry._net;
            if(net==null||!net.IsAlive){if(Session!=null){EnsureSession(null);}CoopTransitionGateWire.Clear();return;}
            EnsureSession(net);
            long now=Environment.TickCount64;
            int partner=0;
            if(net.HasRemote&&net.TryGetRemote(out var id,out _,out _))partner=id;
            if(partner!=PartnerId){PartnerId=partner;PartnerSeenMs=0;PartnerDone.Clear();PartnerLevel=null;Left.Reset();}
            while(CoopTransitionGateWire.Dequeue(out var m))
            {
                if(m.Sender!=PartnerId||PartnerId<=0)continue;
                PartnerSeenMs=now==0?1:now;
                if(m.Kind==R.Kind.Done&&!PartnerDone.Any(k=>k.ToString()==m.Key.ToString()))
                {
                    PartnerDone.Add(m.Key);PartnerDoneCount++;
                    Info("pdone:"+m.Key,60000,"Partner {Partner} done at {Gate}",PartnerId,m.Key.ToString());
                }
            }
            var hero=Game.Class.ME?.hero;var level=hero?._level;
            if(level?.map!=null)EnsureLevel(level.map.id?.ToString());
            if(PartnerId>0&&level?.map!=null)
            {
                string partnerLevel=null;
                if(net.TryGetRemotePosition(PartnerId,out _,out _,out var pl))partnerLevel=pl;
                if(!string.Equals(partnerLevel,PartnerLevel,StringComparison.Ordinal)){PartnerLevel=partnerLevel;PartnerDone.Clear();}
                bool known=!string.IsNullOrWhiteSpace(partnerLevel);
                bool wasLeft=Left.Latched;
                Left.Step(known,known&&string.Equals(partnerLevel,level.map.id?.ToString(),StringComparison.Ordinal),now);
                if(!wasLeft&&Left.Latched)Info("left:"+level.HashlinkPointer.ToInt64(),60000,"Partner {Partner} left the level ({Level})",PartnerId,partnerLevel??"?");
            }
            bool run=false;try{run=InActualRun();}catch{}
            if(run&&PartnerId>0)
            {
                if(R.Due(CapSentMs,now,R.CapabilitySendMs)){CapSentMs=now==0?1:now;CoopTransitionGateWire.Send(net,PartnerId,R.Capability(net.id));}
                if(R.Due(ResendMs,now,R.DoneResendMs))
                {
                    ResendMs=now==0?1:now;
                    foreach(var e in Entries.Values)if(e.LocalDone)CoopTransitionGateWire.Send(net,PartnerId,R.Done(net.id,e.Key));
                }
            }
            foreach(var e in Entries.Values.ToList())
            {
                if(!e.Held||e.Released)continue;
                var verdict=R.Decide(InputsFor(net,e,e.LastWantOpen),out var reason);
                if(R.OpenNow(e.Held,e.Released,e.LastWantOpen,verdict))
                {
                    Release(e,reason,net,now);
                    var npc=FindNpc(level,e);
                    if(npc!=null){OpenNowCalls++;Open(npc,e.Kind);}
                }
            }
            Gate.Ok();
        }
        catch(Exception error){Fault("Gate tick",error);}
    }
    static dc.en.inter.Npc FindNpc(Level level,Entry e)
    {
        if(level?.entities==null||level.HashlinkPointer!=e.LevelPtr)return null;   // only while the hero is in the NPC's own level
        for(int i=0;i<level.entities.length;i++)
        {
            var x=level.entities.getDyn(i);
            if(e.Kind==R.KindPerk&&x is dc.en.inter.npc.PerkMaster p&&!p.destroyed&&R.SameCell(p.cx,p.cy,e.Cx,e.Cy))return p;
            if(e.Kind==R.KindCollector&&x is dc.en.inter.npc.CollectorPanelNpc c&&!c.destroyed&&R.SameCell(c.cx,c.cy,e.Cx,e.Cy))return c;
        }
        return null;
    }
    // Through the proxy, i.e. through the native hook: the latched entry passes it to the native setDoor(true).
    static void Open(dc.en.inter.Npc npc,int kind)
    {
        if(npc is dc.en.inter.npc.PerkMaster p)p.setDoor(true);
        else if(npc is dc.en.inter.npc.CollectorPanelNpc c)c.setDoor(true);
    }

    // ---- probe read-out ----
    public static Dictionary<string,object> Snapshot()
    {
        var entries=Entries.Values.Select(e=>(object)new Dictionary<string,object>{["key"]=e.Text,["kind"]=R.NpcName(e.Kind),["cx"]=e.Cx,["cy"]=e.Cy,["localDone"]=e.LocalDone,
            ["held"]=e.Held,["holds"]=e.Holds,["released"]=e.Released,["reason"]=e.Reason,["lastWantOpen"]=e.LastWantOpen,
            ["partnerDone"]=PartnerDone.Any(k=>R.SameGate(k,e.Key))}).ToList();
        long now=Environment.TickCount64;
        return new Dictionary<string,object>{["ready"]=Published,["running"]=Running,["disabled"]=Disabled,["tripped"]=Gate.Tripped,["faults"]=Gate.Faults,
            ["partner"]=PartnerId,["partnerCapable"]=R.Capable(PartnerSeenMs,now),["partnerSeenAgoMs"]=PartnerSeenMs==0?-1:now-PartnerSeenMs,["partnerLevel"]=PartnerLevel,
            ["partnerLeftLevel"]=Left.Latched,["partnerDone"]=PartnerDone.Select(k=>k.ToString()).ToList(),["level"]=LevelId,["entries"]=entries,
            ["holdCalls"]=HoldCalls,["releases"]=Releases,["passCalls"]=PassCalls,["openNow"]=OpenNowCalls,["messages"]=Messages,["localDone"]=LocalDoneCount,["partnerDoneLines"]=PartnerDoneCount,["cellJitter"]=CellJitter,
            ["branchFromPart"]=BranchApi!=null,["linesReceived"]=CoopTransitionGateWire.ReceivedCount,["linesDropped"]=CoopTransitionGateWire.DroppedCount,
            ["linesSent"]=CoopTransitionGateWire.SentCount,["sendFaults"]=CoopTransitionGateWire.SendFaultCount,["symbolicPin"]=CoopTransitionGatePins.SymbolicPin};
    }
}
