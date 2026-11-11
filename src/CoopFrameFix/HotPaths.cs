using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using dc.en;
using DeadCellsMultiplayerMod;
using HarmonyLib;

// Two exact shortcuts in the community mod 0.5.1 (same results, less work and garbage). Host side mostly.
//
// 1. Boss-part verdict memo.
//    MobsSynchronization.ScanHostBossPartDespawns asks IsBossOwnedTransientPart(mob) for every tracked mob every
//    frame. For an ordinary mob the answer is decided by IsBossRelatedEntity(SafePartType(mob)), which rebuilds
//    the mob's type signature each time (mob.type.ToString() creates a new game string, plus two managed
//    strings, a lower-cased copy and up to eight Contains). The signature of a mob does not change during its
//    life (the mod itself and CoopPerfFix rely on that), so the type part is remembered per mob:
//        IsBossOwnedTransientPart(mob) = !combatant && level.boss != mob && typePart(mob)
//    typePart == false  -> the original returns false on every path: answered at once.
//    typePart == true   -> the original runs unchanged (it needs the live combatant / level.boss checks).
//    The memo is learned only while the mob's type id is readable, and every 64th call per mob the type part is
//    computed again and the memo corrected, so a wrong memory could not last longer than a fraction of a second.
//
// 2. Affect payload without the dynamic binder.
//    MobsSynchronization.BuildMobAffectPresencePayload was compiled with C# `dynamic`: for every affect slot
//    it runs three DLR call sites and boxes an int and a bool (measured: about 26 KB of garbage per call, and
//    up to 48 calls in one frame every 1.5 s). The replacement is the same loop with static types.
//    With COOP_FRAME_STATS=1 every 32nd payload is also built by a private copy of the original and compared.
//
// The two patches are installed independently; a failure of one leaves the other in place.
public static class HotPaths
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string Owner="CoopFrameFix.HotPaths.20261110";
    const BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
    sealed class Verdict{public bool TypePart;public int Calls;}
    static readonly ConditionalWeakTable<Mob,Verdict> PartVerdicts=new();
    static Func<Mob,string> SafePartType,MobTypeId;
    static Func<string,bool> IsBossRelatedEntity;
    static Func<Mob,string> OriginalAffectPayload;
    static bool Stats;
    static long PartCalls,PartShort,PartOriginal,PartLearned,PartRecheck,PartCorrected,AffectCalls,AffectNonEmpty,AffectShadow,AffectMismatch,FaultCount;
    public static bool BossPartReady{get;private set;}
    public static bool AffectReady{get;private set;}
    public static bool BossPartDisabled{get;private set;}
    public static bool AffectDisabled{get;private set;}
    public static string BossPartError{get;private set;}
    public static string AffectError{get;private set;}
    public static string StatsLine=>string.Create(CultureInfo.InvariantCulture,
        $"partCalls={Interlocked.Read(ref PartCalls)} partShort={Interlocked.Read(ref PartShort)} partOriginal={Interlocked.Read(ref PartOriginal)} partLearned={Interlocked.Read(ref PartLearned)} partRecheck={Interlocked.Read(ref PartRecheck)} partCorrected={Interlocked.Read(ref PartCorrected)} affectCalls={Interlocked.Read(ref AffectCalls)} affectNonEmpty={Interlocked.Read(ref AffectNonEmpty)} affectShadow={Interlocked.Read(ref AffectShadow)} affectMismatch={Interlocked.Read(ref AffectMismatch)} hotErrors={Interlocked.Read(ref FaultCount)}");

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static string OriginalAffectStub(Mob mob)=>throw new NotImplementedException("reverse patch target");
    static Type Sync()
    {
        var assembly=typeof(ModEntry).Assembly;
        if(assembly.GetName().Version!=new Version(0,5,1,0)||Hash(assembly.Location)!=BasePin)throw new InvalidOperationException("CoopFrameFix hot paths require exact community 0.5.1");
        return assembly.GetType("DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization",true);
    }

    public static void Install()
    {
        Stats=Environment.GetEnvironmentVariable("COOP_FRAME_STATS")=="1";
        BossPartDisabled=Environment.GetEnvironmentVariable("COOP_FRAME_BOSSPART_DISABLE")=="1";
        AffectDisabled=Environment.GetEnvironmentVariable("COOP_FRAME_AFFECT_DISABLE")=="1";
        if(!BossPartDisabled&&!BossPartReady&&BossPartError==null)
        {
            MethodInfo part=null;var harmony=new Harmony(Owner+".BossPart");
            try
            {
                var sync=Sync();
                part=sync.GetMethod("IsBossOwnedTransientPart",S,null,new[]{typeof(Mob)},null);
                var safe=sync.GetMethod("SafePartType",S,null,new[]{typeof(Mob)},null);
                var related=sync.GetMethod("IsBossRelatedEntity",S,null,new[]{typeof(string)},null);
                var typeId=sync.GetMethod("GetMobTypeIdSafe",S,null,new[]{typeof(Mob)},null);
                if(part?.ReturnType!=typeof(bool)||safe?.ReturnType!=typeof(string)||related?.ReturnType!=typeof(bool)||typeId?.ReturnType!=typeof(string))
                    throw new InvalidOperationException("Pinned boss-part members changed");
                SafePartType=(Func<Mob,string>)Delegate.CreateDelegate(typeof(Func<Mob,string>),safe);
                IsBossRelatedEntity=(Func<string,bool>)Delegate.CreateDelegate(typeof(Func<string,bool>),related);
                MobTypeId=(Func<Mob,string>)Delegate.CreateDelegate(typeof(Func<Mob,string>),typeId);
                harmony.Patch(part,prefix:new HarmonyMethod(typeof(HotPaths).GetMethod(nameof(BeforeBossPart),BindingFlags.Static|BindingFlags.NonPublic)));
                BossPartReady=true;
            }
            catch(Exception error)
            {
                BossPartError=error.GetBaseException().Message;
                try{if(part!=null)harmony.Unpatch(part,HarmonyPatchType.All,harmony.Id);}catch{}
            }
        }
        if(!AffectDisabled&&!AffectReady&&AffectError==null)
        {
            MethodInfo affect=null;var harmony=new Harmony(Owner+".Affect");
            try
            {
                var sync=Sync();
                affect=sync.GetMethod("BuildMobAffectPresencePayload",S,null,new[]{typeof(Mob)},null);
                var dynLength=sync.GetMethod("TryGetDynLength",S,null,new[]{typeof(object)},null);
                if(affect?.ReturnType!=typeof(string)||dynLength?.ReturnType!=typeof(int))throw new InvalidOperationException("Pinned affect payload members changed");
                if(Stats)
                {
                    // A private copy of the original body, only for the sampled comparison in stats mode.
                    try
                    {
                        var copy=Harmony.ReversePatch(affect,new HarmonyMethod(typeof(HotPaths).GetMethod(nameof(OriginalAffectStub),BindingFlags.Static|BindingFlags.NonPublic)));
                        OriginalAffectPayload=(Func<Mob,string>)Delegate.CreateDelegate(typeof(Func<Mob,string>),copy);
                    }
                    catch{OriginalAffectPayload=null;Interlocked.Increment(ref FaultCount);}
                }
                harmony.Patch(affect,prefix:new HarmonyMethod(typeof(HotPaths).GetMethod(nameof(BeforeAffectPayload),BindingFlags.Static|BindingFlags.NonPublic)));
                AffectReady=true;
            }
            catch(Exception error)
            {
                AffectError=error.GetBaseException().Message;
                try{if(affect!=null)harmony.Unpatch(affect,HarmonyPatchType.All,harmony.Id);}catch{}
            }
        }
        if(BossPartError!=null||AffectError!=null)throw new InvalidOperationException("boss part: "+(BossPartError??"ok")+"; affect payload: "+(AffectError??"ok"));
    }

    // ---- 1. boss-part verdict memo ----
    static bool BeforeBossPart(Mob __0,ref bool __result)
    {
        if(__0==null)return true;
        try
        {
            if(Stats)Interlocked.Increment(ref PartCalls);
            if(!PartVerdicts.TryGetValue(__0,out var verdict))
            {
                // Learn only from a complete signature: the mob's own type id must be readable now. (The signature
                // builder falls back to the class name alone when it is not; that partial text is never remembered.)
                if(string.IsNullOrEmpty(MobTypeId(__0)))return true;
                string type=SafePartType(__0);
                if(string.IsNullOrEmpty(type))return true;
                verdict=PartVerdicts.GetValue(__0,_=>new Verdict{TypePart=IsBossRelatedEntity(type)});
                if(Stats)Interlocked.Increment(ref PartLearned);
            }
            else if((++verdict.Calls&63)==0)
            {
                // Every 64th call per mob: compute the type part again; a difference corrects the memo and lets the
                // original answer this call.
                if(Stats)Interlocked.Increment(ref PartRecheck);
                string type=SafePartType(__0);
                if(!string.IsNullOrEmpty(type)&&!string.IsNullOrEmpty(MobTypeId(__0)))
                {
                    bool fresh=IsBossRelatedEntity(type);
                    if(fresh!=verdict.TypePart){Interlocked.Increment(ref PartCorrected);verdict.TypePart=fresh;return true;}
                }
            }
            if(verdict.TypePart){if(Stats)Interlocked.Increment(ref PartOriginal);return true;}
            if(Stats)Interlocked.Increment(ref PartShort);
            __result=false;return false;
        }
        catch{Interlocked.Increment(ref FaultCount);return true;}
    }

    // ---- 2. affect payload, statically typed ----
    // Same statements as the release body (MonsterSynchronization.HostSend.cs BuildMobAffectPresencePayload):
    // slot i is listed when it holds an ArrayObj whose length is positive (TryGetDynLength), indices joined by '.'.
    static string AffectPayload(Mob mob)
    {
        if(mob==null)return string.Empty;
        try
        {
            var affects=mob.getAllAffects();
            if(affects==null||affects.length<=0)return string.Empty;
            StringBuilder builder=null;
            for(int i=0;i<affects.length;i++)
            {
                object slot=affects.getDyn(i);
                if(DynLength(slot)<=0)continue;
                builder??=new StringBuilder(affects.length*3);
                if(builder.Length>0)builder.Append('.');
                builder.Append(i.ToString(CultureInfo.InvariantCulture));
            }
            return builder?.ToString()??string.Empty;
        }
        catch{return string.Empty;}
    }
    static int DynLength(object value)
    {
        if(!(value is dc.hl.types.ArrayObj array))return 0;
        try{return array.length;}catch{return 0;}
    }
    static bool BeforeAffectPayload(Mob __0,ref string __result)
    {
        try
        {
            string mine=AffectPayload(__0);
            if(Stats)
            {
                long n=Interlocked.Increment(ref AffectCalls);
                if(mine.Length>0)Interlocked.Increment(ref AffectNonEmpty);
                var original=OriginalAffectPayload;
                if(original!=null&&(n&31)==0)
                {
                    Interlocked.Increment(ref AffectShadow);
                    string theirs=original(__0);
                    if(!string.Equals(mine,theirs,StringComparison.Ordinal)){Interlocked.Increment(ref AffectMismatch);__result=theirs;return false;}
                }
            }
            __result=mine;return false;
        }
        catch{Interlocked.Increment(ref FaultCount);return true;}
    }
}
