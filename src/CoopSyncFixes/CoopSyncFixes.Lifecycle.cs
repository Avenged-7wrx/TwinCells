using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using dc.en;
using DeadCellsMultiplayerMod;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    private static void InitializeLifecycleFence(Harmony harmony)
    {
        if(Disabled("LIFECYCLE")) return;
        Patch(harmony,Mobs,"ApplyIncomingHostMobStates",nameof(FenceDeadStates));
        Patch(harmony,Mobs,"ApplyIncomingHostMobMoves",nameof(FenceDeadMoves));
        Patch(harmony,Mobs,"ApplyIncomingHostMobAttacks",nameof(FenceDeadAttacks));
        Patch(harmony,Mobs,"ApplyIncomingMobDies",nameof(FenceDeadDies));
        Patch(harmony,Mobs,"ApplyIncomingMobDies",nameof(RememberDeathIntents));
        Patch(harmony,Mobs,"Hook_Mob_onDie",nameof(ObserveDeathIdentity),nameof(CompleteDeathIdentity));
        Patch(harmony,Mobs,"FlushPendingClientAuthoritativeDeaths",nameof(KeepDeferredDeathUntilAwake));
        Patch(harmony,Mobs,"RetryPendingClientBossAttacks",nameof(FencePendingAttacks));
        Patch(harmony,Mobs,"ResolveMobFromHitLocked",nameof(FenceDeadHit));
        Patch(harmony,Mobs,"TryRecoverTombstonedSyncIdLocked",nameof(FenceDeadRecovery));
    }
    private sealed class DeathStamp {public int Generation,Id,ClientBuild;}
    private sealed class IntentLedger
    {
        public string Run;public readonly Dictionary<long,DeathStamp> Ids=new();public readonly Dictionary<IntPtr,DeathStamp> Pointers=new();
    }
    private static readonly ConditionalWeakTable<NetNode,IntentLedger> DeathIntents=new();
    private static IntentLedger CurrentIntents()
    {
        var net=ModEntry._net;if(net==null || net.IsHost)return null;
        var ledger=DeathIntents.GetValue(net,_=>new IntentLedger());
        var descriptor=Coordinator.GetMethod("GetCurrentRemoteDescriptor",Flags).Invoke(null,null);
        var run=descriptor==null?"":Property(descriptor,"SessionId")+":"+Property(descriptor,"RunId")+":"+Property(descriptor,"Sequence");
        if(ledger.Run!=run){ledger.Ids.Clear();ledger.Pointers.Clear();ledger.Run=run;}
        return ledger;
    }
    private static bool HasDeathIntent(int generation,int id)
    {
        var ledger=CurrentIntents();return ledger!=null && ledger.Ids.ContainsKey(((long)generation<<32)^(uint)id);
    }
    private static bool HasCurrentBuildDeathIntent(int generation,int id)
    {
        var ledger=CurrentIntents();return ledger!=null && ledger.Ids.TryGetValue(((long)generation<<32)^(uint)id,out var stamp) &&
            stamp.ClientBuild==(int)Field("s_levelIdentityGeneration");
    }
    private static bool DeadOnClient(int generation,int id) => ModEntry._net != null && !ModEntry._net.IsHost &&
        Current(generation) && (ConfirmedDead(generation,id) || HasDeathIntent(generation,id));
    private static void RememberDeathIntents(IReadOnlyList<NetNode.MobDie> dies)
    {
        if(dies==null || ModEntry._net==null || ModEntry._net.IsHost)return;
        lock(Gate)
        {
            var ledger=CurrentIntents();var forward=(IDictionary)Field("IdToMob");
            foreach(var die in dies)
            {
                if(die.UserId!=1 || die.MobIndex<=0 || !Current(die.Generation))continue;
                var key=((long)die.Generation<<32)^(uint)die.MobIndex;
                if(!ledger.Ids.TryGetValue(key,out var stamp))
                {stamp=new DeathStamp {Generation=die.Generation,Id=die.MobIndex};ledger.Ids.Add(key,stamp);}
                stamp.ClientBuild=(int)Field("s_levelIdentityGeneration");
                if(forward.Contains(die.MobIndex) && forward[die.MobIndex] is Mob mob)
                {
                    ledger.Pointers[mob.HashlinkPointer]=stamp;
                    var targets=(IDictionary)Field("clientMobTargets");
                    if(targets.Contains(mob))
                    {
                        var old=targets[mob];var type=old.GetType();object Get(string name)=>type.GetField(name).GetValue(old);
                        targets[mob]=Activator.CreateInstance(type,new object[] {Get("X"),Get("Y"),Get("Dir"),0,Get("MaxLife"),
                            Get("AnimPayload"),Get("StatePayload"),Get("Time"),Get("Dx"),Get("Dy"),Get("ReceivedFrame"),Get("ForcePositionSnap"),Get("ForceVerticalPositionSnap")});
                    }
                    ((IDictionary)Field("clientLastReportedMobLife"))[mob]=0;
                }
            }
        }
    }
    private static bool KeepDeferredDeathUntilAwake()
    {
        if(ModEntry._net==null || ModEntry._net.IsHost)return true;
        if((bool)Invoke("IsSyncQuiescedForTransition"))return false;
        List<Mob> pending;
        lock(Gate)
        {
            pending=new List<Mob>();foreach(Mob mob in (IEnumerable)Field("s_pendingCulledMobDeaths"))pending.Add(mob);
        }
        foreach(var mob in pending)
        {
            if(mob==null)continue;
            try
            {
                if(mob.destroyed){Invoke("ClearPendingClientAuthoritativeDeath",mob);continue;}
                // Hiding then abandoning a culled death loses native completion and loot callbacks.
                // Keep its zero-HP intent until normal room simulation makes onDie safe.
                if((bool)Invoke("IsMobCulledLocally",mob))
                {
                    if(!mob.initDone)continue;
                    // A zero-HP culled entity may stop receiving the vanilla update that refreshes
                    // these cached flags. Ask the native visibility predicates, then use its normal
                    // activation callback only when the engine now considers this room awake.
                    var outOfGame=mob._isOutOfGame();var onScreen=mob._isOnScreen();
                    if(outOfGame && !onScreen)continue;
                    mob.isOnScreen=onScreen;mob.isOutOfGame=outOfGame;
                    mob.onOutOfGameChange();mob.lastOutOfGame=outOfGame;
                }
                Invoke("RunAuthoritativeClientDeathNow",mob);
                if(mob.destroyed || mob.onDieDone)Invoke("ClearPendingClientAuthoritativeDeath",mob);
            }
            catch { /* A transient native failure must not discard the pending death. */ }
        }
        return false;
    }
    private static void ObserveDeathIdentity(Mob self,out DeathStamp __state)
    {
        __state=null;
        if(self==null || ModEntry._net==null || ModEntry._net.IsHost || (int)Field("authoritativeClientMobDieDepth")<=0)return;
        lock(Gate)
        {
            var ledger=CurrentIntents();
            if(ledger!=null && ledger.Pointers.TryGetValue(self.HashlinkPointer,out var stamp) && Current(stamp.Generation))__state=stamp;
        }
    }
    private static void CompleteDeathIdentity(Mob self,DeathStamp __state)
    {
        if(__state==null || self==null)return;
        try
        {
            if(!self.destroyed && !self.onDieDone)return;
            lock(Gate)
            {
                if(!Current(__state.Generation))return;
                var ledger=Field("s_clientMobLifecycle");ledger.GetType().GetMethod("MarkDead").Invoke(ledger,new object[] {__state.Generation,__state.Id});
            }
        }
        catch { }
    }
    private static IReadOnlyList<T> WithoutDead<T>(IReadOnlyList<T> input,Func<T,int> generation,Func<T,int> id)
    {
        if(input==null || ModEntry._net==null || ModEntry._net.IsHost) return input;
        lock(Gate)
        {
            List<T> filtered=null;
            for(int i=0;i<input.Count;i++)
            {
                var item=input[i];
                if(DeadOnClient(generation(item),id(item)))
                {
                    if(filtered==null) {filtered=new List<T>(input.Count-1);for(int j=0;j<i;j++)filtered.Add(input[j]);}
                }
                else filtered?.Add(item);
            }
            return filtered==null ? input : filtered.Count==0 ? Array.Empty<T>() : filtered;
        }
    }
    private static void FenceDeadStates(ref IReadOnlyList<NetNode.MobStateSnapshot> states)
        => states=WithoutDead(states,s=>s.Generation,s=>s.Index);
    private static void FenceDeadMoves(ref IReadOnlyList<NetNode.MobMoveSnapshot> moves)
        => moves=WithoutDead(moves,s=>s.Generation,s=>s.Index);
    private static void FenceDeadAttacks(ref IReadOnlyList<NetNode.MobAttack> attacks)
        => attacks=WithoutDead(attacks,s=>s.Generation,s=>s.Index);
    private static void FenceDeadDies(ref IReadOnlyList<NetNode.MobDie> dies)
    {
        if(dies==null || ModEntry._net==null || ModEntry._net.IsHost)return;
        lock(Gate)
        {
            List<NetNode.MobDie> filtered=null;
            for(int i=0;i<dies.Count;i++)
            {
                var item=dies[i];
                if(Current(item.Generation) && ConfirmedDead(item.Generation,item.MobIndex))
                {if(filtered==null){filtered=new List<NetNode.MobDie>();for(int j=0;j<i;j++)filtered.Add(dies[j]);}}
                else filtered?.Add(item);
            }
            if(filtered!=null)dies=filtered;
        }
    }
    private static void FencePendingAttacks()
    {
        if(ModEntry._net==null || ModEntry._net.IsHost)return;
        lock(Gate)
        {
            var pending=(IList)Field("clientPendingBossAttacks");
            for(int i=pending.Count-1;i>=0;i--)
            {
                var item=pending[i];
                var attack=(NetNode.MobAttack)item.GetType().GetField("Attack").GetValue(item);
                if(DeadOnClient(attack.Generation,attack.Index))pending.RemoveAt(i);
            }
        }
    }
    private static bool FenceDeadHit(NetNode.MobHit hit,ref Mob __result)
    {
        lock(Gate)
        {
            if(!DeadOnClient(hit.Generation,hit.MobIndex))return true;
            __result=null;return false;
        }
    }
    private static bool FenceDeadRecovery(int syncId,ref Mob recovered,ref bool __result)
    {
        lock(Gate)
        {
            if(!DeadOnClient((int)Field("s_levelIdentityToken"),syncId))return true;
            recovered=null;__result=false;return false;
        }
    }
}
