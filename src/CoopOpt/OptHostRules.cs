using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

// Pure decisions of the host per-enemy fast paths of CoopOpt part OptHost (no game, mod, framework or Harmony types).
// Compiled into the add-on and, together with OptHostChecks.cs only, by Test-Managed.ps1 -Only OptHost.
// Every rule here is the decision a fast path takes; the checks compare each one with a literal transcription of the
// release IL / HashLink bytecode it replaces (release DLL 0.5.1 SHA 378BE6B0..., hlboot A37869A3...).
public static class OptHostRules
{
    // ---- shadow sampling: first WarmShadows decided calls, then every ShadowPeriod-th; every call in QA mode ----
    public const long WarmShadows=4096;
    public const long ShadowPeriod=256;
    public static bool ShouldShadow(long n,bool all)=>n>=1&&(all||n<=WarmShadows||n%ShadowPeriod==0);
    public static bool ShadowAllMode(string value)=>value=="all";
    public static bool SwitchSet(string value)=>value=="1";
    public static string ItemSwitch(string item)=>"COOP_OPT_HOST_"+item+"_DISABLE";

    // ---- item life cycle ----
    public const int StateWaiting=0,StateOn=1,StateOff=2;
    // A waiting item tries to arm on its first call and then on every ArmRetryPeriod-th call (game thread).
    public const long ArmRetryPeriod=512;
    public static bool ArmAttemptDue(long waitingCalls)=>waitingCalls==1||(waitingCalls>1&&waitingCalls%ArmRetryPeriod==0);
    public static string StateName(int state)=>state==StateOn?"on":state==StateOff?"off":"waiting";

    // ---- A1: raw reads of Entity._level / Entity._team / Level.teamMob ----
    // An offset taken from the generated proxy's own field cache is used only when the cache is filled, the field is
    // an object pointer, and the offset is a positive, pointer-aligned position inside a plausible object.
    public const long MaxFieldOffset=1<<16;
    public static bool OffsetUsable(bool hasCache,long offset,bool objectKind,int pointerSize)
        =>hasCache&&objectKind&&pointerSize>0&&offset>=pointerSize&&offset<MaxFieldOffset&&offset%pointerSize==0;
    // IsMobHostileToPlayers (IL 119882): level != null && team != null && team == level.teamMob (proxy identity = pointer).
    public static bool Hostile(long level,long team,long levelTeamMob)=>level!=0&&team!=0&&team==levelTeamMob;
    // The teamMob slot is read only when both pointers are present (the original never reads it otherwise).
    public static bool NeedsTeamMob(long level,long team)=>level!=0&&team!=0;
    // A slot counts as confirmed only once it held a real object pointer equal to the proxy getter's object (a null slot
    // next to a null getter proves nothing about the offset).
    public static bool SlotConfirms(long slot,long getter)=>slot!=0&&slot==getter;
    // IsSyncMob (IL 142564) after EnableMobsSync == true and mob != null, inside its try region.
    public enum SyncVerdict{False,True,ByType}
    public static SyncVerdict SyncMob(bool destroyed,long level,bool hostile)
        =>destroyed||level==0?SyncVerdict.False:hostile?SyncVerdict.True:SyncVerdict.ByType;
    // The value of a verdict that is not ByType (ByType is IsSyncMobByType's answer).
    public static bool SyncValue(SyncVerdict verdict)=>verdict==SyncVerdict.True;

    // ---- A3: near-box pre-check in TryActivateHostMobForRemotePlayer ----
    public static bool SkipActivation(bool near)=>!near;
    // IL 144297: the near test starts at IL_0047; the calls before it are what a skipped call no longer runs.
    public const int ActivationNearTestAt=0x47;
    public const int NearUnseen=0,NearFalse=1,NearTrue=2;
    public static int NearSeen(bool result)=>result?NearTrue:NearFalse;
    // A skipped call is confirmed by the original when it returned false and did not find the mob near.
    public static bool ActivationShadowAgrees(bool originalResult,int nearSeen)=>!originalResult&&nearSeen!=NearTrue;
    // After WarmShadows shadowed calls in which the original's near test was never observed the check cannot work.
    public static bool ActivationUnverifiable(long shadows,long observed)=>shadows>=WarmShadows&&observed==0;

    // ---- T1: owner prefix region of IsEntityOnCurrentCombatIdentity ----
    // CoopSyncFixes.MatchTrackedNativeLevel decides only when level, currentLevel and s_levelIdentityReady are set.
    public static bool IdentityDecided(bool levelPresent,bool currentPresent,bool ready)=>levelPresent&&currentPresent&&ready;
    public static bool IdentityMatch(long level,long current)=>level==current;

