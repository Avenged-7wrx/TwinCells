using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using dc.en;
using dc.en.inter;
using dc.pr;
using HaxeProxy.Runtime;
using LevelMob = dc.level.Mob;
using Room = dc.level.Room;
using Marker = dc.level.Marker;

// Cold-installed companion for one proven source topology. The original network
// parser, generation fence, creator, HP/death application, and queues stay intact.
public static class ShrineReplicaPatch
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string FixesPin="FFF00CA3F9297B00EBD7688A9E4D223FD2A14F2D0EC8DFE34801F8490C7342E9";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string RegistrationPin="857C4714B2D9218BD2FF2DB2BD8E9DB6F41B075D54596BB6646F6B523A7791A7";
    public const string Owner="CoopShrineFix.NativeSourcePair.20261108";
    const BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const string RetireReason="exact_native_shrine_handoff";
    static readonly object InitializationGate=new();
    static readonly List<Pair> Pending=new(); // Only under original Mobs.Sync, max16.
    static Type M, F;
    static FieldInfo SyncField, ForwardField, ReverseField, TrackedField, IndicesField, LevelField, GenerationField, BuildField;
    static FieldInfo WireStatesField, TicketField, OpenField;
    static MethodInfo WireLookup, Remove, Rebind, ReadyLevel, DeadOnClient, Create, Resolve;
    static MethodInfo Tombstone, Reset;
    static EventInfo AttachEvent;
    static Delegate AttachHook;
    static volatile bool Published;
    static long Remembered, Paired, HandedOff, Expired, Rejected, ObserverErrors, MutationErrors;
    [ThreadStatic] static Mob Retiring;
    public static bool Ready=>Published;
    public static bool Disabled {get;private set;}
    public static string InitializationError {get;private set;}
    public static long RecordedReplicas=>Interlocked.Read(ref Remembered);
    public static long PairedNativeBirths=>Interlocked.Read(ref Paired);
    public static long CompletedHandoffs=>Interlocked.Read(ref HandedOff);
    public static long ExpiredPairs=>Interlocked.Read(ref Expired);
    public static long RejectedPairs=>Interlocked.Read(ref Rejected);
    public static long InspectionErrors=>Interlocked.Read(ref ObserverErrors);
    public static long HandoffErrors=>Interlocked.Read(ref MutationErrors);
    sealed class Pair
    {
        public NetNode Net; public Level Level; public IntPtr LevelPointer, MapPointer;
        public object Ticket; public int Generation, Build, Id; public long Created;
        public EliteShrine Shrine; public LevelMob Source; public Room Room;
        public IntPtr ShrinePointer, SourcePointer, RoomPointer, ReplicaPointer, NativePointer;
        public int SourceUid; public Mob Replica, Native;
    }
    static object Sync=>SyncField.GetValue(null);
    static Dictionary<int,Mob> Forward=>(Dictionary<int,Mob>)ForwardField.GetValue(null);
    static Dictionary<Mob,int> Reverse=>(Dictionary<Mob,int>)ReverseField.GetValue(null);
    static IList Tracked=>(IList)TrackedField.GetValue(null);
    static IDictionary Indices=>(IDictionary)IndicesField.GetValue(null);
    static object Field(string name)=>M.GetField(name,S).GetValue(null);
    static long Now()=>Environment.TickCount64;
    static IntPtr Pointer(HaxeProxyBase value)=>value?.HashlinkPointer??IntPtr.Zero;
    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static MethodInfo Method(string name,Type result,params Type[] parameters)
    {
        var method=M.GetMethod(name,S,null,parameters,null);
        if(method?.ReturnType!=result)throw new InvalidOperationException("Pinned method changed: "+name);
        return method;
    }
    static FieldInfo FieldInfo(string name,Type expected)
    {
        var field=M.GetField(name,S);
        if(field?.FieldType!=expected)throw new InvalidOperationException("Pinned field changed: "+name);
        return field;
    }
    public static void Install()
    {
        lock(InitializationGate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier shrine initialization failed: "+InitializationError);
            Harmony harmony=null;bool attached=false;var patched=new List<MethodInfo>();
            try
            {
                var original=typeof(NetNode).Assembly;
                if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin ||
                    Hash(Path.Combine(Path.GetDirectoryName(typeof(Mob).Assembly.Location),"hlboot.dat"))!=NativePin)
                    throw new InvalidOperationException("CoopShrineFix requires exact community0.5.1/native35.13.5");
                F=global::CoopPackRuntime.PackParts.FindType("CoopSyncFixes","CoopSyncFixes")??throw new InvalidOperationException("CoopShrineFix requires reviewed CoopSyncFixes FFF00CA3");
                var registration=global::CoopPackRuntime.PackParts.FindType("CoopMobRegistrationFix","MobRegistrationPatch")??throw new InvalidOperationException("CoopShrineFix requires enabled positive-ID companion857C");
                if(!(bool)registration.GetProperty("Ready",S).GetValue(null)||(bool)registration.GetProperty("Disabled",S).GetValue(null))
                    throw new InvalidOperationException("CoopShrineFix requires enabled positive-ID companion857C");
                ShrineProxyCompatibility.ValidateLoaded(typeof(Mob).Assembly);
                M=original.GetType("DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization",true);
                SyncField=FieldInfo("Sync",typeof(object));ForwardField=FieldInfo("IdToMob",typeof(Dictionary<int,Mob>));
                ReverseField=FieldInfo("MobToId",typeof(Dictionary<Mob,int>));TrackedField=M.GetField("trackedMobs",S);IndicesField=M.GetField("trackedMobIndices",S);
                LevelField=FieldInfo("currentLevel",typeof(Level));GenerationField=FieldInfo("s_levelIdentityToken",typeof(int));BuildField=FieldInfo("s_levelIdentityGeneration",typeof(int));
                Create=Method("TryCreateClientMobReplica",typeof(Mob),typeof(string),typeof(double),typeof(double));
                Resolve=Method("ResolveTrackedMobForIncomingStateLocked",typeof(Mob),typeof(NetNode.MobStateSnapshot),typeof(HashSet<Mob>));
                Remove=Method("RemoveTrackedMobLocked",typeof(void),typeof(Mob),typeof(string));
                Rebind=Method("TryRebindTrackedMobSyncIdLocked",typeof(void),typeof(Mob),typeof(int));
                ReadyLevel=Method("IsLevelIdentityReadyLocked",typeof(bool),typeof(Level));
                Tombstone=Method("TryCaptureClientTombstoneForMappedMobLocked",typeof(void),typeof(Mob),typeof(string));
                Reset=Method("ResetMobTrackingStateLocked",typeof(void));
                DeadOnClient=F.GetMethod("DeadOnClient",S);
                WireStatesField=F.GetField("WireStates",S);
                var wireType=F.GetNestedType("WireState",BindingFlags.NonPublic);
                TicketField=wireType.GetField("ClientTicket",I);OpenField=wireType.GetField("ClientOpen",I);
                WireLookup=WireStatesField.FieldType.GetMethod("TryGetValue");
                if(DeadOnClient?.ReturnType!=typeof(bool)||TicketField==null||OpenField?.FieldType!=typeof(bool)||WireLookup==null)
                    throw new InvalidOperationException("FFF wire/death contract changed");
                Disabled=Environment.GetEnvironmentVariable("COOP_SHRINE_REPLICA_DISABLE")=="1";
                harmony=new Harmony(Owner);
                var consume=Method("ConsumeIncomingMobRegistry",typeof(void),typeof(NetNode));
                harmony.Patch(consume,transpiler:new HarmonyMethod(typeof(ShrineReplicaPatch).GetMethod(nameof(CaptureCreator),S)));patched.Add(consume);
                harmony.Patch(Resolve,prefix:new HarmonyMethod(typeof(ShrineReplicaPatch).GetMethod(nameof(BeforeResolve),S)));patched.Add(Resolve);
                harmony.Patch(Tombstone,prefix:new HarmonyMethod(typeof(ShrineReplicaPatch).GetMethod(nameof(AllowTombstone),S)));patched.Add(Tombstone);
                harmony.Patch(Reset,prefix:new HarmonyMethod(typeof(ShrineReplicaPatch).GetMethod(nameof(ClearPairs),S)));patched.Add(Reset);
                attached=true;SubscribeNativeHook();
                if(patched.Any(p=>!Harmony.GetPatchInfo(p).Owners.Contains(Owner)))throw new InvalidOperationException("Shrine hook installation incomplete");
                Published=true;
            }
            catch(Exception e)
            {
                Published=false;InitializationError=e.Message;
                if(attached)try{UnsubscribeNativeHook();}catch(Exception rollback){InitializationError+="; native rollback: "+rollback.Message;}
                if(harmony!=null)foreach(var method in patched)try{harmony.Unpatch(method,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    static IEnumerable<CodeInstruction> CaptureCreator(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();var calls=Enumerable.Range(0,code.Count).Where(i=>code[i].Calls(Create)).ToArray();
        if(calls.Length!=1)throw new InvalidOperationException("Expected one MOBREG replica creator");
        int p=calls[0];
        if(p<6 || !code[p-5].LoadsField(typeof(NetNode.MobRegistryEntry).GetField("Type")) ||
            !code[p-3].LoadsField(typeof(NetNode.MobRegistryEntry).GetField("X")) ||
            !code[p-1].LoadsField(typeof(NetNode.MobRegistryEntry).GetField("Y")) ||
            code[p+1].opcode!=OpCodes.Stloc_S || !(code[p+1].operand is LocalBuilder replica) || replica.LocalIndex!=10)
            throw new InvalidOperationException("Pinned MOBREG creator stack changed");
        // Original argument producer is ldloc.s V6 before each readonly field read.
        if(!(code[p-2].operand is LocalBuilder entry)||entry.LocalIndex!=6||entry.LocalType!=typeof(NetNode.MobRegistryEntry))
            throw new InvalidOperationException("Pinned MOBREG entry local changed");
        code.InsertRange(p+1,new[]{new CodeInstruction(OpCodes.Dup),new CodeInstruction(OpCodes.Ldloc,entry),
            new CodeInstruction(OpCodes.Call,typeof(ShrineReplicaPatch).GetMethod(nameof(RememberReplica),S))});
        return code;
    }
    static object WireTicket(NetNode net)
    {
        var args=new object[]{net,null};
        if(!(bool)WireLookup.Invoke(WireStatesField.GetValue(null),args)||args[1]==null || !(bool)OpenField.GetValue(args[1]))return null;
        // Immutable ticket reference; resets/renegotiation replace it. Never nest
        // Net._sync under Mobs.Sync. A second identity read below rejects replacement.
        return TicketField.GetValue(args[1]);
    }
    static bool Context(Pair pair)
    {
        var net=ModEntry._net;var level=LevelField.GetValue(null) as Level;
        return net!=null&&!net.IsHost&&ReferenceEquals(net,pair.Net)&&ReferenceEquals(WireTicket(net),pair.Ticket)&&pair.Ticket!=null&&
            Pointer(level)==pair.LevelPointer&&Pointer(level?.map)==pair.MapPointer&&
            ShrinePairGuard.Current(pair.Created,Now(),pair.Generation,pair.Build,(int)GenerationField.GetValue(null),(int)BuildField.GetValue(null))&&
            (bool)ReadyLevel.Invoke(null,new object[]{level});
    }
    static void Prune()
    {
        for(int i=Pending.Count-1;i>=0;i--)if(!Context(Pending[i])){Pending.RemoveAt(i);Interlocked.Increment(ref Expired);}
    }
    static void ClearPairs(){lock(Sync)Pending.Clear();}
    static bool HasLadder(dc.hl.types.ArrayObj loots)
    {
        if(loots==null||loots.length<0||loots.length>128)return false;
        int count=0;
        for(int i=0;i<loots.length;i++)if(loots.getDyn(i) is dc.LootType.InvItem loot&&loot.Param0?.kind is dc.tool.InventItemKind.Meta meta&&meta.Param0?.ToString()=="LadderKey")count++;
        return count==1;
    }
    static bool ValidSource(Pair pair,bool requireUnused)
    {
        var shrine=pair.Shrine;var source=shrine?.lMob;var room=pair.Room;
        if(shrine==null||shrine.destroyed||!shrine.initDone||requireUnused&&shrine.used||
            Pointer(shrine)!=pair.ShrinePointer||Pointer(shrine._level)!=pair.LevelPointer||
            Pointer(source)!=pair.SourcePointer||source.__uid!=pair.SourceUid||
            source.kind?.ToString()!="Archer"||!source.elite||!source.inShrine||!source.disableEliteSkill||!HasLadder(source.loots)||
            Pointer(room)!=pair.RoomPointer||Pointer(room.map)!=pair.MapPointer||room.rType?.ToString()!="MetaLadderKey"||room.rTemplate?.ToString()!="CLadderGateKey"||
            shrine.cx!=source.cx||shrine.cy!=source.cy||Pointer(pair.Level.map.getRoomAt(source.cx,source.cy))!=pair.RoomPointer)return false;
        if(room.markers==null||room.markers.length!=4||room.mobs==null||room.mobs.length!=1)return false;
        int sourceMatches=0,markerMatches=0;var topology=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<room.mobs.length;i++)if(room.mobs.getDyn(i) is LevelMob mob&&Pointer(mob)==pair.SourcePointer)sourceMatches++;
        for(int i=0;i<room.markers.length;i++)
        {
            if(room.markers.getDyn(i) is not Marker marker)return false;
            string key=string.Join("|",marker.kind?.ToString(),marker.customId?.ToString(),marker.mobId?.ToString(),marker.itemId?.ToString());
            if(!topology.Add(key))return false;
            if(key=="EliteShrine|noSkill|Archer|LadderKey"&&ShrinePairGuard.MarkerSource(source.cx,source.cy,room.x,room.y,marker.cx,marker.cy,marker.width,marker.height))markerMatches++;
        }
        return sourceMatches==1&&markerMatches==1&&topology.SetEquals(new[]{"ZDoor|||","CustomDeco|flagMetaObject||","EliteShrine|noSkill|Archer|LadderKey","VineLadderUp|||"});
    }
    static bool NativeActor(Mob mob,Pair pair,bool guardian,bool requireInitialized=true)
    {
        if(mob==null||mob.destroyed||requireInitialized&&!mob.initDone||mob.life<=0||mob.onDieDone||Pointer(mob._level)!=pair.LevelPointer||mob.type?.ToString()!="Archer")return false;
        return guardian?mob.elite&&HasLadder(mob.loots):!mob.elite&&!HasLadder(mob.loots);
    }
    static Pair FindSource(Mob replica,NetNode.MobRegistryEntry entry)
    {
        var net=ModEntry._net;var level=LevelField.GetValue(null) as Level;
        if(net==null||net.IsHost||level?.map==null||entry.NetId<=0||entry.Generation!=(int)GenerationField.GetValue(null)||entry.Type!="Archer|Archer")return null;
        var ticket=WireTicket(net);if(ticket==null||!(bool)ReadyLevel.Invoke(null,new object[]{level}))return null;
        if(level.entities==null||level.entities.length<0||level.entities.length>32768)return null;
        Pair selected=null;
        for(int i=0;i<level.entities.length;i++)
        {
            if(level.entities.getDyn(i) is not EliteShrine shrine||shrine.destroyed||shrine.used||shrine.lMob==null)continue;
            var source=shrine.lMob;var room=level.map.getRoomAt(shrine.cx,shrine.cy);if(room==null)continue;
            var pair=new Pair {Net=net,Level=level,LevelPointer=Pointer(level),MapPointer=Pointer(level.map),Ticket=ticket,
                Generation=entry.Generation,Build=(int)BuildField.GetValue(null),Id=entry.NetId,Created=Now(),Shrine=shrine,Source=source,Room=room,
                ShrinePointer=Pointer(shrine),SourcePointer=Pointer(source),SourceUid=source.__uid,RoomPointer=Pointer(room),Replica=replica,ReplicaPointer=Pointer(replica)};
            if(!ValidSource(pair,true)||!NativeActor(replica,pair,false)||
                !ShrinePairGuard.BirthCell(source.cx,source.cy,room.x,room.y,source.cx-room.x,source.cy-room.y,entry.X,entry.Y))continue;
            // A local guardian already present is not a deferred birth candidate.
            bool alreadyBorn=false;
            for(int j=0;j<level.entities.length;j++)if(level.entities.getDyn(j) is Mob actor&&NativeActor(actor,pair,true)&&Pointer(level.map.getRoomAt(actor.cx,actor.cy))==pair.RoomPointer){alreadyBorn=true;break;}
            if(alreadyBorn)continue;
            if(selected!=null)return null;selected=pair;
        }
        return selected!=null&&ReferenceEquals(WireTicket(net),ticket)?selected:null;
    }
    static void RememberReplica(Mob replica,NetNode.MobRegistryEntry entry)
    {
        if(!Published||Disabled||replica==null)return;
        try
        {
            if(!Monitor.IsEntered(Sync))throw new InvalidOperationException("MOBREG creator outside original lock");
            Prune();if(Pending.Count>=ShrinePairGuard.MaximumPending)return;
            var pair=FindSource(replica,entry);if(pair==null)return;
            if(Pending.Any(p=>p.Id==pair.Id||p.SourcePointer==pair.SourcePointer||p.ReplicaPointer==pair.ReplicaPointer)){Interlocked.Increment(ref Rejected);return;}
            Pending.Add(pair);Interlocked.Increment(ref Remembered);
        }
        catch(Exception e){Report(e,false);}
    }
    static void BuildNativeHook()
    {
        // Emit against the loaded official hook delegate ABI. This avoids a
        // generated CoreLib compile-reference quirk, without DynamicInvoke or
        // catching/wrapping an exception from the original native call.
        AttachEvent=typeof(Level).Assembly.GetType("dc.pr.Hook_Level",true).GetEvent("attachMob");
        var signature=AttachEvent.EventHandlerType.GetMethod("Invoke");
        var types=signature.GetParameters().Select(p=>p.ParameterType).ToArray();
        if(signature.ReturnType!=typeof(Mob)||types.Length!=3||types[1]!=typeof(Level)||types[2]!=typeof(LevelMob))
            throw new InvalidOperationException("Native attach handler ABI changed");
        var original=types[0].GetMethod("Invoke");
        if(original.ReturnType!=typeof(Mob)||!original.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(Level),typeof(LevelMob)}))
            throw new InvalidOperationException("Native attach original ABI changed");
        var method=new DynamicMethod("CoopShrineObserveAttach",typeof(Mob),types,typeof(ShrineReplicaPatch),true);var il=method.GetILGenerator();var result=il.DeclareLocal(typeof(Mob));
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Callvirt,original);il.Emit(OpCodes.Stloc,result);
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Ldloc,result);il.Emit(OpCodes.Call,typeof(ShrineReplicaPatch).GetMethod(nameof(ObserveAttached),S));
        il.Emit(OpCodes.Ldloc,result);il.Emit(OpCodes.Ret);AttachHook=method.CreateDelegate(AttachEvent.EventHandlerType);
    }
    static void SubscribeNativeHook(){BuildNativeHook();AttachEvent.AddEventHandler(null,AttachHook);}
    static void UnsubscribeNativeHook(){if(AttachEvent!=null&&AttachHook!=null)AttachEvent.RemoveEventHandler(null,AttachHook);}
    static void ObserveAttached(Level level,LevelMob source,Mob actor)
    {
        if(!Published||Disabled||source==null||actor==null)return;
        try
        {
            lock(Sync)
            {
                Prune();var matches=Pending.Where(p=>Pointer(level)==p.LevelPointer&&Pointer(source)==p.SourcePointer&&source.__uid==p.SourceUid).ToArray();
                if(matches.Length!=1)return;var pair=matches[0];
                if(pair.Native!=null||!pair.Shrine.used||!ValidSource(pair,false)||!NativeActor(actor,pair,true,false)||
                    Pointer(actor)==pair.ReplicaPointer||Reverse.ContainsKey(actor)||Forward.Values.Any(m=>Pointer(m)==Pointer(actor))){Interlocked.Increment(ref Rejected);return;}
                pair.Native=actor;pair.NativePointer=Pointer(actor);Interlocked.Increment(ref Paired);
            }
        }
        catch(Exception e){Report(e,false);}
    }
    static bool DeathStarted(Pair pair)
    {
        if((bool)DeadOnClient.Invoke(null,new object[]{pair.Generation,pair.Id}))return true;
        if(((IDictionary)Field("s_clientMobTombstonesBySyncId")).Contains(pair.Id))return true;
        foreach(var name in new[]{"s_pendingCulledMobDeaths","clientPendingSuppressedMobDies","clientPendingSuppressedBossDies"})
            if(((HashSet<Mob>)Field(name)).Contains(pair.Replica)||((HashSet<Mob>)Field(name)).Contains(pair.Native))return true;
        var life=(IDictionary)Field("clientLastReportedMobLife");
        return life.Contains(pair.Replica)&&Convert.ToInt32(life[pair.Replica])<=0 || life.Contains(pair.Native)&&Convert.ToInt32(life[pair.Native])<=0;
    }
    static bool ExactOwners(Pair pair,HashSet<Mob> reserved)
    {
        return pair.Native!=null&&Pointer(pair.Replica)==pair.ReplicaPointer&&Pointer(pair.Native)==pair.NativePointer&&
            Forward.TryGetValue(pair.Id,out var existing)&&ReferenceEquals(existing,pair.Replica)&&
            Reverse.TryGetValue(pair.Replica,out var id)&&id==pair.Id&&Indices.Contains(pair.Replica)&&Tracked.Contains(pair.Replica)&&
            !Reverse.Keys.Any(m=>Pointer(m)==pair.NativePointer)&&!Forward.Values.Any(m=>Pointer(m)==pair.NativePointer)&&
            !Reverse.Any(p=>Pointer(p.Key)==pair.ReplicaPointer&&!ReferenceEquals(p.Key,pair.Replica))&&
            !Forward.Any(p=>p.Key!=pair.Id&&Pointer(p.Value)==pair.ReplicaPointer)&&
            (reserved==null||!reserved.Any(m=>Pointer(m)==pair.NativePointer||Pointer(m)==pair.ReplicaPointer));
    }
    static void BeforeResolve(NetNode.MobStateSnapshot state,HashSet<Mob> reservedMobs)
    {
        if(!Published||Disabled||Pending.Count==0)return;
        Pair pair=null;
        try
        {
            if(!Monitor.IsEntered(Sync))throw new InvalidOperationException("MOBSTATE resolution outside original lock");
            Prune();pair=Pending.SingleOrDefault(p=>p.Id==state.Index);
            if(pair==null||!ShrinePairGuard.Packet(state.Index,state.Generation,state.Type,pair.Id,pair.Generation)||
                !ExactOwners(pair,reservedMobs)||!Context(pair)||!pair.Shrine.used||!ValidSource(pair,false)||
                !NativeActor(pair.Replica,pair,false)||!NativeActor(pair.Native,pair,true)||DeathStarted(pair))return;
        }
        catch(Exception e){Report(e,false);return;}
        if(pair==null)return;
        // No callback, packet, or native health state is manufactured. The
        // caller's exact accepted snapshot proceeds after the mapping transfer.
        var prior=Retiring;
        try
        {
            Retiring=pair.Replica;
            Remove.Invoke(null,new object[]{pair.Replica,RetireReason});
            if(Forward.ContainsKey(pair.Id)||Reverse.ContainsKey(pair.Replica)||Tracked.Contains(pair.Replica)||Reverse.ContainsKey(pair.Native))
                throw new InvalidOperationException("Original replica cleanup did not retire exact ownership");
            Rebind.Invoke(null,new object[]{pair.Native,pair.Id});
            if(!Owns(pair.Native,pair.Id))throw new InvalidOperationException("Original native guardian rebind refused");
            // Native destroy schedules normal GC; audited #1139/#861 has no
            // onDie/dropLoot. Subsequent old-object unregister sees no old ID.
            DestroyReplica(pair.Replica);
            Pending.Remove(pair);Interlocked.Increment(ref HandedOff);
        }
        catch(Exception e)
        {
            // Before the transfer completes, restore only an empty original ID.
            // Never displace a foreign/native owner or clear a death ledger.
            // Original methods can throw AFTER writing their maps (e.g. final
            // integrity logging). Inspect the resulting ownership, never infer
            // it from whether reflection Invoke returned successfully.
            if(Owns(pair.Native,pair.Id)&&!Reverse.ContainsKey(pair.Replica)&&!Tracked.Contains(pair.Replica)&&!Indices.Contains(pair.Replica))
            {
                try{if(!pair.Replica.destroyed)DestroyReplica(pair.Replica);if(pair.Replica.destroyed)Interlocked.Increment(ref HandedOff);}
                catch(Exception retire){Report(retire,true);}
            }
            else if(!Forward.ContainsKey(pair.Id)&&!Reverse.ContainsKey(pair.Native)&&!Reverse.ContainsKey(pair.Replica))
                try{Rebind.Invoke(null,new object[]{pair.Replica,pair.Id});}catch(Exception restore){Report(restore,true);}
            Pending.Remove(pair);Report(e,true);
        }
        finally{Retiring=prior;}
    }
    static bool Owns(Mob actor,int id)=>Forward.TryGetValue(id,out var owner)&&ReferenceEquals(owner,actor)&&Reverse.TryGetValue(actor,out var back)&&back==id;
    static void DestroyReplica(Mob replica)=>replica.destroy();
    static bool AllowTombstone(Mob mob,string reason)=>!Published||Disabled||Retiring==null||!ReferenceEquals(mob,Retiring)||reason!=RetireReason;
    static void Report(Exception error,bool mutation)
    {
        long n=mutation?Interlocked.Increment(ref MutationErrors):Interlocked.Increment(ref ObserverErrors);
        if(n<=8)Serilog.Log.Warning("[CoopShrineFix] {Stage} rejected: {Error}",mutation?"handoff":"inspection",error.GetBaseException().Message);
    }
}
