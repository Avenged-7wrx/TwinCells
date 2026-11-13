using System;
using System.Runtime.CompilerServices;
using dc.pr;
using DeadCellsMultiplayerMod;
using Newtonsoft.Json;

public sealed partial class CoopSyncFixes
{
    private sealed class ActivationRequest
    {
        public string Token,Level;public double Seed;public int Generation;
        public Guid SessionId;public long RunId,Number;public int Sequence;
    }
    private static readonly ConditionalWeakTable<object,ActivationRequest> ActivationRequests=new();
    private sealed class ClientBuildStamp {public int Value;}
    private static readonly ConditionalWeakTable<NetNode,ClientBuildStamp> ClientBuilds=new();
    private static int ClientBuild(NetNode net)=>ClientBuilds.GetValue(net,_=>new ClientBuildStamp()).Value;
    private static long NextActivationRequest;
    private static string TransferNonce(WorldHeader header,string nonce)=>header?.ActivationOnly==true?nonce+"."+header.RequestToken:nonce;
    private static JoinContext ActivationContext(JoinContext connection,ActivationRequest request)=>new JoinContext {
        Nonce=connection.Nonce,SessionId=request.SessionId,RunId=request.RunId,Sequence=request.Sequence};
    private static bool ActivationRunMatches(WorldJoin state,JoinContext connection,object descriptor)
    {
        if(state.ActivationOnly)return state.Request!=null && JoinContext.RequestMatches(connection,state.Request,descriptor);
        if(state.Header==null)return connection.Matches(descriptor);
        return JoinContext.SameRun(state.Header.SessionId,state.Header.RunId,state.Header.Sequence,descriptor);
    }
    private static void EnsureClientActivation(NetNode net)
    {
        if(Disabled("WORLD") || Disabled("ACTIVATION") || net==null || net.IsHost || !net.IsAlive ||
            !ClientConnections.TryGetValue(net,out var context))return;
        var level=ActiveBranch();var tracked=Field("currentLevel") as Level;
        if(level?.map==null || tracked?.HashlinkPointer!=level.HashlinkPointer || !(bool)Field("s_levelIdentityReady"))return;
        var descriptor=Coordinator.GetMethod("GetCurrentRemoteDescriptor",Flags).Invoke(null,null);
        if(descriptor==null)return;
        var build=ClientBuild(net);
        if(WorldJoins.TryGetValue(net,out var prior))
        {
            if(ActivationRunMatches(prior,context,descriptor))
            {
                if(!prior.ActivationOnly && !prior.Complete)return;
                if(prior.LocalPointer==level.HashlinkPointer && prior.LocalBuild==build && !NeedsWireRefresh(net,prior,level))return;
            }
        }
        else if(context.LateJoin && context.Matches(descriptor))return;
        var request=new ActivationRequest {Token=Guid.NewGuid().ToString("N"),Number=System.Threading.Interlocked.Increment(ref NextActivationRequest),Level=level.map.id.ToString(),Seed=level.map.seed,
            Generation=(int)Field("s_levelIdentityToken"),SessionId=(Guid)Property(descriptor,"SessionId"),
            RunId=(long)Property(descriptor,"RunId"),Sequence=(int)Property(descriptor,"Sequence")};
        var state=new WorldJoin {ActivationOnly=true,Request=request,LocalPointer=level.HashlinkPointer,LocalBuild=build,Started=Environment.TickCount64};
        CaptureExpectedWire(net,state);
        WorldJoins.Remove(net);WorldJoins.Add(net,state);
    }
    private static bool LocalActivationCurrent(WorldJoin state)
    {
        if(!state.ActivationOnly)return true;
        var descriptor=Coordinator.GetMethod("GetCurrentRemoteDescriptor",Flags).Invoke(null,null);
        if(!ClientConnections.TryGetValue(ModEntry._net,out var context) || !ActivationRunMatches(state,context,descriptor))return false;
        var level=Game.Class.ME?.hero?._level;var tracked=Field("currentLevel") as Level;
        return level?.HashlinkPointer==state.LocalPointer && tracked?.HashlinkPointer==state.LocalPointer &&
            ClientBuild(ModEntry._net)==state.LocalBuild && (bool)Field("s_levelIdentityReady");
    }
    private static bool ActivationTargetMatches(ActivationRequest request,WorldHeader header)=>request!=null && header!=null &&
        request.Level==header.Level && request.Generation==header.Generation &&
        BitConverter.DoubleToInt64Bits(request.Seed)==BitConverter.DoubleToInt64Bits(header.Seed);
    private static string GetWorldRequest(JoinContext context,WorldJoin state)=>state.ActivationOnly?
        $"CSFJACT|{context.Nonce}|0|"+JsonConvert.SerializeObject(state.Request):$"CSFJGET|{context.Nonce}";
}