    // ---- T2: the shell re-check at IL_0016 is dominated by a false result of the same predicate ----
    public static bool ShellRecheckAgrees(bool real)=>!real;

    // ---- T3 (= design D1): hero canBeHitBy without the two hook dispatches ----
    public static bool ReceiverEligible(bool exactHeroType,bool isLocalHero)=>exactHeroType&&isLocalHero;
    // Each hook manager must hold exactly the one registered handler and still route native calls through its own
    // entry delegate (no processor of a Harmony patch on the proxy method, no direct route around the handlers).
    public static bool HandlerChainIntact(int handlers,bool sameHandler,bool sameEntry,long redirect)
        =>handlers==1&&sameHandler&&sameEntry&&redirect==0;
    public static string AdaptName(int findex)=>"hook_adapt_"+findex.ToString(CultureInfo.InvariantCulture);
    // HL f@664 pr.Game.hasCinematic: (curCine != null && !curCine.destroyed) || ModalPopUp.ME != null
    public static bool GameHasCinematic(bool cinePresent,bool cineDestroyed,bool modalPresent)=>(cinePresent&&!cineDestroyed)||modalPresent;
    // HL f@2050 en.Hero.canBeHitBy: f@921 (= this.canBeHit()) && !game.hasCinematic() && !game.paused
    public static bool HeroCanBeHit(bool canBeHit,bool hasCinematic,bool paused)=>canBeHit&&!hasCinematic&&!paused;
    // Shadow verdict of one call: both routes threw, or neither threw and both gave the same answer.
    public static bool OutcomesAgree(bool fastThrew,bool fast,bool realThrew,bool real)=>fastThrew==realThrew&&(fastThrew||fast==real);

    // ---- patches on targets ----
    // Every patch on a fast-path target must be ours (a foreign prefix/postfix could observe or change what we skip).
    public static bool OnlyOwner(IEnumerable<string> owners,string own)
    {
        if(owners==null)return true;
        foreach(var o in owners)if(o!=own)return false;
        return true;
    }
    public const string OwnerHarmonyId="local.deadcells.syncfixes.1";
    // T1 replicates exactly one owner prefix on DoesLevelMatchCurrentIdentityLocked and nothing else may be there.
    public static bool OwnerPrefixExact(int prefixes,string owner,string method,string declaringType,int others)
        =>prefixes==1&&owner==OwnerHarmonyId&&method=="MatchTrackedNativeLevel"&&declaringType=="CoopSyncFixes"&&others==0;
    // The owner's postfix on ComputeLevelIdentityToken (a T2-skipped call can reach it): allowed only as that one patch.
    public static bool OwnerPostfixExact(int postfixes,string owner,string method,string declaringType,int others)
        =>others==0&&postfixes==1&&owner==OwnerHarmonyId&&declaringType=="CoopSyncFixes"&&method=="SaltBranchIdentity";
    // Known patches on the framework's hook dispatcher HashlinkHookManager.HookEntry, which T3's fast route does not enter:
    // CoopLoadFix's chain cache (the same dispatch from a cached chain) and CoopNetMeter's QA meter (counts dispatches).
    // Any other patch there refuses T3.
    public const string LoadFixChainOwner="CoopLoadFix.ChainCache.20261110",NetMeterOwner="CoopNetMeter.QA";
    public static bool HookEntryPatchAllowed(bool prefix,bool postfix,string owner,string declaringType,string method)
        =>prefix&&owner==LoadFixChainOwner&&declaringType=="ChainCache"&&method=="BeforeHookEntry"
        ||owner==NetMeterOwner&&declaringType=="CoopNetMeter"&&(prefix&&method=="BeforeHookEntry"||postfix&&method=="AfterHookEntry");
    // Upper bound of a guard set (the methods a fast path must see unpatched); a larger closure is a binding error.
    public const int MaxGuarded=4096;
    // A transpiler replaces a call only when the body holds it exactly once.
    public static int SingleIndex(IReadOnlyList<bool> matches)
    {
        int at=-1;
        for(int i=0;i<matches.Count;i++)if(matches[i]){if(at>=0)return -1;at=i;}
        return at;
    }

