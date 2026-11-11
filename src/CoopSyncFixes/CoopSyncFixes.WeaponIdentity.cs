using System;
using System.Reflection;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    // Enabled by the integrated entry point. Pinned 0.5.1 rejects every negative
    // permanentId before its existing no-ID (0) kind/slot resolution can run.
    // Native starting weapons legitimately use the unassigned sentinel -1.
    // Normalize only this managed input; never rewrite the wire or InventItem.
    internal static bool WeaponIdentityReady;
    internal static long WeaponIdentityTryNormalizations;
    internal static long WeaponIdentityApplyNormalizations;
    private static readonly MethodInfo WeaponIdentityClientIndex=typeof(ModEntry).GetMethod("TryGetClientIndex",Flags);
    private static readonly FieldInfo WeaponIdentityNet=typeof(ModEntry).GetField("_net",Flags);

    private static void InitializeRemoteWeaponIdentity(Harmony harmony)
    {
        if(Disabled("WEAPON_IDENTITY") || WeaponIdentityReady)return;
        var signature=new[]{typeof(int),typeof(string),typeof(int),typeof(int),typeof(int?)};
        var instanceFlags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        var guarded=typeof(ModEntry).GetMethod("TryApplyRemoteWeaponUpdate",instanceFlags,null,signature,null)
            ??throw new MissingMethodException(typeof(ModEntry).FullName,"TryApplyRemoteWeaponUpdate");
        var direct=typeof(ModEntry).GetMethod("ApplyRemoteWeaponUpdate",instanceFlags,null,signature,null)
            ??throw new MissingMethodException(typeof(ModEntry).FullName,"ApplyRemoteWeaponUpdate");
        if(WeaponIdentityClientIndex==null || WeaponIdentityNet==null)
            throw new MissingMemberException("Pinned remote weapon identity context is unavailable");
        harmony.Patch(guarded,prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(NormalizeGuardedWeaponIdentity),Flags)));
        harmony.Patch(direct,prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(NormalizeDirectWeaponIdentity),Flags)));
        WeaponIdentityReady=true;
    }

    private static bool CanNormalizeRemoteWeaponIdentity(int remoteId,string kindId,int slot,int permanentId)
    {
        if(!WeaponIdentityReady || Disabled("WEAPON_IDENTITY") || permanentId!=-1 || (slot!=0 && slot!=1) ||
            string.IsNullOrWhiteSpace(kindId) || kindId.Length>160)return false;
        // Keep the original mapping's local/self/out-of-range exclusions. This
        // reads managed network context and does not touch native ghost objects.
        try {
            var net=WeaponIdentityNet.GetValue(null) as NetNode;
            if(net==null)return false;
            return (bool)WeaponIdentityClientIndex.Invoke(null,new object[]{net.id,remoteId,0});
        }catch {return false;}
    }

    private static void NormalizeGuardedWeaponIdentity(int remoteId,string kindId,int slot,ref int permanentId)
    {
        if(!CanNormalizeRemoteWeaponIdentity(remoteId,kindId,slot,permanentId))return;
        permanentId=0;
        Interlocked.Increment(ref WeaponIdentityTryNormalizations);
    }

    private static void NormalizeDirectWeaponIdentity(int remoteId,string kindId,int slot,ref int permanentId)
    {
        if(!CanNormalizeRemoteWeaponIdentity(remoteId,kindId,slot,permanentId))return;
        permanentId=0;
        Interlocked.Increment(ref WeaponIdentityApplyNormalizations);
    }
}
