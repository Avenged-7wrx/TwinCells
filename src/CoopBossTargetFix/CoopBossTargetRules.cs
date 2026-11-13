using System;
using System.Security.Cryptography;
using System.Threading;

// CoopBossTargetFix pure rules (no game, base-mod or GameProxy reference: compiled alone by the managed checks).
// Issue 9 of the 2026-11-12 playtest (PLAN-ALPHA2.md issue 9, unit U3): while the host is downed the Concierge
// (dc.en.mob.boss.Behemoth) keeps the downed host as its nemesis. The base mod's "clear" call setNemesisTarget(null) is a
// native no-op (Mob.hx:1680) and it refuses a non-Hero boss nemesis, so the boss never attacks the client. The part hands
// the nemesis to the surviving player while a player is downed and gives it back on revive (or when the survivor's avatar
// goes away). Every decision is made here from booleans read by CoopBossTargetPatch.
public static class CoopBossTargetRules
{
    public const string PartName="CoopBossTargetFix";
    public const string LogPrefix="[CoopBossTargetFix] ";
    // Part kill switch (scheme COOP_<PART>_DISABLE; OWNER-DECISIONS Q1: no rename in Alpha2). The pack row switch
    // COOP_PACK_BOSS_TARGET_DISABLE is added by integration (U15) with the row. It is named in parts.json only: the COOP_PACK_
    // namespace belongs to the pack host, and gate G11 refuses a COOP_PACK_ string literal in a part source.
    public const string KillSwitch="COOP_BOSS_TARGET_DISABLE";
    public const string HarmonyId="CoopBossTargetFix.Nemesis.20261112";
    // Allowlist (PLAN-ALPHA2.md issue 9 step 2: Behemoth only; Queen, TimeKeeper, Collector, Giant, KingsHand and HandBase
    // need their own bytecode audit first). Exact runtime proxy type, not a base type.
    public static readonly string[] AllowedBossTypes={"dc.en.mob.boss.Behemoth"};
    // The base mod's own survivor range for bosses (DownedRecovery.cs:18 HostBossSurvivorRetargetMaxDistancePx = 24*96).
    public const double SurvivorRangePx=24.0*96.0;

    public static bool SwitchSet(string value)=>value=="1";
    public static bool IsAllowlisted(string runtimeTypeFullName)
    {
        if(runtimeTypeFullName==null)return false;
        foreach(var t in AllowedBossTypes)if(string.Equals(t,runtimeTypeFullName,StringComparison.Ordinal))return true;
        return false;
    }