    // ---- IL shapes of the generated proxy members the fast paths rely on ----
    public const byte OpLdArg0=0x02,OpLdStr=0x72,OpLdSFldA=0x7F,OpCall=0x28,OpRet=0x2A,OpLdcI4=0x20;
    // ldarg.0; ldstr name; ldsflda cache; call HaxeProxyHelper.Get(Value)FieldById<T>; ret
    public static bool FieldGetterShape(byte[] il)=>il!=null&&il.Length==17&&il[0]==OpLdArg0&&il[1]==OpLdStr&&il[6]==OpLdSFldA&&il[11]==OpCall&&il[16]==OpRet;
    public const int GetterStringAt=2,GetterFieldAt=7,GetterHelperAt=12;
    // ldc.i4 global; ldsflda cache; call HaxeProxyHelper.GetGlobal; ret
    public static bool GlobalGetterShape(byte[] il,int global)=>il!=null&&il.Length==16&&il[0]==OpLdcI4&&Int32At(il,1)==global&&il[5]==OpLdSFldA&&il[10]==OpCall&&il[15]==OpRet;
    // A proxy method that starts with ldc.i4 <findex> (HaxeProxyHelper.GetCallInfoById(findex) + calli) calls that function directly.
    public static bool CallsFunction(byte[] il,int findex)=>il!=null&&il.Length>=5&&il[0]==OpLdcI4&&Int32At(il,1)==findex;
    public static int Int32At(byte[] il,int at)=>il[at]|(il[at+1]<<8)|(il[at+2]<<16)|(il[at+3]<<24);

