using System;
using System.Collections;
using System.Reflection;
using DeadCellsMultiplayerMod;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    // Opt-in with the negotiated cinematic presentation module. Never drain a whole
    // queue: only skip the exact ordinary attack being appended in the current scope.
    static bool CineAttackExpiryInitialized;
    static long CineAttackAppendsSuppressed,CineAttackMissingOrStaleReceipt,CineAttackScopeFailures;
    static long CineAttackAppendAttempts,CineAttackFeatureRejected,CineAttackContextRejected,CineAttackCanonicalRejected,CineAttackPreparedScopes;
    static long CineAttackAttributionRejected,CineAttackOrdinaryRejected,CineAttackPeerRejected,CineAttackGhostRejected,CineAttackWeaponRejected;
    [ThreadStatic] static CineAttackAppendScope CurrentCineAttackAppend;
    sealed class CineAttackAppendScope
    {public object Queue;public NetNode.RemoteAttack Attack;public bool Used;}
    sealed class CineAttackPreviousScope {public CineAttackAppendScope Previous;}
    static MethodInfo CineAttackAppendMethod()=>typeof(NetNode).GetField("_pendingAttacks",CineInstance).FieldType.GetMethod("AddBounded",CineInstance);
    static void InitializeCinematicAttackExpiry(Harmony harmony)
    {
        if(CineAttackExpiryInitialized)return;
        InitializeReceiveAgeBridge(harmony);
        harmony.Patch(typeof(NetNode).GetMethod("HandleLine",CineInstance),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(BeginCineAttackAppend),Flags)){priority=HarmonyLib.Priority.First},
            finalizer:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(EndCineAttackAppend),Flags)));
        harmony.Patch(CineAttackAppendMethod(),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(KeepCineAttackAppend),Flags)));
        CineAttackExpiryInitialized=true;
    }
    static bool OrdinaryCineAttack(NetNode.RemoteAttack attack)=>attack.Action==RemoteAttackAction.Attack &&
        (attack.Slot==0 || attack.Slot==1) && attack.PermanentId>=-1 && !string.IsNullOrWhiteSpace(attack.Kind) &&
        attack.Kind.Length<=160 && !attack.Kind.StartsWith("__",StringComparison.Ordinal);
    static bool CanonicalCineAttackLine(string line)
    {
        var fields=(line??"").TrimEnd('\r','\n').Split('|');
        return (fields.Length==6 || fields.Length==7) && fields[0]=="ATK" && fields[fields.Length-1]=="ATK";
    }
    static bool SameCineAttack(NetNode.RemoteAttack a,NetNode.RemoteAttack b)=>a.Id==b.Id && a.Kind==b.Kind &&
        a.Slot==b.Slot && a.PermanentId==b.PermanentId && a.Ammo==b.Ammo && a.Action==b.Action;
    static bool FreshCineAttackReceipt(NetNode net,string line,int? sender,long now)
    {
        if(!TakeReceivedLineAge(net,line,sender,out var at) || now<at || now-at>1500)
        {System.Threading.Interlocked.Increment(ref CineAttackMissingOrStaleReceipt);return false;}
        return true;
    }
    static CineAttackAppendScope BuildCineAttackAppend(NetNode net,string line,int? sender)
    {
        if(!CineModuleInitialized || !CineAttackExpiryInitialized || !ReferenceEquals(net,ModEntry._net) || RejectedConnections.TryGetValue(net,out _)){CineAttackFeatureRejected++;return null;}
        if(!CinematicPresentationContext(out var game,out var level)){CineAttackContextRejected++;return null;}
        if(!CanonicalCineAttackLine(line)){CineAttackCanonicalRejected++;return null;}
        // A missing receipt, stale backlog or unknown attribution PRESERVES the base
        // append. Raw ATK has no incarnation/epoch field; never infer it from user ID.
        if(!FreshCineAttackReceipt(net,line,sender,Environment.TickCount64))return null;
        object[] parsed={line.Substring(line.IndexOf('|')+1),null,null,0,0,null,RemoteAttackAction.Attack};
        typeof(NetNode).GetMethod("ParseAttackPayload",Flags).Invoke(null,parsed);
        int? parsedId=(int?)parsed[1];int? effective=net.IsHost && sender.HasValue?sender:parsedId??sender;
        if(!effective.HasValue || effective<=0 || effective==net.id){CineAttackAttributionRejected++;return null;}
        var attack=new NetNode.RemoteAttack(effective.Value,(string)parsed[2],(int)parsed[3],(int)parsed[4],(int?)parsed[5],(RemoteAttackAction)parsed[6]);
        if(!OrdinaryCineAttack(attack)){CineAttackOrdinaryRejected++;return null;}
        if(net.IsHost){if(!CinematicPeers(net).Exists(p=>p.Id==attack.Id)){CineAttackPeerRejected++;return null;}}
        else if(net.id<=1 || !CineHandshakes.TryGetValue(net,out var handshake) || !handshake.WelcomeAccepted || !ClientConnections.TryGetValue(net,out _)){CineAttackPeerRejected++;return null;}
        string remoteLevel=null;
        lock(typeof(NetNode).GetField("_sync",CineInstance).GetValue(net))
        {
            var remotes=(IDictionary)typeof(NetNode).GetField("_remotes",CineInstance).GetValue(net);
            if(remotes.Contains(attack.Id))
            {
                var record=remotes[attack.Id];var fields=record.GetType();
                if((bool)fields.GetField("HasRemote",CineInstance).GetValue(record) && (bool)fields.GetField("HasPosition",CineInstance).GetValue(record))
                    remoteLevel=(string)fields.GetField("LevelId",CineInstance).GetValue(record);
            }
        }
        // Native eligibility is resolved outside _sync, before original HandleLine.
        var ghost=PresentationGhost(net,level,attack.Id,remoteLevel);
        if(CineRun(net)==null || ghost?.inventory==null){CineAttackGhostRejected++;return null;}
        var equipped=ghost.inventory.getEquippedWeaponOn(attack.Slot);
        if(!(bool)typeof(ModEntry).GetMethod("IsWeaponKindMatch",Flags).Invoke(null,new object[]{equipped,attack.Kind}) ||
            attack.PermanentId>0 && equipped.permanentId!=attack.PermanentId){CineAttackWeaponRejected++;return null;}
        CineAttackPreparedScopes++;return new CineAttackAppendScope {Queue=typeof(NetNode).GetField("_pendingAttacks",CineInstance).GetValue(net),Attack=attack};
    }
    static void BeginCineAttackAppend(NetNode __instance,string line,int? senderId,ref CineAttackPreviousScope __state)
    {
        if(CurrentCineAttackAppend==null && line?.StartsWith("ATK|",StringComparison.OrdinalIgnoreCase)!=true)return;
        __state=new CineAttackPreviousScope {Previous=CurrentCineAttackAppend};CurrentCineAttackAppend=null;
        if(line?.StartsWith("ATK|",StringComparison.OrdinalIgnoreCase)!=true)return;
        CineAttackAppendAttempts++;
        try{CurrentCineAttackAppend=BuildCineAttackAppend(__instance,line,senderId);}
        catch{System.Threading.Interlocked.Increment(ref CineAttackScopeFailures);} // Preserve base behavior on an unknown context.
    }
    static Exception EndCineAttackAppend(Exception __exception,CineAttackPreviousScope __state)
    {if(__state!=null)CurrentCineAttackAppend=__state.Previous;return __exception;}
    static bool KeepCineAttackAppend(object __instance,NetNode.RemoteAttack item)
    {
        // This callback runs under the original network _sync. It reads/writes only
        // managed scope fields, never native objects, locks, loot, input or weapons.
        var scope=CurrentCineAttackAppend;
        if(!CineModuleInitialized || !CineAttackExpiryInitialized || scope==null || scope.Used ||
            !ReferenceEquals(scope.Queue,__instance) || !OrdinaryCineAttack(item) || !SameCineAttack(scope.Attack,item))return true;
        scope.Used=true;System.Threading.Interlocked.Increment(ref CineAttackAppendsSuppressed);return false;
    }
}