    // ---- pins ----
    // Release 0.5.1 DeadCellsMultiplayerMod.dll, the native boot image and the raw GameProxy accelerator (the symbolic
    // GameProxy pin is CoopBossTargetProxyCompatibility.FingerprintPin).
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string MobsSync="DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization";
    public const string Entry="DeadCellsMultiplayerMod.ModEntry";
    public const string Ghost="DeadCellsMultiplayerMod.GhostHero";
    public const string Lobby="DeadCellsMultiplayerMod.LobbySession";
    // IL bodies (release 0.5.1, IL bytes as GetILAsByteArray returns them): declaring type, method, IL length, SHA-256.
    // P = patched by this part; C = called by this part; R = relied on (the call sites and the base mod's own nemesis
    // writers whose behaviour the hand-over was checked against, PLAN-ALPHA2.md issue 9).
    public static readonly (string Type,string Method,int Length,string Sha,char Role)[] IlPins=
    {
        (MobsSync,"TryClearHostMobInvalidPlayerTargets",92,"94054F86EA198F62782920C0911EF376C7BC05A46C5767F91951BC1FF0B25718",'P'),
        (Ghost,"DisposeKingRuntime",239,"49C6BE7C735291C02FA65A5D6EC407B815F61DB0F4B943219F5C0D1B07745684",'P'),
        (Entry,"DisposeClientSlot",266,"E486CC7ED92F4FAC6692BCB14D151A05D657CD31188A7B4DDFF38E5549D06039",'P'),
        (Entry,"DisposeClientSlotForSubLevelTransition",312,"7C9B38A3662A04F7961D14EF0FD9C4E34B9B348F2488482EDD0DD2F73942554A",'P'),
        (MobsSync,"TryResolveDetectedHostCombatTarget",371,"26A645FBCDEE26F6004FC1F45223072602947AEBFA2E2D9380A0599009F243CC",'C'),
        (MobsSync,"ResolveClosestLivingHostCombatTarget",156,"C948B2350F09A01D255AC0B66AA1ABF65DDB1C23AC5C9364565FA4454F8088A8",'C'),
        (MobsSync,"HasLocalQueuedOrChargingSkill",76,"8C53CC715B6A2457CBEBE63135EA4EF1507689E5702914B2BB828E576D02AB46",'C'),
        (MobsSync,"IsKnownPlayerEntity",96,"E45AD7F5EF113F87059A62AA4D2697F479E654CCA72201D35ACCA87FF645BF9B",'C'),
        (MobsSync,"IsHardInvalidPlayerTargetEntity",87,"D1DEA35AD1F74EEA63B865D8973DC406729CE4DC84AC82191F2026D5F6E6AA7A",'C'),
        (MobsSync,"IsHost",20,"7B3A1D50BBFE9BB7A5C5BD21B2A63C574AB7D699B7C56C43E2DD3AC3C4EBEF4D",'C'),
        (Entry,"IsEntityDownedForCombat",141,"D65C5503AD109B3E12CD4BC66124DCF4363A1A6258AE9C4891C75A8123B6E635",'C'),
        (Entry,"HasAnyPlayerDownedForCombat",144,"3E97C3B172495D21F12B72B81F55DDA4883AA8AA43DCD46DD32F91F36DB5B339",'C'),
        (Lobby,"get_NetRef",6,"6DA5F23A4D13988DE21B0F637DD992C0DF2805337998BF11708590761A8CC2A7",'C'),
        (MobsSync,"TryMaintainHostBossSurvivorTarget",163,"5F6030F85BE32F17C8ED6D8AD81BAD54C1C0BA91B1C2EABB81C4B0A61E8DBCB2",'R'),
        (MobsSync,"TryRepairHostMobAfterPlayerCombatStateChange",182,"38C942B25B200FA5B80610D9711025F198896781A15667A17D528CB0F8ED10CD",'R'),
        (MobsSync,"Hook_Mob_setNemesisTarget",9,"030AAACBE287E7A5902C6B8FEC461A5A23CA8BC4342DF643C93231495E88ECF9",'R'),
        (MobsSync,"TrySetNemesisTargetExact",85,"D115771B4654F011DB78224372316EBDE248C1535B1BB78F16386E905555BA78",'R'),
        (MobsSync,"TryResolveSafeBossNemesisTarget",155,"C17CD688947BAA94E17CAD5A06ECA4FA0008E7378C733D57788C70DD35AC7A1C",'R'),
    };
    public static string Sha256(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
    public static bool PinMatches(byte[] il,int length,string sha)=>il!=null&&il.Length==length&&string.Equals(Sha256(il),sha,StringComparison.Ordinal);
    public static bool IsPinText(string sha)
    {
        if(sha==null||sha.Length!=64)return false;
        foreach(char c in sha)if(!(c>='0'&&c<='9'||c>='A'&&c<='F'))return false;
        return true;
    }

    // ---- decisions ----
    public enum Act{None,Defer,HandOver,Restore,Forget}
    public enum RecordOp{Create,Update,Drop}

    // What CoopBossTargetPatch read for one boss (every field false/absent when unknown).
    public struct View
    {
        public bool IsHost;            // MobsSynchronization.IsHost(LobbySession.NetRef)
        public bool Allowlisted;       // IsAllowlisted(boss runtime type)
        public bool MobAlive;          // !destroyed && life > 0
        public bool NemesisIsPlayer;   // base IsKnownPlayerEntity(nemesis)
        public bool NemesisInvalid;    // base IsHardInvalidPlayerTargetEntity(nemesis): downed, destroyed, dead, untargetable, off-level, corpse
        public bool NemesisGone;       // nemesis destroyed, or neither the local hero nor a current ModEntry.clients avatar
        public bool SkillBusy;         // base HasLocalQueuedOrChargingSkill(boss)
        public bool HasSurvivor;       // the base mod's survivor choice S exists (detected target, else closest living within 2304 px)
        public bool SurvivorIsNemesis; // S is the current nemesis
        public bool HasRecord;         // this part handed this boss's nemesis over and has not given it back
        public bool NemesisIsHandedTo; // the current nemesis is the player the record handed it to
        public bool AnyDowned;         // base HasAnyPlayerDownedForCombat()
        public bool OriginalAlive;     // recorded original nemesis: !destroyed && life > 0 (a downed player has life 1)
        public bool OriginalDowned;    // base IsEntityDownedForCombat(original)
        public bool OriginalSameLevel; // original's level is the boss's level
    }

    // One decision for one boss, from the postfix on TryClearHostMobInvalidPlayerTargets (every frame while a player is
    // downed, and once per tracked mob after each player combat-state change) and from the frame tick (records only).
    public static Act Decide(in View v)
    {
        if(!v.IsHost||!v.Allowlisted)return Act.None;
        if(!v.MobAlive)return v.HasRecord?Act.Forget:Act.None;
        bool originalUsable=v.HasRecord&&v.OriginalAlive&&v.OriginalSameLevel;
        if(v.NemesisIsPlayer&&v.NemesisInvalid)
        {
            // The player we handed the boss to has left (avatar destroyed / no longer a client): give it back to the
            // original even while that player is still downed, so the boss never keeps a destroyed avatar.
            if(v.HasRecord&&v.NemesisIsHandedTo&&v.NemesisGone&&originalUsable)return Act.Restore;
            // Never retarget a skill already queued or charging (the base mod's own rule, DownedRecovery.cs:158).
            if(v.SkillBusy)return Act.Defer;
            if(v.HasSurvivor&&!v.SurvivorIsNemesis)return Act.HandOver;
            return Act.None;
        }
        if(v.HasRecord)
        {
            // Someone else (vanilla, the base mod) chose another nemesis since: nothing of ours to undo.
            if(!v.NemesisIsHandedTo)return Act.Forget;
            // Revive: nobody is downed and the original player is back.
            if(!v.AnyDowned&&originalUsable&&!v.OriginalDowned)return Act.Restore;
        }
        return Act.None;
    }
    // The frame tick never hands over (that stays in the base mod's own target flow); it only restores or forgets.
    public static Act TickAct(Act decided)=>decided==Act.Restore||decided==Act.Forget?decided:Act.None;
    // After a successful hand-over to S.
    public static RecordOp AfterHandOver(bool hadRecord,bool survivorIsOriginal)=>!hadRecord?RecordOp.Create:survivorIsOriginal?RecordOp.Drop:RecordOp.Update;
    // A prefix on a base-mod method that is about to destroy the avatar K (GhostHero.DisposeKingRuntime,
    // ModEntry.DisposeClientSlot*): for a record handed to K.
    public static Act DisposeAct(bool recordHandedToIsK,bool mobAlive,bool nemesisIsK,bool originalUsable)
    {
        if(!recordHandedToIsK)return Act.None;
        if(!mobAlive)return Act.Forget;
        return nemesisIsK&&originalUsable?Act.Restore:Act.Forget;
    }
    // At most this many bosses carry a record (dual Behemoth in Boss Rush is two).
    public const int MaxRecords=8;
}

// Per-part fault gate (same thresholds as PartGate / OptFaultGate): trips at 8 consecutive or 64 total faults.
public sealed class CoopBossTargetFaultGate
{
    public const int ConsecutiveLimit=8;
    public const int TotalLimit=64;
    int _consecutive;long _total;
    public volatile bool Tripped;
    public long Faults=>Interlocked.Read(ref _total);
    public void Ok(){_consecutive=0;}
    // Returns true exactly once: on the fault that trips the gate.
    public bool Fail()
    {
        long n=Interlocked.Increment(ref _total);
        bool was=Tripped;
        if(++_consecutive>=ConsecutiveLimit||n>=TotalLimit)Tripped=true;
        return Tripped&&!was;
    }
}

// Rate limit for repeated log lines: at most one line per interval (Stopwatch ticks), counting the suppressed ones.
public sealed class CoopBossTargetLogLimiter
{
    readonly long _interval;long _next;long _suppressed;bool _any;
    public CoopBossTargetLogLimiter(long intervalTicks){_interval=intervalTicks;}
    // True when a line may be written now; suppressed returns how many were held back since the last line.
    public bool Allow(long nowTicks,out long suppressed)
    {
        if(!_any||nowTicks>=_next){_any=true;_next=nowTicks+_interval;suppressed=_suppressed;_suppressed=0;return true;}
        _suppressed++;suppressed=0;return false;
    }
}