    // ---- IL call sites (the guard closures) ----
    // Operand kinds of every opcode, from the runtime's own OpCodes table.
    static readonly Dictionary<short,OperandType> Operands=BuildOperands();
    static Dictionary<short,OperandType> BuildOperands()
    {
        var d=new Dictionary<short,OperandType>();
        foreach(var f in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static))
            if(f.GetValue(null) is OpCode op&&op.Size>0)d[op.Value]=op.OperandType;
        return d;
    }
    // Operand bytes of one kind (InlineSwitch: the count only; its targets are added by CallSites).
    public static int OperandSize(OperandType kind)=>kind switch
    {
        OperandType.InlineNone=>0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
        OperandType.InlineVar=>2,
        OperandType.InlineI8 or OperandType.InlineR=>8,
        _=>4,
    };
    public const short OpJmp=0x27,OpCallvirt=0x6F,OpNewobj=0x73,OpLdftn=unchecked((short)0xFE06),OpLdvirtftn=unchecked((short)0xFE07);
    // Instructions that name a method a body can run: call, callvirt, newobj, jmp, ldftn, ldvirtftn (calli names a signature).
    public static bool IsCallSite(short op)=>op==OpCall||op==OpCallvirt||op==OpNewobj||op==OpJmp||op==OpLdftn||op==OpLdvirtftn;
    // Offsets and method tokens of every call site of an IL body; null when the body cannot be decoded.
    public static List<(int Offset,int Token)> CallSites(byte[] il)
    {
        if(il==null)return null;
        var sites=new List<(int,int)>();int i=0;
        while(i<il.Length)
        {
            int at=i;short op=il[i++];
            if(op==0xFE){if(i>=il.Length)return null;op=unchecked((short)(0xFE00|il[i++]));}
            if(!Operands.TryGetValue(op,out var kind))return null;
            long size=OperandSize(kind);
            if(kind==OperandType.InlineSwitch){if(i+4>il.Length)return null;size+=4L*(uint)Int32At(il,i);}
            if(i+size>il.Length)return null;
            if(IsCallSite(op))sites.Add((at,Int32At(il,i)));
            i+=(int)size;
        }
        return sites;
    }

    // ---- pins ----
    public static string Sha256(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
    public static bool PinMatches(byte[] il,int length,string sha)=>il!=null&&il.Length==length&&string.Equals(Sha256(il),sha,StringComparison.Ordinal);
    public static bool IsPinText(string sha)
    {
        if(sha==null||sha.Length!=64)return false;
        foreach(char c in sha)if(!(c>='0'&&c<='9'||c>='A'&&c<='F'))return false;
        return true;
    }
    // IL bodies (release 0.5.1) the arguments of the fast paths rest on: name, IL length, SHA-256 of the IL bytes.
    public static readonly (string Name,int Length,string Sha)[] ModPins=
    {
        ("IsMobHostileToPlayers",49,"4271A32C899B5E37A2C76EB7C24E1CF965EEBD02A09C0087CFC0EAA7E4E0A42B"),
        ("IsSyncMob",63,"44E9CFA7DFECC432A5ED21E780C8FBF561F1E699FE68F4005B860082426B7CFD"),
        ("IsSyncMobByType",48,"4FE11774F507F3F07123AF688573FDEB3822E7E73DC4DC1C6FF254DB5468C105"),
        ("TryActivateHostMobForRemotePlayer",112,"9E4D45499AA6F60A8375C4DEE005DE63C0E4AA069D2E2EC32AE3BA925A5B97E4"),
        ("IsMobNearAnyRemotePlayer",54,"FE1429D8AD2C198DF66E180530F026CC35B31C1A1257054102BCD7133117D18A"),
        ("IsWithinRangeBox",82,"E9B48A255A6836ED0797046608B1094FCD532D7A33A05DB3F5C25480AE4D029C"),
        ("GetWorldX",25,"F7CFDE4723B14D46C632BF244A771055384F85713471CEF355CD63E71A48E0DB"),
        ("GetWorldY",25,"DAE46C866705124676EF9BA87913C296A9371689D53C0E3DFF64ABA5F83CC5FC"),
        ("IsEntityOnCurrentCombatIdentity",53,"1BE2070CCCFDDC378ED54522739497A84A2E0DB3BA6DEC69DD79F73D179BD9D3"),
        ("DoesLevelMatchCurrentIdentityLocked",105,"B4FE24495AA464AD47FFAED2DDEE170288CCD157386272006D0A3A77041F36B0"),
        ("IsAcquirablePlayerCombatTargetForMob",156,"A5BF243210FFFC74316B29AFA51D3F622A3B630430EEF400A5CD87996065072F"),
        ("IsHardInvalidPlayerTargetEntity",87,"D1DEA35AD1F74EEA63B865D8973DC406729CE4DC84AC82191F2026D5F6E6AA7A"),
        ("IsPreservablePlayerCombatTargetForMob",43,"9F0B2C8BA5FBF83AC757A666A87289ED7E49727ED6C828BF42706A0AEEF01BC2"),
        ("IsPreservablePlayerCombatTargetEntity",37,"20F037AD1A145B67554B9F153F596B3898CA74E6F81E9D2C900BFC3FF79EDDE5"),
        ("IsRemotePlayerCombatShell",78,"940FF8127A89D11B996F7B87EC67234DC7C3BB0A9764E1492D0444BA53E0F2F1"),
        ("IsCorpseLikeCombatTargetEntity",20,"686612D39BFD0CFE7DEA4F638326ECA456A808A3C3AA7CE535833592ED8AB3A7"),
        ("IsKnownPlayerEntity",96,"E45AD7F5EF113F87059A62AA4D2697F479E654CCA72201D35ACCA87FF645BF9B"),
    };
    public static readonly (string Name,int Length,string Sha)[] EntryPins=
    {
        ("Hook_Hero_canBeHitBy",70,"F066403FA5AF9E1138BC335D6BDD10BBF7F9B0BAD29459D2C61D29DCEAFE0333"),
        ("Hook_Game_hasCinematic",29,"AAA76A4D88A3949B1810F614EE20EDCB7D0D02B0EEA42FF5817AD9EE9244E43D"),
        ("IsModFakeDeathCine",57,"FBC88FC158CDDC72F5F59AFE91ACA54F3463DAF88BCF4B3EDD24AD39C3B1DEFB"),
        ("IsEntityDownedForCombat",141,"D65C5503AD109B3E12CD4BC66124DCF4363A1A6258AE9C4891C75A8123B6E635"),
    };
    // CoopSyncFixes build-62842b60 (installed DLL FFF00CA3...): the owner prefix T1 replicates and the helper it reads through.
    public const string OwnerModulePin="FFF00CA3F9297B00EBD7688A9E4D223FD2A14F2D0EC8DFE34801F8490C7342E9";
    public static readonly (string Name,int Length,string Sha)[] OwnerPins=
    {
        ("MatchTrackedNativeLevel",74,"1FFCFAEC28AE4A113C1E03377B6920CF085BF767AE61AC17D0CDD605E428DADD"),
        ("Field",23,"621D8C1363F28CC7325BF76BFFA8689923DE1FE14819FD9F344A82B41BB0DE23"),
        ("SaltBranchIdentity",480,"19E336E99277D2ACF62D4DE4DF210803E657167F6136E144270355BE71659E96"),
    };

    // ---- counters for the stats lines ----
    // Written by the game thread only, read by the stats timer: each line shows the change since the previous line.
    public sealed class CounterSet
    {
        public readonly string[] Names;public readonly long[] V;readonly long[] _last;
        public CounterSet(params string[] names){Names=names;V=new long[names.Length];_last=new long[names.Length];}
        public long Total(int i)=>System.Threading.Volatile.Read(ref V[i]);
        public string Deltas()
        {
            var sb=new StringBuilder();
            for(int i=0;i<Names.Length;i++)
            {
                long now=System.Threading.Volatile.Read(ref V[i]);
                if(sb.Length>0)sb.Append(' ');
                sb.Append(Names[i]).Append('=').Append((now-_last[i]).ToString(CultureInfo.InvariantCulture));
                _last[i]=now;
            }
            return sb.ToString();
        }
    }
    public static double PerCall(long total,long calls,double scale)=>calls>0?total*scale/calls:0;
    public const int StatsPeriodSeconds=5,VerifyPeriodSeconds=2,QuietHintSeconds=180;
    public static bool Due(long tick,int period)=>tick>0&&tick%period==0;
}
