using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using dc.en;
using dc.pr;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Newtonsoft.Json;
using Serilog;

public sealed partial class CoopSyncFixes
{
    private sealed class MobBirth
    {
        public int Id,BirthId;public string Type;public double X,Y;public bool Bootstrap,Dead;
        public double CurrentX,CurrentY;public int Dir,Life,MaxLife;public string State;
    }
    private sealed class MobManifest
    {
        public int V=2,Generation,HighWatermark;public string Level;public double Seed,Frame;public long Epoch,Revision;
        public List<MobBirth> Entries=new();
    }
    private sealed class HostManifestStore
    {
        public string RunKey;public MobManifest Active;public MapHistory Current;
        public readonly Dictionary<IntPtr,MapHistory> Maps=new();
    }
    private static readonly ConditionalWeakTable<NetNode,HostManifestStore> HostManifests=new();
    private static void InitializeManifests(Harmony harmony)
    {
        if(Disabled("WORLD"))return;
        Patch(harmony,Mobs,"TryGetMobSyncId",null,nameof(ObserveRuntimeBirth));
        Patch(harmony,Mobs,"RememberHostDeathTombstoneLocked",null,nameof(ObserveRecordedDeath));
        harmony.Patch(typeof(NetNode).GetMethod("SendMobDie"),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ObserveSentDeath),Flags)));
        Patch(harmony,Mobs,"RunClientIncomingFrameConsume",nameof(WaitForManifestBindings));
        Patch(harmony,Mobs,"ResetMobTrackingLocked",nameof(SaveVerifiedClientBirths));
        InitializeSnapshotFence(harmony);
        CineGate.Alpha1Rule=Disabled("CINEGATE");
        Log.Information("[CoopSyncFixes] Cinematic gate: {CineGateMode:l}",Disabled("ACTIVATION")?
            "off (COOP_SYNC_DISABLE_ACTIVATION=1): incoming mob sync is never held for a cinematic":CineGate.Alpha1Rule?
            "Alpha1 rule (COOP_SYNC_DISABLE_CINEGATE=1): incoming mob sync held during every cinematic":
            "incoming mob sync held during a cinematic only while a level change is in progress");
    }
    private static void CaptureBootstrapManifest(NetNode net,Level level)
    {
        var descriptor=Coordinator.GetMethod("GetCurrentHostDescriptor",Flags).Invoke(null,null);
        if(descriptor==null)return;
        var run=Property(descriptor,"SessionId")+":"+Property(descriptor,"RunId")+":"+Property(descriptor,"Sequence");
        lock(Gate)
        {
            var store=HostManifests.GetValue(net,_=>new HostManifestStore());
            if(store.RunKey!=run){store.Maps.Clear();store.Active=null;store.Current=null;store.RunKey=run;}
            var epoch=EnsureHostEpoch(net,level).Epoch;
            if(store.Active==null || store.Active.Epoch!=epoch)
            {
                var history=GetMapHistory(store,level.map);
                var previous=history.Manifest;
                store.Active=new MobManifest {Level=level.map.id.ToString(),Seed=level.map.seed,Epoch=epoch,Generation=(int)Field("s_levelIdentityToken")};
                store.Current=history;
                foreach(DictionaryEntry pair in (IDictionary)Field("MobToId"))CaptureBirth(store.Active,pair.Key as Mob,(int)pair.Value,true,history);
                if(!Disabled("HISTORY") && previous!=null)
                    foreach(var birth in previous.Entries)if(birth.Dead)
                    {
                        // Living bindings come first; old dead identities then consume only
                        // remaining bootstrap natives on a freshly generated joining peer.
                        store.Active.Entries.Add(birth);store.Active.Revision++;
                    }
                if(store.Active.Entries.Count>8192)throw new InvalidOperationException("Resumed world manifest capacity exceeded");
                history.Manifest=store.Active;
            }
            else foreach(DictionaryEntry pair in (IDictionary)Field("MobToId"))CaptureBirth(store.Active,pair.Key as Mob,(int)pair.Value,true,store.Current);
        }
    }
    private static void CaptureBirth(MobManifest manifest,Mob mob,int id,bool bootstrap,MapHistory history)
    {
        if(mob==null || id<=0)return;
        foreach(var old in manifest.Entries)if(old.Id==id){old.Bootstrap|=bootstrap;return;}
        if(manifest.Entries.Count>=8192)throw new InvalidOperationException("World manifest enemy capacity exceeded");
        var type=(string)Invoke("BuildMobStateTypeSignature",mob);
        NativeBirth prior=null;
        if(!Disabled("HISTORY") && history!=null && history.Births.TryGetValue(mob.HashlinkPointer,out var found) &&
            found.Native!=null && !found.Native.destroyed && found.Uid==mob.__uid && found.Native.HashlinkPointer==mob.HashlinkPointer && !found.Birth.Dead && found.Birth.Type==type)prior=found;
        var birth=new MobBirth {Id=id,BirthId=prior?.Birth.BirthId ?? id,Type=type,X=prior?.Birth.X ?? (double)Invoke("GetWorldX",mob),
            Y=prior?.Birth.Y ?? (double)Invoke("GetWorldY",mob),Bootstrap=prior?.Birth.Bootstrap ?? bootstrap};
        manifest.Entries.Add(birth);manifest.Revision++;
        if(history!=null)history.Births[mob.HashlinkPointer]=new NativeBirth {Native=mob,Birth=birth,Uid=mob.__uid};
    }
    private static void ObserveRuntimeBirth(Mob mob,int syncId,bool __result)
    {
        var net=ModEntry._net;if(!__result || net==null || !net.IsHost || !HostManifests.TryGetValue(net,out var store))return;
        lock(Gate)
        {
            if(store.Active==null || store.Active.Generation!=(int)Field("s_levelIdentityToken"))return;
            CaptureBirth(store.Active,mob,syncId,false,store.Current);
        }
    }
    private static void RememberManifestDeath(NetNode net,int id,int generation)
    {
        if(net==null || !net.IsHost || !HostManifests.TryGetValue(net,out var store))return;
        lock(Gate)
        {
            var active=store.Active;if(active==null || active.Generation!=generation)return;
            foreach(var record in active.Entries)if(record.Id==id)
            {if(!record.Dead){record.Dead=true;active.Revision++;}return;}
        }
    }
    private static void ObserveSentDeath(NetNode __instance,int mobIndex,int generation)=>RememberManifestDeath(__instance,mobIndex,generation);
    private static void ObserveRecordedDeath(int syncId,int generation)=>RememberManifestDeath(ModEntry._net,syncId,generation);
    private static string BuildManifestSnapshot(NetNode net,long epoch,out long revision)
    {
        revision=0;
        lock(Gate)
        {
            if(!HostManifests.TryGetValue(net,out var store) || store.Active?.Epoch!=epoch)return null;
            var active=store.Active;var forward=(IDictionary)Field("IdToMob");var reverse=(IDictionary)Field("MobToId");
            var snapshot=new MobManifest {Level=active.Level,Seed=active.Seed,Generation=active.Generation,Epoch=epoch,Revision=active.Revision,
                Frame=(double)Invoke("GetCurrentFrame",new object[] {null}),HighWatermark=(int)Field("nextRuntimeSyncId")-1};
            foreach(var birth in active.Entries)
            {
                var mob=forward.Contains(birth.Id)?forward[birth.Id] as Mob:null;
                if(!birth.Dead && (mob==null || mob.destroyed || !reverse.Contains(mob) || (int)reverse[mob]!=birth.Id))continue;
                var item=new MobBirth {Id=birth.Id,BirthId=birth.BirthId,Type=birth.Type,X=birth.X,Y=birth.Y,Dead=birth.Dead,Bootstrap=birth.Bootstrap,
                    CurrentX=birth.X,CurrentY=birth.Y,Life=0,MaxLife=1,Dir=1,State=""};
                if(!birth.Dead)
                {
                    item.CurrentX=(double)Invoke("GetWorldX",mob);item.CurrentY=(double)Invoke("GetWorldY",mob);item.Life=mob.life;item.MaxLife=mob.maxLife;
                    item.Dir=mob.dir;item.State=(string)Invoke("BuildHostMobStatePayload",mob);
                }
                snapshot.Entries.Add(item);
            }
            revision=active.Revision;return JsonConvert.SerializeObject(snapshot);
        }
    }
    private static void ValidateManifest(MobManifest manifest,WorldHeader header)
    {
        if(manifest==null || manifest.V!=2 || manifest.Level!=header.Level || manifest.Epoch!=header.Epoch || manifest.Generation!=header.Generation ||
            manifest.Revision!=header.MobRevision || BitConverter.DoubleToInt64Bits(manifest.Seed)!=BitConverter.DoubleToInt64Bits(header.Seed) ||
            !double.IsFinite(manifest.Frame) || manifest.Entries==null || manifest.Entries.Count>8192)throw new InvalidOperationException("Invalid mob snapshot identity");
        var seen=new HashSet<int>();var births=new HashSet<int>();
        foreach(var item in manifest.Entries)
            if(item.Id<=0 || item.Id>manifest.HighWatermark || item.BirthId<=0 || !seen.Add(item.Id) || !births.Add(item.BirthId) || string.IsNullOrEmpty(item.Type) || item.Type.Length>512 ||
                !double.IsFinite(item.X) || !double.IsFinite(item.Y) || !double.IsFinite(item.CurrentX) || !double.IsFinite(item.CurrentY) ||
                item.MaxLife<0 || item.Life<0)throw new InvalidOperationException("Invalid mob snapshot entry");
    }
    private static bool WaitForManifestBindings(NetNode net)
    {
        if(!Disabled("ACTIVATION") && net!=null && !net.IsHost && HoldConsumeForCinematic())return false;
        EnsureClientActivation(net);return WireConsumeReady(net) && (!PendingWorld(net,out var state) || (!state.ActivationOnly && state.ManifestApplied));
    }
    // Alpha2 U2 (PLAN-ALPHA2.md issues 12 and 10 step 1). Alpha1 skipped the client's whole incoming consume (host mob
    // states, moves, attacks, deaths, hits and the base mod's boss death watchdog) while ANY cinematic ran, so every dialog,
    // item, chest or boss scene froze the replicas, and a scene that waits for a boss death (the Behemoth outro) could
    // never receive it. The hold protects a level change, so it now applies only while one is in progress: a
    // LevelTransition, a level identity that is not ready, no live hero or level, or a hero whose native level is not the
    // tracked one (the Z-door, teleporter and sub-level window before ReconcileActiveBranch runs). Every other cinematic
    // falls through to the unchanged tail above (EnsureClientActivation does nothing during a cinematic: ActiveBranch() is
    // null). COOP_SYNC_DISABLE_CINEGATE=1 restores the Alpha1 rule; a fault in the reads holds that frame and, after
    // CineGateMonitor.FaultLimit faults, restores the Alpha1 rule until the game restarts; COOP_SYNC_DISABLE_ACTIVATION=1
    // still removes the hold altogether, as before.
    internal const int CineGateNone=0,CineGateOpen=1,CineGateHoldTransition=2,CineGateHoldIdentity=3,CineGateHoldNoLevel=4,
        CineGateHoldBranch=5,CineGateHoldAlpha1=6,CineGateVerdicts=7;
    internal static int CineGateVerdict(bool cine,bool alpha1Rule,bool levelTransition,bool identityReady,bool heroLive,IntPtr heroLevel,IntPtr trackedLevel)
    {
        if(!cine)return CineGateNone;
        if(alpha1Rule)return CineGateHoldAlpha1;
        if(levelTransition)return CineGateHoldTransition;
        if(!identityReady)return CineGateHoldIdentity;
        if(!heroLive || heroLevel==IntPtr.Zero || trackedLevel==IntPtr.Zero)return CineGateHoldNoLevel;
        return heroLevel==trackedLevel?CineGateOpen:CineGateHoldBranch;
    }
    internal static bool CineGateHolds(int verdict)=>verdict>=CineGateHoldTransition;
    internal static string CineGateReason(int verdict)=>verdict switch {CineGateNone=>"none",CineGateOpen=>"open",CineGateHoldTransition=>"level-transition",
        CineGateHoldIdentity=>"identity-not-ready",CineGateHoldNoLevel=>"no-hero-level",CineGateHoldBranch=>"branch-not-reconciled",CineGateHoldAlpha1=>"alpha1-rule",_=>"unknown"};
    internal static string CineGateName(int verdict)=>CineGateHolds(verdict)?"held:"+CineGateReason(verdict):CineGateReason(verdict);
    // Per-cinematic bookkeeping, the fault latch and the rate-limited lines of the gate. Plain instance state, touched only
    // on the game thread (the consume prefix), so the managed checks drive it without a game.
    internal sealed class CineGateMonitor
    {
        internal const int FaultLimit=3,LogBurst=12;
        internal const long LogWindowMs=60000;
        internal static readonly long[] LongMarksMs={20000,60000,180000};
        internal bool Alpha1Rule,Latched;internal int Faults;internal string LastFault;
        internal IntPtr Cine;internal string Type;internal long StartedMs;internal int Verdict=CineGateNone,LongMark;
        internal readonly long[] Frames=new long[CineGateVerdicts],Totals=new long[CineGateVerdicts];
        internal long Episodes,LinesWritten,LinesSuppressed;
        long windowStart,pendingSuppressed;int windowLines;bool windowOpen;
        internal bool UseAlpha1=>Alpha1Rule || Latched;
        // One call per client frame of the prefix (IntPtr.Zero: no cinematic). Returns a line to log, or null.
        internal string Observe(IntPtr cine,string type,int verdict,long nowMs)
        {
            if(verdict<CineGateNone || verdict>=CineGateVerdicts)throw new ArgumentOutOfRangeException(nameof(verdict));
            string line=null;
            if(cine!=Cine && Cine!=IntPtr.Zero)line=Close(nowMs);
            if(cine==IntPtr.Zero)return Limit(line,nowMs);
            if(cine!=Cine){Cine=cine;Type=type??"?";StartedMs=nowMs;Verdict=verdict;LongMark=0;Array.Clear(Frames,0,Frames.Length);Episodes++;}
            else if(verdict!=Verdict){line??=Type+" now "+CineGateName(verdict)+" after "+Seconds(nowMs)+" s (was "+CineGateName(Verdict)+")";Verdict=verdict;}
            Frames[verdict]++;Totals[verdict]++;
            if(LongMark<LongMarksMs.Length && nowMs-StartedMs>=LongMarksMs[LongMark]){LongMark++;line??=Type+" running for "+Seconds(nowMs)+" s; "+Counts();}
            return Limit(line,nowMs);
        }
        internal string Fault(Exception error,long nowMs)
        {
            Faults++;LastFault=error==null?"unknown":error.GetType().Name+": "+error.Message;
            if(!Latched && Faults>=FaultLimit){Latched=true;LinesWritten++;return "Cinematic gate: fault "+Faults+" ("+LastFault+"); fell back to the Alpha1 hold rule until the game restarts";}
            return Limit("fault "+Faults+" ("+LastFault+"); this frame held",nowMs);
        }
        string Close(long nowMs)
        {
            var line=Type+" ended after "+Seconds(nowMs)+" s; "+Counts();
            Cine=IntPtr.Zero;Type=null;Verdict=CineGateNone;return line;
        }
        string Counts()
        {
            long held=0;var reasons=new System.Text.StringBuilder();
            for(int v=CineGateHoldTransition;v<CineGateVerdicts;v++)if(Frames[v]>0)
            {held+=Frames[v];reasons.Append(reasons.Length==0?" (":", ").Append(CineGateReason(v)).Append(' ').Append(Frames[v]);}
            if(reasons.Length>0)reasons.Append(')');
            return "gate open on "+Frames[CineGateOpen]+" frames, held on "+held+" frames"+reasons;
        }
        string Seconds(long nowMs)=>((nowMs-StartedMs)/1000.0).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture);
        string Limit(string line,long nowMs)
        {
            if(line==null)return null;
            if(!windowOpen || nowMs<windowStart || nowMs-windowStart>=LogWindowMs){windowOpen=true;windowStart=nowMs;windowLines=0;}
            if(windowLines>=LogBurst){LinesSuppressed++;pendingSuppressed++;return null;}
            windowLines++;LinesWritten++;
            if(pendingSuppressed>0){line+=" (+"+pendingSuppressed+" gate lines suppressed)";pendingSuppressed=0;}
            return "Cinematic gate: "+line;
        }
    }
    private static readonly CineGateMonitor CineGate=new();
    private static FieldInfo CineGateReadyField,CineGateTrackedField;
    private static bool HoldConsumeForCinematic()
    {
        var game=dc.pr.Game.Class.ME;var cine=game?.curCine;   // the Alpha1 read, unchanged
        long now=Environment.TickCount64;
        if(cine==null)
        {
            if(CineGate.Cine!=IntPtr.Zero)
                try{LogCineGate(CineGate.Observe(IntPtr.Zero,null,CineGateNone,now),false);}
                catch(Exception error){try{LogCineGate(CineGate.Fault(error,now),true);}catch{}}
            return false;
        }
        try
        {
            bool alpha1=CineGate.UseAlpha1,transition=false,ready=false,heroLive=false;IntPtr heroLevel=IntPtr.Zero,trackedLevel=IntPtr.Zero;
            if(!alpha1)
            {
                var hero=game.hero;var level=hero?._level;
                var tracked=(CineGateTrackedField??=Mobs.GetField("currentLevel",Flags)).GetValue(null) as Level;
                ready=(bool)(CineGateReadyField??=Mobs.GetField("s_levelIdentityReady",Flags)).GetValue(null);
                transition=cine is dc.cine.LevelTransition;heroLive=hero!=null && !hero.destroyed && level!=null && !level.destroyed;
                heroLevel=level?.HashlinkPointer??IntPtr.Zero;trackedLevel=tracked?.HashlinkPointer??IntPtr.Zero;
            }
            var verdict=CineGateVerdict(true,alpha1,transition,ready,heroLive,heroLevel,trackedLevel);
            var pointer=cine.HashlinkPointer;
            LogCineGate(CineGate.Observe(pointer,pointer==CineGate.Cine?null:cine.GetType().FullName,verdict,now),false);
            return CineGateHolds(verdict);
        }
        catch(Exception error)
        {
            try{LogCineGate(CineGate.Fault(error,now),true);}catch{}
            return true;   // the Alpha1 hold for this frame
        }
    }
    private static void LogCineGate(string line,bool warning)
    {
        if(line==null)return;
        if(warning)Log.Warning("[CoopSyncFixes] {CineGateLine:l}",line);else Log.Information("[CoopSyncFixes] {CineGateLine:l}",line);
    }
    private static void ApplyJoinManifest(WorldJoin state)
    {
        if(!AtWorldTarget(state.Header) || !ValidWireHeader(ModEntry._net,state.Header))return;
        var manifest=state.Manifest;var states=new List<NetNode.MobStateSnapshot>();var dies=new List<NetNode.MobDie>();
        var bind=Mobs.GetMethod("TryBindUnboundMobByTypeAndSpawnLocked",Flags,null,new[] {typeof(int),typeof(string),typeof(double),typeof(double),
            typeof(HashSet<Mob>),typeof(Mob).MakeByRefType(),typeof(bool).MakeByRefType()},null);
        var reserved=new HashSet<Mob>();var boundCount=0;var deadCount=0;
        var placements=new List<(Mob Mob,double X,double Y)>();
        lock(Gate)
        {
            var forward=(IDictionary)Field("IdToMob");var reverse=(IDictionary)Field("MobToId");
            foreach(var item in manifest.Entries)
            {
                if(ConfirmedDead(manifest.Generation,item.Id))continue;
                if(item.Dead && HasCurrentBuildDeathIntent(manifest.Generation,item.Id) && !forward.Contains(item.Id))continue;
                Mob mob=FindVerifiedClientBirth(item,reserved);
                if(mob!=null)Invoke("TryRebindTrackedMobSyncIdLocked",mob,item.Id);
                else mob=forward.Contains(item.Id)?forward[item.Id] as Mob:null;
                if(mob==null)
                {
                    var args=new object[] {item.Id,item.Type,item.X,item.Y,reserved,null,false};
                    if((bool)bind.Invoke(null,args))mob=args[5] as Mob;
                }
                if(mob==null && !item.Dead)
                {
                    mob=Invoke("TryCreateClientMobReplica",item.Type,item.CurrentX,item.CurrentY) as Mob;
                    if(mob==null)throw new InvalidOperationException("Cannot create live snapshot replica "+item.Type);
                    var tracked=(IList)Field("trackedMobs");((IDictionary)Field("trackedMobIndices"))[mob]=tracked.Count;tracked.Add(mob);
                    forward[item.Id]=mob;reverse[mob]=item.Id;
                }
                if(mob!=null)
                {
                    if(!reverse.Contains(mob) || (int)reverse[mob]!=item.Id || !ReferenceEquals(forward[item.Id],mob))
                        throw new InvalidOperationException("Mob snapshot broke identity bijection");
                    reserved.Add(mob);boundCount++;
                    RememberVerifiedClientBirth(item,mob);
                }
                if(item.Dead)
                {
                    deadCount++;
                    if(mob==null)
                    {var ledger=Field("s_clientMobLifecycle");ledger.GetType().GetMethod("MarkDead").Invoke(ledger,new object[] {manifest.Generation,item.Id});}
                    else dies.Add(new NetNode.MobDie(1,item.Id,item.X,item.Y,manifest.Generation,item.Type));
                }
                else
                {
                    states.Add(new NetNode.MobStateSnapshot(item.Id,item.CurrentX,item.CurrentY,item.Dir,item.Life,Math.Max(1,item.MaxLife),"",item.Type,
                        item.State??"",manifest.Generation,manifest.Frame));
                    placements.Add((mob,item.CurrentX,item.CurrentY));
                }
            }
            // Finish every birth-coordinate match before moving any candidate. Off-screen
            // interpolation is intentionally gated by vanilla culling, so a fresh join must
            // also initialize physical coordinates from the authoritative snapshot.
            foreach(var placement in placements)
            {
                if(!Disabled("CULLED"))PlaceSleepingReplica(placement.Mob,placement.X,placement.Y);
            }
        }
        SnapshotApplyDepth++;
        try {Invoke("ApplyIncomingHostMobStates",states);Invoke("ApplyIncomingMobDies",dies);AdoptWireManifest(ModEntry._net,state);state.ManifestApplied=true;}
        finally {SnapshotApplyDepth--;}
        Log.Information("[CoopSyncFixes] Bound full mob snapshot level={Level} count={Count} bound={Bound} dead={Dead} revision={Revision}",
            manifest.Level,manifest.Entries.Count,boundCount,deadCount,manifest.Revision);
    }
}
