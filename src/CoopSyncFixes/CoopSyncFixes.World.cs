using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dc.pr;
using dc.cine;
using HarmonyLib;
using ModCore.Events.Interfaces.Game;
using ModCore.Utilities;
using DeadCellsMultiplayerMod;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

public sealed partial class CoopSyncFixes : IOnFrameUpdate
{
    private const BindingFlags InstanceFlags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly Type WorldData=typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.GameDataSync",true);
    private static readonly ConditionalWeakTable<NetNode,HostEpoch> HostEpochs=new();
    private static readonly ConditionalWeakTable<NetNode,WorldJoin> WorldJoins=new();
    private static readonly ConditionalWeakTable<object,SemaphoreSlim> WorldSendGates=new();
    private sealed class HostEpoch {public long Epoch;public IntPtr Pointer;public int BuildGeneration;}
    private sealed class WorldHeader
    {
        public string Nonce,Level,GraphHash,SeedPayload,MobHash,RequestToken,HostRunNonce;public bool ActivationOnly;public int Floor;
        public Guid SessionId;public long RunId;public int Sequence;
        public long Epoch,GraphSequence,MobRevision,TransferId;public double Seed;public int Generation,Cx,Cy;public bool SubLevel;
        public int RouteVersion;public string RouteHash;
    }
    private sealed class WorldJoin
    {
        public WorldHeader Header;public string Graph;public MobManifest Manifest;public long NextRequest,Started;
        public bool Routed,GraphApplied,ManifestApplied,Accepted,SpawnApplied,Complete,Finalizing;
        public int AnchorX,AnchorY;
        public bool ActivationOnly;public ActivationRequest Request;public IntPtr LocalPointer;public int LocalBuild;
        public long ExpectedWireEpoch;public string ExpectedWireRun;
        public readonly Dictionary<int,byte[]> MobParts=new();public int MobPartCount,MobPartBytes;
        public RouteBundle Route;public int RouteCursor=-1;public IntPtr RouteExpected;public bool RouteRootApplied;
        public RoutePortalProgress RoutePortal;
        public int RouteLoadingSlot;public readonly HashSet<int> RouteAppliedSlots=new();
        public readonly Dictionary<int,byte[]> GraphParts=new();public int GraphPartCount,GraphPartBytes;
    }
    private static object WorldField(string name)=>WorldData.GetField(name,Flags).GetValue(null);
    private static object NetField(NetNode net,string name)=>typeof(NetNode).GetField(name,InstanceFlags).GetValue(net);
    private static string HashText(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void InitializeWorldJoin(Harmony harmony)
    {
        if(Disabled("WORLD"))return;
        harmony.Patch(typeof(NetNode).GetMethod("HandleLine",InstanceFlags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ReadWorldLine),Flags)));
        Patch(harmony,Lobby,"TryClaimClientAutoStartLocked",nameof(WaitForWorldSnapshot));
        Patch(harmony,Lobby,"SendRunReadyFromHero",nameof(WaitForFinalWorld));
        Patch(harmony,WorldData,"TryApplyRemoteLevelGraph",nameof(PinRouteImport),nameof(ObserveJoinGraph));
        Patch(harmony,Mobs,"RebuildMobArray",null,nameof(AdvanceHostEpoch));
        harmony.Patch(typeof(ModEntry).GetMethod("ArmJoinSpawnForCurrentLevel",InstanceFlags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(WaitForApprovedSpawn),Flags)));
        RegisterJoinLevelSeedHook();
        InitializeJoinLease(harmony);
    }
    private static bool PendingWorld(NetNode net,out WorldJoin state)
    {
        state=null;
        if(Disabled("WORLD") || net==null || net.IsHost || !ClientConnections.TryGetValue(net,out var context))return false;
        if(WorldJoins.TryGetValue(net,out state) && state.ActivationOnly)return !state.Complete;
        if(!context.LateJoin)return false;
        var descriptor=Coordinator.GetMethod("GetCurrentRemoteDescriptor",Flags).Invoke(null,null);
        if(descriptor!=null && !context.Matches(descriptor))return false;
        state=WorldJoins.GetValue(net,_=>new WorldJoin {Started=Environment.TickCount64});
        return !state.Complete;
    }
    private static bool WaitForWorldSnapshot(ref bool __result)
    {
        if(!PendingWorld(ModEntry._net,out var state) || state.ActivationOnly || state.Header!=null && state.Graph!=null && state.Manifest!=null)return true;
        __result=false;return false;
    }
    private static bool WaitForFinalWorld()=>!PendingWorld(ModEntry._net,out var state) || state.ActivationOnly;
    private static bool WaitForApprovedSpawn()=>!PendingWorld(ModEntry._net,out var state) || state.ActivationOnly || state.Accepted;
    private static void ObserveJoinGraph(string levelId,bool __result)
    {
        if(__result && PendingWorld(ModEntry._net,out var routing) && routing.Route!=null)
        {
            // A committed child can be generated synchronously inside its root load.
            // Its native scope identifies that exact bundle slot without advancing
            // the route cursor/loading slot while the parent still owns generation.
            var slot=TryGetGeneratedRouteGraph(levelId,out var generated)?generated:routing.Route.Graphs[routing.RouteLoadingSlot];
            if(slot.Map.Level==levelId)lock(WorldField("_levelGraphLock"))
            {
                var applied=(IDictionary)WorldField("_lastAppliedLevelGraphSequenceByLevelId");
                if(applied.Contains(levelId) && (long)applied[levelId]==slot.Sequence)
                {routing.RouteAppliedSlots.Add(slot.Slot);if(slot.Slot==0)routing.RouteRootApplied=true;RecordGeneratedRouteImport(routing,slot);}
            }
            return;
        }
        if(__result && PendingWorld(ModEntry._net,out var state) && state.Header?.Level==levelId)
            lock(WorldField("_levelGraphLock"))
            {
                var applied=(IDictionary)WorldField("_lastAppliedLevelGraphSequenceByLevelId");
                state.GraphApplied=applied.Contains(levelId) && (long)applied[levelId]==state.Header.GraphSequence;
            }
    }
    private static void AdvanceHostEpoch(Level level,bool __result)
    {
        var net=ModEntry._net;if(!__result || net==null)return;
        if(!net.IsHost){ClientBuilds.GetValue(net,_=>new ClientBuildStamp()).Value++;return;}
        if(WireEnabled){CaptureBootstrapManifest(net,level);return;}
        // Native tracking generation is reset by several lifecycle paths; an
        // accepted rebuild is still a new host allocation epoch on the same map.
        var epoch=HostEpochs.GetValue(net,_=>new HostEpoch());epoch.Epoch++;
        epoch.Pointer=level.HashlinkPointer;epoch.BuildGeneration=(int)Field("s_levelIdentityGeneration");
        CaptureBootstrapManifest(net,level);
    }
    private static HostEpoch EnsureHostEpoch(NetNode net,Level level)
    {
        var epoch=HostEpochs.GetValue(net,_=>new HostEpoch());var build=(int)Field("s_levelIdentityGeneration");
        if(epoch.Pointer!=level.HashlinkPointer || epoch.BuildGeneration!=build)
        {epoch.Epoch++;epoch.Pointer=level.HashlinkPointer;epoch.BuildGeneration=build;}
        return epoch;
    }
    private static WorldHeader CaptureWorld(NetNode net,JoinContext context,out string graph,out string mobs)
    {
        graph=null;mobs=null;
        if(!context.Matches(Coordinator.GetMethod("GetCurrentHostDescriptor",Flags).Invoke(null,null)))return null;
        var hero=dc.pr.Game.Class.ME?.hero;var level=hero?._level;
        if(hero==null || level?.map==null || hero.destroyed || hero.life<=0 || !SecretSnapshotSafe(dc.pr.Game.Class.ME,level.map))return null;
        if(dc.pr.Game.Class.ME.curCine!=null && !HoldJoiningTransition(dc.pr.Game.Class.ME.curCine) || (bool)Invoke("IsSyncQuiescedForTransition"))return null;
        var id=level.map.id.ToString();var seed=level.map.seed;
        int generation;
        lock(Gate)
        {
            generation=(int)Field("s_levelIdentityToken");
            if(generation<=0 || (int)Invoke("ComputeLevelIdentityToken",level)!=generation)return null;
        }
        string seedPayload;RouteBundle capturedRoute=null;
        if(level.isSubLevel || level.isSecret)
        {
            graph=CaptureRouteBundle(net,level);if(graph==null)return null;
            capturedRoute=JsonConvert.DeserializeObject<RouteBundle>(graph);seedPayload=capturedRoute.Graphs[capturedRoute.Graphs.Count-1].SeedPayload;
        }
        else lock(NetField(net,"_hostCacheSync"))
        {graph=((IDictionary)NetField(net,"_cachedHostLevelGraphsByLevelId"))[id] as string;seedPayload=((IDictionary)NetField(net,"_cachedHostLevelSeedsByLevelId"))[id] as string;}
        if(string.IsNullOrEmpty(graph) || string.IsNullOrEmpty(seedPayload))return null;
        var graphInfo=JObject.Parse(capturedRoute==null?graph:capturedRoute.Graphs[capturedRoute.Graphs.Count-1].Json);
        if((string)graphInfo["LevelId"]!=id)return null;
        var epoch=EnsureHostEpoch(net,level);
        mobs=BuildManifestSnapshot(net,epoch.Epoch,out var revision);
        if(mobs==null)return null;
        var header=new WorldHeader {Nonce=context.Nonce,SessionId=context.SessionId,RunId=context.RunId,Sequence=context.Sequence,
            Level=id,Seed=seed,Generation=generation,Epoch=epoch.Epoch,GraphSequence=(long)graphInfo["Seq"],
            GraphHash=HashText(graph),SeedPayload=seedPayload,MobHash=HashText(mobs),MobRevision=revision,Cx=hero.cx,Cy=hero.cy,SubLevel=level.isSubLevel};
        if(level.isSubLevel || level.isSecret)
        {
            header.RouteVersion=1;header.RouteHash=HashText(JsonConvert.SerializeObject(capturedRoute.Steps));
        }
        return FillWireWorldHeader(net,header)?header:null;
    }
    private static Task SendWorld(NetNode net,object connection,params string[] lines)=>SendWorldCore(net,connection,null,lines);
    private static async Task SendWorldCore(NetNode net,object connection,WorldHeader expected,string[] lines)
    {
        var gate=WorldSendGates.GetValue(connection,_=>new SemaphoreSlim(1,1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if(expected!=null && !IsTransferStillCurrent(net,connection,expected))return;
            var steam=(bool)NetField(net,"_useSteamTransport");
            var method=typeof(NetNode).GetMethod(steam?"SendLineToSteamClientSafe":"SendLineToClientSafe",InstanceFlags);
            foreach(var line in lines)
                await ((Task)method.Invoke(net,steam?new object[] {connection,line+"\n",null}:new object[] {connection,line+"\n"})).ConfigureAwait(false);
        }
        catch(Exception error){Log.Warning(error,"[CoopSyncFixes] World snapshot send failed");}
        finally {gate.Release();}
    }
    private static string[] BuildWorldTransfer(JoinContext context,WorldHeader header,string graph,string mobs)
    {
        var bytes=Encoding.UTF8.GetBytes(mobs);
        if(bytes.Length>16*1024*1024)throw new InvalidOperationException("Mob snapshot exceeds transfer capacity");
        const int chunkSize=96000;var count=(bytes.Length+chunkSize-1)/chunkSize;
        var nonce=TransferNonce(header,context.Nonce);
        var lines=new List<string> {$"CSFJLEVEL|{nonce}|{header.Epoch}|"+JsonConvert.SerializeObject(header)};
        if(header.RouteVersion==0)lines.Add($"CSFJGRAPH|{nonce}|{header.Epoch}|"+graph);
        else
        {
            var graphs=Encoding.UTF8.GetBytes(graph);if(graphs.Length>16*1024*1024)throw new InvalidOperationException("Route bundle capacity exceeded");
            var blocks=(graphs.Length+chunkSize-1)/chunkSize;
            for(int i=0;i<blocks;i++)lines.Add($"CSFJGRAPHPART|{nonce}|{header.Epoch}|"+new JObject { ["hash"]=header.GraphHash,["transfer"]=header.TransferId,["i"]=i,["n"]=blocks,
                ["data"]=Convert.ToBase64String(graphs,i*chunkSize,Math.Min(chunkSize,graphs.Length-i*chunkSize)) }.ToString(Formatting.None));
        }
        for(int i=0;i<count;i++)
            lines.Add($"CSFJMOBPART|{nonce}|{header.Epoch}|"+
                new JObject { ["hash"]=header.MobHash,["revision"]=header.MobRevision,["i"]=i,["n"]=count,
                    ["transfer"]=header.TransferId,["data"]=Convert.ToBase64String(bytes,i*chunkSize,Math.Min(chunkSize,bytes.Length-i*chunkSize)) }.ToString(Formatting.None));
        return lines.ToArray();
    }
    private static void SendRequest(NetNode net,string line)
        =>typeof(NetNode).GetMethod("SendRaw",InstanceFlags).Invoke(net,new object[] {line});
    private static bool ReadWorldLine(NetNode __instance,string line,int? senderId,ref bool __result,ref string forwardLine)
    {
        if(line==null || !line.StartsWith("CSFJ",StringComparison.Ordinal))return true;
        __result=true;forwardLine=null;
        try
        {
            var parts=line.Split(new[] {'|'},4);
            if(parts.Length<2)throw new InvalidOperationException("Malformed world control");
            if(__instance.IsHost)
            {
                if(!senderId.HasValue)return false;
                object connection;
                lock(NetField(__instance,"_clientsLock"))
                    connection=((IDictionary)NetField(__instance,(bool)NetField(__instance,"_useSteamTransport")?"_steamClients":"_clients"))[senderId.Value];
                if(connection==null || !HostConnections.TryGetValue(connection,out var context))return false;
                ActivationRequest activation=null;var requestNonce=parts[1].Split('.');
                if(requestNonce[0]!=context.Nonce || requestNonce.Length>2)return false;
                if(parts[0]=="CSFJACT")
                {
                    if(parts.Length!=4 || parts[3].Length>4096)return false;
                    activation=JsonConvert.DeserializeObject<ActivationRequest>(parts[3]);
                    if(activation==null || activation.Number<=0 || !Guid.TryParseExact(activation.Token,"N",out _) || string.IsNullOrEmpty(activation.Level) ||
                        activation.Level.Length>128 || activation.Generation<=0 || !double.IsFinite(activation.Seed) ||
                        !ActivationContext(context,activation).Matches(Coordinator.GetMethod("GetCurrentHostDescriptor",Flags).Invoke(null,null)))return false;
                    if(ActivationRequests.TryGetValue(connection,out var previousActivation) &&
                        (activation.Number<previousActivation.Number || activation.Number==previousActivation.Number && activation.Token!=previousActivation.Token))return false;
                    ActivationRequests.Remove(connection);ActivationRequests.Add(connection,activation);
                }
                else if(requestNonce.Length==2)
                {
                    if(!ActivationRequests.TryGetValue(connection,out activation) || activation.Token!=requestNonce[1])return false;
                }
                else if(!context.LateJoin)return false;
                var effective=activation==null?context:ActivationContext(context,activation);
                if(parts[0]=="CSFJFINISHACK")
                {AcknowledgeIssuedFinish(__instance,connection,parts);return false;}
                if(parts[0]=="CSFJGET" && WorldTransfers.TryGetValue(connection,out var busy) && Volatile.Read(ref busy.Sending)!=0)
                {Interlocked.Increment(ref busy.Coalesced);return false;}
                var header=CaptureWorld(__instance,effective,out var graph,out var mobs);if(header==null)return false;
                if(activation!=null)
                {if(!ActivationTargetMatches(activation,header))return false;header.ActivationOnly=true;header.RequestToken=activation.Token;}
                var nonce=TransferNonce(header,context.Nonce);
                var slot=FreezeWorldTransfer(connection,effective,header,graph,mobs);
                var readiness=parts.Length==4 ? parts[3].Split(',') : Array.Empty<string>();
                if((parts[0]=="CSFJREADY" || parts[0]=="CSFJDONE" || parts[0]=="CSFJFINISHACK") && parts.Length==4 && long.Parse(parts[2],CultureInfo.InvariantCulture)==header.Epoch &&
                    readiness.Length==3 && MatchesWorldTransfer(slot.Header,int.Parse(readiness[0],CultureInfo.InvariantCulture),
                        long.Parse(readiness[1],CultureInfo.InvariantCulture),long.Parse(readiness[2],CultureInfo.InvariantCulture)))
                {
                    var approval=new JObject { ["generation"]=header.Generation,["revision"]=slot.Header.MobRevision,["transfer"]=slot.Header.TransferId,["hash"]=slot.Header.MobHash,
                        ["x"]=header.Cx,["y"]=header.Cy,["level"]=header.Level }.ToString(Formatting.None);
                    if(parts[0]=="CSFJFINISHACK")slot.FinishAcknowledged=true;
                    else
                    {
                        if(parts[0]=="CSFJDONE")GrantPeerWire(__instance,senderId.Value,connection,slot.Header,true);
                        BeginHostJoinLease(__instance,connection,slot);
                        _=QueueWorldControl(__instance,connection,slot,$"{(parts[0]=="CSFJDONE"?"CSFJFINISH":"CSFJACCEPT")}|{nonce}|{header.Epoch}|"+approval);
                    }
                }
                else if(parts[0]=="CSFJGET" || parts[0]=="CSFJACT" || parts[0]=="CSFJREADY" || parts[0]=="CSFJDONE")
                    _=QueueFrozenWorld(__instance,connection,slot);
                return false;
            }
            if(!PendingWorld(__instance,out var state) || !ClientConnections.TryGetValue(__instance,out var client) ||
                (state.ActivationOnly?client.Nonce+"."+state.Request.Token:client.Nonce)!=parts[1] || !LocalActivationCurrent(state))return false;
            if(parts.Length!=4)throw new InvalidOperationException("Incomplete world control");
            var epochValue=long.Parse(parts[2],CultureInfo.InvariantCulture);
            if(parts[0]=="CSFJLEVEL")
            {
                var header=JsonConvert.DeserializeObject<WorldHeader>(parts[3]);
                var identity=state.ActivationOnly?ActivationContext(client,state.Request):client;
                if(header==null || header.Nonce!=client.Nonce || header.ActivationOnly!=state.ActivationOnly ||
                    state.ActivationOnly && (header.RequestToken!=state.Request.Token || !ActivationTargetMatches(state.Request,header)) ||
                    header.SessionId!=identity.SessionId || header.RunId!=identity.RunId ||
                    header.Sequence!=identity.Sequence || header.Epoch!=epochValue || string.IsNullOrWhiteSpace(header.Level) || header.Level.Length>128 ||
                    header.Generation<=0 || header.Epoch<=0 || header.TransferId<=0 || !double.IsFinite(header.Seed))throw new InvalidOperationException("Invalid world identity");
                if(!ValidWireHeader(__instance,header))return false;
                if(header.RouteVersion!=0 && header.RouteVersion!=1 || header.SubLevel && header.RouteVersion!=1 || header.RouteVersion==1 && (header.RouteHash?.Length!=64))throw new InvalidOperationException("Missing supported sublevel route");
                if(state.Header!=null && state.Header.Epoch>header.Epoch)return false;
                if(!Disabled("TRANSFERS") && state.Header?.Epoch==header.Epoch && (state.Header.MobRevision>header.MobRevision || state.Header.GraphSequence>header.GraphSequence || state.Header.TransferId>header.TransferId))return false;
                if(!Disabled("TRANSFERS") && state.Header?.Epoch==header.Epoch && state.Header.TransferId==header.TransferId &&
                    (!SameWorldRevision(state.Header,header) || state.Header.MobHash!=header.MobHash))throw new InvalidOperationException("Snapshot transfer identity changed");
                if(state.Header==null || state.Header.Epoch!=header.Epoch || state.Header.GraphHash!=header.GraphHash)
                {
                    state.Header=header;state.Graph=null;state.Route=null;state.RouteCursor=-1;state.RouteExpected=IntPtr.Zero;state.RoutePortal=null;state.RouteRootApplied=false;state.RouteLoadingSlot=0;state.RouteAppliedSlots.Clear();state.GraphParts.Clear();state.GraphPartCount=state.GraphPartBytes=0;
                    state.Manifest=null;state.MobParts.Clear();state.MobPartCount=0;state.MobPartBytes=0;state.ManifestApplied=false;state.Routed=false;state.GraphApplied=false;state.Accepted=false;state.SpawnApplied=false;
                    Log.Information("[CoopSyncFixes] Join target level={Level} seed={Seed} epoch={Epoch}",header.Level,header.Seed,header.Epoch);
                }
                else if(Disabled("TRANSFERS")?state.Header.MobRevision!=header.MobRevision:state.Header.TransferId!=header.TransferId)
                {state.Header=header;state.GraphParts.Clear();state.GraphPartCount=state.GraphPartBytes=0;state.Manifest=null;state.MobParts.Clear();state.MobPartCount=0;state.MobPartBytes=0;state.ManifestApplied=false;state.Accepted=false;state.SpawnApplied=false;}
            }
            else if(parts[0]=="CSFJGRAPHPART" && state.Header?.Epoch==epochValue && state.Header.RouteVersion==1)
            {
                var part=JObject.Parse(parts[3]);
                if((long?)part["transfer"]!=state.Header.TransferId || (string)part["hash"]!=state.Header.GraphHash)return false;
                if(state.Graph!=null && state.Route!=null)return false;
                var index=(int)part["i"];var count=(int)part["n"];var data=(string)part["data"];
                if(count<1 || count>175 || index<0 || index>=count || data==null || data.Length>128000 || state.GraphPartCount!=0 && state.GraphPartCount!=count)throw new InvalidOperationException("Invalid route bundle chunk");
                var block=Convert.FromBase64String(data);
                if(state.GraphParts.TryGetValue(index,out var prior)){if(!CryptographicOperations.FixedTimeEquals(prior,block))throw new InvalidOperationException("Route bundle chunk changed");}
                else {state.GraphParts[index]=block;state.GraphPartBytes+=block.Length;}
                state.GraphPartCount=count;if(state.GraphPartBytes>16*1024*1024)throw new InvalidOperationException("Route bundle capacity exceeded");
                if(state.GraphParts.Count==count)
                {
                    var joined=new byte[state.GraphPartBytes];var offset=0;for(int i=0;i<count;i++){var item=state.GraphParts[i];Buffer.BlockCopy(item,0,joined,offset,item.Length);offset+=item.Length;}
                    if(Convert.ToHexString(SHA256.HashData(joined))!=state.Header.GraphHash)throw new InvalidOperationException("Route bundle checksum mismatch");
                    var text=Encoding.UTF8.GetString(joined);state.Route=ValidateRouteBundle(text,state.Header);state.Graph=text;
                    state.GraphParts.Clear();state.GraphPartBytes=0;
                    if(state.ActivationOnly){state.GraphApplied=true;state.RouteCursor=state.Route.Steps.Count;}else PinRouteSlot(state,0);
                }
            }
            else if(parts[0]=="CSFJGRAPH" && state.Header?.Epoch==epochValue)
            {
                if(parts[3].Length>1000000 || HashText(parts[3])!=state.Header.GraphHash)throw new InvalidOperationException("World graph checksum mismatch");
                var info=JObject.Parse(parts[3]);
                if((string)info["LevelId"]!=state.Header.Level || (long)info["Seq"]!=state.Header.GraphSequence)throw new InvalidOperationException("World graph identity mismatch");
                state.Graph=parts[3];if(state.ActivationOnly)state.GraphApplied=true;else PinWorldCaches(state);
            }
            else if(parts[0]=="CSFJMOBPART" && state.Header?.Epoch==epochValue && !state.ManifestApplied && state.Manifest==null)
            {
                var part=JObject.Parse(parts[3]);
                if((string)part["hash"]!=state.Header.MobHash || (long)part["revision"]!=state.Header.MobRevision || (long?)part["transfer"]!=state.Header.TransferId)return false;
                var index=(int)part["i"];var count=(int)part["n"];var text=(string)part["data"];
                if(count<1 || count>175 || index<0 || index>=count || text==null || text.Length>128000 ||
                    state.MobPartCount!=0 && state.MobPartCount!=count)throw new InvalidOperationException("Invalid mob snapshot chunk");
                var chunk=Convert.FromBase64String(text);
                if(state.MobParts.TryGetValue(index,out var previous))
                {if(!CryptographicOperations.FixedTimeEquals(previous,chunk))throw new InvalidOperationException("Mob snapshot chunk changed");}
                else {state.MobParts.Add(index,chunk);state.MobPartBytes+=chunk.Length;}
                state.MobPartCount=count;
                if(state.MobPartBytes>16*1024*1024)throw new InvalidOperationException("Mob snapshot transfer exceeded capacity");
                if(state.MobParts.Count==count)
                {
                    var assembled=new byte[state.MobPartBytes];var offset=0;
                    for(int i=0;i<count;i++){var block=state.MobParts[i];Buffer.BlockCopy(block,0,assembled,offset,block.Length);offset+=block.Length;}
                    if(Convert.ToHexString(SHA256.HashData(assembled))!=state.Header.MobHash)throw new InvalidOperationException("Mob snapshot checksum mismatch");
                    var manifest=JsonConvert.DeserializeObject<MobManifest>(Encoding.UTF8.GetString(assembled));
                    ValidateManifest(manifest,state.Header);state.Manifest=manifest;state.MobParts.Clear();state.MobPartBytes=0;
                }
            }
            else if(parts[0]=="CSFJACCEPT" && state.Header?.Epoch==epochValue && state.GraphApplied && state.ManifestApplied && AtWorldTarget(state.Header))
            {
                var accepted=JObject.Parse(parts[3]);
                if(!MatchesWorldTransfer(state.Header,(int)accepted["generation"],(long)accepted["revision"],(long)accepted["transfer"],(string)accepted["hash"]))return false;
                if((string)accepted["level"]!=state.Header.Level)throw new InvalidOperationException("Approved spawn world mismatch");
                state.AnchorX=(int)accepted["x"];state.AnchorY=(int)accepted["y"];
                state.Accepted=true;state.Finalizing=true;
            }
            else if(parts[0]=="CSFJFINISH" && state.Header?.Epoch==epochValue && state.SpawnApplied && AtWorldTarget(state.Header))
            {
                var finished=JObject.Parse(parts[3]);
                if(!MatchesWorldTransfer(state.Header,(int)finished["generation"],(long)finished["revision"],(long)finished["transfer"],(string)finished["hash"]))return false;
                if(!OpenClientWire(__instance,state))return false;
                state.Complete=true;state.LocalPointer=(Field("currentLevel") as Level).HashlinkPointer;state.LocalBuild=ClientBuild(__instance);
                if(!state.ActivationOnly)Lobby.GetMethod("SendRunReadyFromHero",Flags).Invoke(null,null);
                SendRequest(__instance,$"CSFJFINISHACK|{TransferNonce(state.Header,client.Nonce)}|{state.Header.Epoch}|{state.Header.Generation},{state.Header.MobRevision},{state.Header.TransferId}");
                Log.Information("[CoopSyncFixes] World join completed level={Level} epoch={Epoch}",state.Header.Level,state.Header.Epoch);
            }
        }
        catch(Exception error)
        {
            Log.Warning(error,"[CoopSyncFixes] Rejecting world join");
            RejectedConnections.GetValue(__instance,_=>new object());__result=false;
        }
        return false;
    }
    private static void PinWorldCaches(WorldJoin state)
    {
        var header=state.Header;
        if(state.Route!=null){PinRouteSlot(state,state.RouteLoadingSlot);return;}
        PinOneWorldGraph(header.Level,header.SeedPayload,state.Graph);
    }
    private static void PinOneWorldGraph(string level,string seedPayload,string graphText)
    {
        var seedParts=seedPayload.Split('|');
        if(seedParts.Length!=3 || seedParts[0]!=level)throw new InvalidOperationException("World seed identity mismatch");
        var seedType=WorldData.GetNestedType("RemoteLevelSeedSync",BindingFlags.NonPublic);
        var seed=Activator.CreateInstance(seedType,true);
        seedType.GetProperty("Seed").SetValue(seed,double.Parse(seedParts[2],CultureInfo.InvariantCulture));
        seedType.GetProperty("Seq").SetValue(seed,long.Parse(seedParts[1],CultureInfo.InvariantCulture));
        lock(WorldField("_levelSeedLock"))
        {((IDictionary)WorldField("_remoteLevelSeedsByLevelId"))[level]=seed;Monitor.PulseAll(WorldField("_levelSeedLock"));}
        var graphType=WorldData.GetNestedType("LevelGraphSync",BindingFlags.NonPublic);
        var graph=JsonConvert.DeserializeObject(graphText,graphType);
        graphType.GetProperty("ReceivedAtTick").SetValue(graph,Environment.TickCount64);
        lock(WorldField("_levelGraphLock"))
        {((IDictionary)WorldField("_remoteLevelGraphs"))[level]=graph;Monitor.PulseAll(WorldField("_levelGraphLock"));}
    }
    private static bool AtWorldTarget(WorldHeader header)
    {
        WorldJoin routed=null;
        if(header.RouteVersion==1 && (!WorldJoins.TryGetValue(ModEntry._net,out routed) || routed.Header!=header || routed.Route==null ||
            !routed.ActivationOnly && (routed.RouteCursor!=routed.Route.Steps.Count || routed.RouteExpected!=IntPtr.Zero)))return false;
        var level=dc.pr.Game.Class.ME?.hero?._level;
        var tracked=Field("currentLevel") as Level;
        return level?.map!=null && SecretSnapshotSafe(dc.pr.Game.Class.ME,level.map) && level.isSubLevel==header.SubLevel &&
            (header.RouteVersion!=1 || SameRouteMap(DescribeRouteMap(level.map),routed.Route.Target)) &&
            level.map.id?.ToString()==header.Level && BitConverter.DoubleToInt64Bits(level.map.seed)==BitConverter.DoubleToInt64Bits(header.Seed) &&
            tracked?.HashlinkPointer==level.HashlinkPointer && (bool)Field("s_levelIdentityReady") &&
            (int)Field("s_levelIdentityToken")==header.Generation && (int)Invoke("ComputeLevelIdentityToken",level)==header.Generation;
    }
    private static Delegate WorldLoadHook;
    private static void RegisterJoinLevelSeedHook()
    {
        // Generated native hook delegates reference runtime CoreLib directly. Build the adapter
        // from their actual runtime signature instead of mixing CoreLib and reference facades.
        var hookType=typeof(Game).Assembly.GetType("dc.pr.Hook_Game",true);
        var eventInfo=hookType.GetEvent("loadMainLevel");var signature=eventInfo.EventHandlerType.GetMethod("Invoke");
        var parameters=signature.GetParameters();var types=new Type[parameters.Length];
        for(int i=0;i<types.Length;i++)types[i]=parameters[i].ParameterType;
        var method=new DynamicMethod("CoopSyncJoinLoad",signature.ReturnType,types,typeof(CoopSyncFixes),true);
        var il=method.GetILGenerator();
        var loadScope=il.DeclareLocal(typeof(GeneratedRouteLoadScope));
        var result=signature.ReturnType==typeof(void)?null:il.DeclareLocal(signature.ReturnType);
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_3);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(BeginGeneratedRouteLoad),Flags));il.Emit(OpCodes.Stloc,loadScope);
        il.BeginExceptionBlock();
        // Ref<bool> is a ref struct and MUST remain on the stack; boxing it is invalid IL.
        for(int i=0;i<types.Length;i++)
        {
            if(i==5)
            {il.Emit(OpCodes.Ldarg,(short)3);il.Emit(OpCodes.Ldarg,(short)5);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(JoinForcedSeed),Flags));}
            else il.Emit(OpCodes.Ldarg,(short)i);
        }
        il.Emit(OpCodes.Callvirt,types[0].GetMethod("Invoke"));if(result!=null)il.Emit(OpCodes.Stloc,result);
        il.BeginFinallyBlock();il.Emit(OpCodes.Ldloc,loadScope);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(EndGeneratedRouteLoad),Flags));il.EndExceptionBlock();
        if(result!=null)il.Emit(OpCodes.Ldloc,result);il.Emit(OpCodes.Ret);
        WorldLoadHook=method.CreateDelegate(eventInfo.EventHandlerType);
        RuntimeHelpers.PrepareDelegate(WorldLoadHook);
        eventInfo.AddEventHandler(null,WorldLoadHook);
    }
    private static int? JoinForcedSeed(dc.String id,int? forcedSeed)
    {
        if(PendingWorld(ModEntry._net,out var state) && state.Routed && (state.Route==null?state.Header?.Level:state.Route.Graphs[state.RouteLoadingSlot].Map.Level)==id?.ToString())
        {
            var seed=state.Route==null?state.Header.Seed:state.Route.Graphs[state.RouteLoadingSlot].Map.Seed;
            if(seed!=Math.Truncate(seed) || seed<int.MinValue || seed>int.MaxValue)throw new InvalidOperationException("Host map seed cannot be represented by native forcedSeed");
            forcedSeed=(int)seed;PinWorldCaches(state);
        }
        return forcedSeed;
    }
    public void OnFrameUpdate(double dt)
    {
        RefreshPlayerColors();
        var net=ModEntry._net;
        ObserveCommittedSecretRoot();
        TickNativeRoutes(net);
        TickWireNotices(net);
        EnsureClientActivation(net);
        TickHostJoinLeases(net);
        TickNativeTransitionGates();
        if(!PendingWorld(net,out var state) || !net.IsAlive || !net.HasRemote)return;
        try
        {
            var now=Environment.TickCount64;
            if(!LocalActivationCurrent(state))return;
            TickClientRoute(state);
            if(now-state.Started>120000)throw new InvalidOperationException("World join timed out");
            if(state.Accepted)
            {
                if(!state.SpawnApplied && AtWorldTarget(state.Header) && (dc.pr.Game.Class.ME.curCine==null || HoldJoiningTransition(dc.pr.Game.Class.ME.curCine)))
                {
                    if(state.ActivationOnly){state.SpawnApplied=true;}
                    else {
                    var entry=ModEntry.Instance;
                    if((bool)typeof(ModEntry).GetMethod("IsLocalBossIntroCineActive",Flags).Invoke(null,null) ||
                        (bool)typeof(ModEntry).GetMethod("IsLocalPlayerDowned",Flags).Invoke(null,null))return;
                    var joining=dc.pr.Game.Class.ME.hero;
                    if(joining._level.map.getRoomAt(state.AnchorX,state.AnchorY)==null)throw new InvalidOperationException("Approved spawn has no room in joined map");
                    joining.GetType().GetMethod("setPosCase").Invoke(joining,new object[] {state.AnchorX,state.AnchorY,null,null});
                    if(joining.cx!=state.AnchorX || joining.cy!=state.AnchorY)throw new InvalidOperationException("Native placement did not reach approved cell");
                    state.SpawnApplied=true;Lobby.GetField("_midRunJoinSpawnPending",Flags).SetValue(null,false);
                    typeof(ModEntry).GetMethod("MarkDiveNetGuardAfterSpawnOrRoomChange",InstanceFlags).Invoke(entry,null);
                    typeof(ModEntry).GetMethod("SendCurrentRoomTarget",InstanceFlags).Invoke(entry,new object[] {true});
                    Log.Information("[CoopSyncFixes] Applied epoch-approved join spawn at {X}:{Y} in {Level}",state.AnchorX,state.AnchorY,state.Header.Level);
                    }
                }
                if(now>=state.NextRequest)
                {
                    state.NextRequest=now+1000;
                    var context=ClientConnections.GetValue(net,_=>throw new InvalidOperationException("Missing connection context"));
                    SendRequest(net,state.SpawnApplied?$"CSFJDONE|{TransferNonce(state.Header,context.Nonce)}|{state.Header.Epoch}|{state.Header.Generation},{state.Header.MobRevision},{state.Header.TransferId}":GetWorldRequest(context,state));
                }
                return;
            }
            if(now>=state.NextRequest)
            {
                state.NextRequest=now+1000;
                var context=ClientConnections.GetValue(net,_=>throw new InvalidOperationException("Missing connection context"));
                if(state.Header!=null && state.GraphApplied && state.ManifestApplied && AtWorldTarget(state.Header))
                    SendRequest(net,$"CSFJREADY|{TransferNonce(state.Header,context.Nonce)}|{state.Header.Epoch}|{state.Header.Generation},{state.Header.MobRevision},{state.Header.TransferId}");
                else if(state.Header==null || state.Graph==null || state.Manifest==null)SendRequest(net,GetWorldRequest(context,state));
            }
            if(state.Header!=null && state.GraphApplied && state.Manifest!=null && !state.ManifestApplied && AtWorldTarget(state.Header))ApplyJoinManifest(state);
            if(state.ActivationOnly)return;
            var game=dc.pr.Game.Class.ME;var hero=game?.hero;
            if(state.Header==null || state.Graph==null || state.Routed || hero?._level?.map==null || hero.destroyed || hero.life<=0 || game.curCine!=null)return;
            if(AtWorldTarget(state.Header) && state.GraphApplied)return;
            PinWorldCaches(state);state.Routed=true;
            Invoke("QuiesceForLevelTransition");
            var root=state.Route?.Root;Log.Information("[CoopSyncFixes] Routing late join to {Level} seed={Seed}",root?.Level??state.Header.Level,root?.Seed??state.Header.Seed);
            _=LevelTransition.Class.@goto((root?.Level??state.Header.Level).AsHaxeString());
        }
        catch(Exception error)
        {
            Log.Warning(error,"[CoopSyncFixes] Aborting incomplete world join");
            RejectedConnections.GetValue(net,_=>new object());ModEntry.Instance.StopNetworkFromMenu();
        }
    }
}
