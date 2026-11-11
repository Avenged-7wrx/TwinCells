using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeadCellsMultiplayerMod;

public sealed partial class CoopSyncFixes
{
    private sealed class WorldTransferSlot
    {
        public long Serial;public WorldHeader Header;public string[] Lines;
        public int Sending,ControlSending,Started,Coalesced;
        public bool FinishAcknowledged,Cancelled;public long LeaseUntil;
    }
    private static readonly ConditionalWeakTable<object,WorldTransferSlot> WorldTransfers=new();
    private sealed class IssuedFinish {public WorldTransferSlot Slot;}
    private static readonly ConditionalWeakTable<object,IssuedFinish> IssuedFinishes=new();
    private static bool SameWorldRevision(WorldHeader a,WorldHeader b)=>a!=null && b!=null && a.Epoch==b.Epoch && a.Generation==b.Generation &&
        a.Level==b.Level && BitConverter.DoubleToInt64Bits(a.Seed)==BitConverter.DoubleToInt64Bits(b.Seed) && a.GraphSequence==b.GraphSequence &&
        a.GraphHash==b.GraphHash && a.MobRevision==b.MobRevision && a.Nonce==b.Nonce && a.ActivationOnly==b.ActivationOnly && a.RequestToken==b.RequestToken &&
        a.HostRunNonce==b.HostRunNonce && a.Floor==b.Floor && a.SubLevel==b.SubLevel && a.RouteVersion==b.RouteVersion && a.RouteHash==b.RouteHash;
    private static WorldTransferSlot FreezeWorldTransfer(object connection,JoinContext context,WorldHeader captured,string graph,string mobs)
    {
        var slot=WorldTransfers.GetValue(connection,_=>new WorldTransferSlot());
        // Frame, HP and coordinates can change without a birth/death revision. Retries must
        // retain one exact byte snapshot, while live packets catch up after initial binding.
        if(Disabled("TRANSFERS") || !SameWorldRevision(slot.Header,captured))
        {
            var previous=slot;
            captured.TransferId=previous.Serial+1;
            slot=new WorldTransferSlot {Serial=captured.TransferId,Header=captured,
                LeaseUntil=previous.Header?.RequestToken==captured.RequestToken && !previous.FinishAcknowledged?previous.LeaseUntil:0};
            slot.Lines=BuildWorldTransfer(context,captured,graph,mobs);
            previous.Cancelled=true;WorldTransfers.Remove(connection);WorldTransfers.Add(connection,slot);
        }
        return slot;
    }
    private static bool TryBeginWorldSend(WorldTransferSlot slot)
    {
        if(Disabled("TRANSFERS")){Interlocked.Increment(ref slot.Started);return true;}
        if(Interlocked.CompareExchange(ref slot.Sending,1,0)!=0){Interlocked.Increment(ref slot.Coalesced);return false;}
        Interlocked.Increment(ref slot.Started);return true;
    }
    private static async Task QueueFrozenWorld(NetNode net,object connection,WorldTransferSlot slot)
    {
        if(!TryBeginWorldSend(slot))return;
        var lines=slot.Lines;
        try {await SendWorld(net,connection,lines).ConfigureAwait(false);}
        finally {Volatile.Write(ref slot.Sending,0);}
    }
    private static bool MatchesWorldTransfer(WorldHeader header,int generation,long revision,long transfer,string hash=null)
        =>header!=null && header.Generation==generation && (Disabled("TRANSFERS") || header.MobRevision==revision && header.TransferId==transfer &&
            (hash==null || header.MobHash==hash));
    private static bool IsTransferStillCurrent(NetNode net,object connection,WorldHeader header)
    {
        if(!net.IsAlive || !WorldTransfers.TryGetValue(connection,out var slot) || slot.Cancelled || slot.Header!=header ||
            !HostEpochs.TryGetValue(net,out var epoch) || Volatile.Read(ref epoch.Epoch)!=header.Epoch)return false;
        var active=HostManifests.TryGetValue(net,out var manifests)?manifests.Active:null;
        return active?.Epoch==header.Epoch && Volatile.Read(ref active.Revision)==header.MobRevision;
    }
    private static async Task QueueWorldControl(NetNode net,object connection,WorldTransferSlot slot,string line)
    {
        if(Interlocked.CompareExchange(ref slot.ControlSending,1,0)!=0)return;
        var header=slot.Header;
        try
        {
            if(line.StartsWith("CSFJFINISH|",StringComparison.Ordinal))IssuedFinishes.GetValue(connection,_=>new IssuedFinish()).Slot=slot;
            await SendWorldCore(net,connection,header,new[] {line}).ConfigureAwait(false);
        }
        finally {Volatile.Write(ref slot.ControlSending,0);}
    }
    private static bool AcknowledgeIssuedFinish(NetNode net,object connection,string[] parts)
    {
        if(parts.Length!=4 || !IssuedFinishes.TryGetValue(connection,out var issued) || issued.Slot?.Header==null)return false;
        var slot=issued.Slot;var header=slot.Header;var ack=parts[3].Split(',');
        if(parts[1]!=TransferNonce(header,header.Nonce) || !HostEpochs.TryGetValue(net,out var epoch) || epoch.Epoch!=header.Epoch ||
            !long.TryParse(parts[2],out var e) || e!=header.Epoch || ack.Length!=3 || !int.TryParse(ack[0],out var generation) ||
            !long.TryParse(ack[1],out var revision) || !long.TryParse(ack[2],out var transfer) || !MatchesWorldTransfer(header,generation,revision,transfer))return false;
        slot.FinishAcknowledged=true;
        if(WorldTransfers.TryGetValue(connection,out var current) && current.Header?.Epoch==header.Epoch && current.Header.RequestToken==header.RequestToken)
            current.FinishAcknowledged=true;
        return true;
    }
}
