using System;
using System.Globalization;

// Pure decisions of the CoopComboFix part (Alpha2 unit U8, issues 11 and 18; PLAN-ALPHA2.md issue 11 and owner decision
// Q2). No game types: Test-Managed.ps1 compiles this file together with checks\ComboFixChecks.cs alone, and
// Test-Mutants.ps1 mutates it.
//
// Game facts these rules encode (Dead Cells v35 bytecode, hlboot A37869A3; scratchpad u8combo queries):
//  - The combo is the mutation P_DmgKill. Mob.onDirectHitFromHero (f@1076, Mob.hx:2680-2694) adds one stack of affect
//    132 to game.hero, resets every stack to the item's duration and shows ComboCount.setValue(n, 1 + k*n) with
//    n = countAffect(132) and k = prct + customScaling * getRelevantPerkTier("P_DmgKill"). setValue has no other caller
//    with n > 0 (callers of f@5356: f@1076 and the ComboCount constructor).
//  - Mob.onDamage calls onDirectHitFromHero only for tag 18 or (source is a Hero, no carrier, no tag 14, no tag 29)
//    (Mob.hx:2467-2468). DoT ticks (createFromDot f@6975: tags 7,8,9,5,12,29) and thrown hits never build combo.
//  - The damage multiplier: HeroAttackSource.computePerksBonusMul (f@18793) returns 1 + the sum of its perk terms; the
//    combo term (closure f@36025, HeroAttackSource.hx:108-112) is k * countAffect(132) of the attacking hero (parent),
//    and is 0 while the hero has no stack. computeExtraBonusMul (f@18792) skips the perks for tag 27. Every hit, DoT
//    ticks included (Entity.fixedUpdate f@950 -> createFromDot -> AttackUtils.hit -> updateDamages f@36019), goes
//    through it, so in single player the attacker's combo also multiplies his own DoT ticks.
//  - The base mod replays every client hit report on the host as createFromHeroAndHit(host hero, hint, null, mob)
//    (release IL 181279): a direct hit by the host hero, so it fed the host's combo, and the host's combo term
//    multiplied the client's damage. The client's hint is the attack's baseDmg (ReadAttackIntentDamage, IL 133616),
//    so the client's own combo was lost.
//
// Owner decision Q2: as in the real game - each player builds his own combo from his own direct hits, fire/poison/bleed
// build no combo for anyone, the client's hits never feed the host's combo, and the client's own combo multiplier
// ships in Alpha2.
//   Part A: on the replays (TryReplayIncomingSpecialHitReaction, TryApplyHostBossFinishingHit) and inside the base mod's
//           KingSkin weapon context, Mob.onDirectHitFromHero does not run (no stack, no HUD, no on-hit mutations of the
//           local hero, no kill-reward cooldowns, which the client's own kill alone no longer gives the host).
//   Part B: the client multiplies the damage hint of its own hero's attacks by its own 1 + c (c = its combo term), and
//           the host removes its own combo term from the perks multiplier of the replayed hit. Result on the host:
//           hint * (1 + c_client) * (the host's other perk terms, unchanged base-mod behaviour). Wire: a capability
//           exchange (CCMB1|HI / CCMB1|OK); the client scales only while the host answered "on" (rule 7).
public static class ComboFixRules
{
    public const string Part="CoopComboFix";
    public const string KillSwitch="COOP_COMBO_DISABLE";          // the whole part (pack row: COOP_PACK_COMBO_DISABLE)
    public const string PartBSwitch="COOP_COMBO_PARTB_DISABLE";   // Part B only (hint scaling, host term removal, wire)
    public const int ComboAffect=132;
    public const int NoPerkBonusTag=27;
    public const double MaxPerStack=1.0;     // sanity bound of k (CDB P_DmgKill: prct 0.02, customScaling 0.001 per tier)
    public const int MaxStacks=1000;
    public const double MaxFactor=4.0;       // cap of the client's hint factor (never more than 4x the base intent)

    public static bool SwitchSet(string value)=>value=="1";

    // ---- Part A: Mob.onDirectHitFromHero ----
    public enum Skip{None,Replay,BossFinish,King}
    public static Skip DirectHitVerdict(int replayDepth,int bossDepth,bool inKingContext)
    {
        if(replayDepth>0)return Skip.Replay;
        if(bossDepth>0)return Skip.BossFinish;
        if(inKingContext)return Skip.King;
        return Skip.None;
    }

    // ---- the per-stack multiplier k, read from the game's own HUD call ComboCount.setValue(n, 1 + k*n) ----
    public static bool TryPerStack(int stacks,double mult,out double perStack)
    {
        perStack=0;
        if(stacks<=0||stacks>MaxStacks||!double.IsFinite(mult))return false;
        double k=(mult-1)/stacks;   // a multiplier below 1 gives k < 0 and is refused below
        if(!double.IsFinite(k)||k<0||k>MaxPerStack)return false;
        perStack=k;return true;
    }
    // The combo term k*n. No stack: 0 (the closure skips). Stacks but k never seen: NaN (cannot be computed).
    public static double Term(int stacks,bool known,double perStack)
    {
        if(stacks<=0)return 0;
        if(!known||stacks>MaxStacks||!double.IsFinite(perStack)||perStack<0||perStack>MaxPerStack)return double.NaN;
        return perStack*stacks;
    }

    // ---- Part B, client: the damage hint of the local hero's own attack ----
    public enum HintVerdict{Scaled,PartOff,NotClient,Unsupported,NoHint,NotOwnHero,NoPerkBonus,NoStacks,PerStackUnknown}
    public static HintVerdict ClientHint(bool partOn,bool isClient,bool hostSupports,double hint,bool ownHero,bool noPerkBonusTag,
        int stacks,bool known,double perStack,out double factor)
    {
        factor=1;
        if(!partOn)return HintVerdict.PartOff;
        if(!isClient)return HintVerdict.NotClient;
        if(!hostSupports)return HintVerdict.Unsupported;
        if(!double.IsFinite(hint)||hint<=0)return HintVerdict.NoHint;
        if(!ownHero)return HintVerdict.NotOwnHero;
        if(noPerkBonusTag)return HintVerdict.NoPerkBonus;
        if(stacks<=0)return HintVerdict.NoStacks;
        double c=Term(stacks,known,perStack);
        if(double.IsNaN(c))return HintVerdict.PerStackUnknown;
        factor=Math.Min(MaxFactor,1+c);
        return HintVerdict.Scaled;
    }

    // ---- Part B, host: the perks multiplier of the replayed client hit, without the host's own combo term ----
    // Only the first perks computation of a replay frame for the replay's own target by the local hero is considered
    // (secondary attacks a replayed kill may cause are left alone); ConsumesFrame says whether that computation was it.
    public enum HostVerdict{Removed,PartOff,NotReplay,FrameDone,OtherTarget,NotLocalHero,NoStacks,PerStackUnknown,BadValue}
    public static HostVerdict HostPerks(bool partOn,bool inReplay,bool frameDone,bool sameTarget,bool parentIsLocalHero,
        double original,int stacks,bool known,double perStack,out double result)
    {
        result=original;
        if(!partOn)return HostVerdict.PartOff;
        if(!inReplay)return HostVerdict.NotReplay;
        if(frameDone)return HostVerdict.FrameDone;
        if(!sameTarget)return HostVerdict.OtherTarget;
        if(!parentIsLocalHero)return HostVerdict.NotLocalHero;
        if(stacks<=0)return HostVerdict.NoStacks;
        double c=Term(stacks,known,perStack);
        if(double.IsNaN(c))return HostVerdict.PerStackUnknown;
        if(!double.IsFinite(original))return HostVerdict.BadValue;
        result=Math.Max(0,original-c);
        return HostVerdict.Removed;
    }
    public static bool ConsumesFrame(HostVerdict v)=>v==HostVerdict.Removed||v==HostVerdict.NoStacks||v==HostVerdict.PerStackUnknown||v==HostVerdict.BadValue;

    // ---- wire: capability exchange (one '\n'-terminated ASCII line each) ----
    //   client -> host : CCMB1|HI|1|<nonce>          once the client's WELCOME was accepted and its ID applied; resent
    //   host -> client : CCMB1|OK|1|<nonce>|on|off   "on" while the host's Part A and Part B are live
    // No handshake token: a peer that does not strip a HELLO/WELCOME token refuses the connection (LobbyColorRules.cs:11-13).
    // A peer without this part drops CCMB1 lines silently (NetNode.HandleLine: no tag matches, TryParsePositionLine fails).
    public const string Tag="CCMB1|";
    public const string Version="1";
    public const int MaxLine=64;
    public enum Kind{None,Hi,Answer}
    public static bool ValidNonce(string nonce)
    {
        if(nonce==null||nonce.Length!=32)return false;
        foreach(char c in nonce)if(!(c>='0'&&c<='9'||c>='a'&&c<='f'))return false;
        return true;
    }
    public static string Hi(string nonce)=>ValidNonce(nonce)?Tag+"HI|"+Version+"|"+nonce+"\n":throw new ArgumentException("bad nonce");
    public static string Answer(string nonce,bool on)=>ValidNonce(nonce)?Tag+"OK|"+Version+"|"+nonce+"|"+(on?"on":"off")+"\n":throw new ArgumentException("bad nonce");
    public static bool TryParse(string line,out Kind kind,out string nonce,out bool on)
    {
        kind=Kind.None;nonce=null;on=false;
        if(line==null||line.Length>MaxLine+2||!line.StartsWith(Tag,StringComparison.Ordinal))return false;
        var body=line;
        if(body.EndsWith("\n",StringComparison.Ordinal))body=body.Substring(0,body.Length-1);
        if(body.EndsWith("\r",StringComparison.Ordinal))body=body.Substring(0,body.Length-1);
        // Every field below has a fixed alphabet and length, so a line with an embedded CR/LF (or longer than MaxLine,
        // a cheap pre-filter above) can never parse.
        var f=body.Split('|');
        if(f.Length==4&&f[0]=="CCMB1"&&f[1]=="HI"&&f[2]==Version&&ValidNonce(f[3])){kind=Kind.Hi;nonce=f[3];return true;}
        if(f.Length==5&&f[0]=="CCMB1"&&f[1]=="OK"&&f[2]==Version&&ValidNonce(f[3])&&(f[4]=="on"||f[4]=="off")){kind=Kind.Answer;nonce=f[3];on=f[4]=="on";return true;}
        return false;
    }

    // Client side of the exchange, per accepted WELCOME (one nonce per connection epoch).
    public const long HiRetryMs=2000,HiKeepAliveMs=10000,SupportFreshMs=30000;
    public const int HiMaxUnanswered=30;
    public sealed class ClientLink
    {
        public readonly string Nonce;
        public long LastHi=long.MinValue,AnswerAt=long.MinValue;
        public int Unanswered;
        public bool Answered,On;
        public ClientLink(string nonce){if(!ValidNonce(nonce))throw new ArgumentException("bad nonce");Nonce=nonce;}
        // First HI at once; then every 2 s until answered (at most 30 times); then a keep-alive every 10 s.
        public bool HiDue(long now)
        {
            if(LastHi==long.MinValue)return true;
            if(now<LastHi)return false;
            if(!Answered)return Unanswered<HiMaxUnanswered&&now-LastHi>=HiRetryMs;
            return now-LastHi>=HiKeepAliveMs;
        }
        public void SentHi(long now){LastHi=now;if(!Answered)Unanswered++;}
        // An answer counts only for this epoch's nonce. Returns true when it was accepted.
        public bool Accept(string nonce,bool on,long now)
        {
            if(!string.Equals(nonce,Nonce,StringComparison.Ordinal))return false;
            Answered=true;Unanswered=0;On=on;AnswerAt=now;return true;
        }
        // The host's last answer said "on" and is at most 30 s old (it is refreshed by the keep-alive every 10 s).
        public bool Supported(long now)=>Answered&&On&&AnswerAt!=long.MinValue&&now>=AnswerAt&&now-AnswerAt<=SupportFreshMs;
        public bool GaveUp=>!Answered&&Unanswered>=HiMaxUnanswered;
    }

    // Host side: answers per one-second window (a well-behaved client sends at most one HI per 2 s).
    public const int MaxAnswersPerSecond=4;
    public static bool AnswerAllowed(long now,ref long windowStart,ref int inWindow)
    {
        if(windowStart==long.MinValue||now<windowStart||now-windowStart>=1000){windowStart=now;inWindow=0;}
        if(inWindow>=MaxAnswersPerSecond)return false;
        inWindow++;return true;
    }

    // ---- fault gate (OptFaultGate thresholds: 8 consecutive or 64 in total switch the part off for the session) ----
    public const int MaxConsecutiveFaults=8,MaxTotalFaults=64;
    public static bool ShouldTrip(int consecutive,long total)=>consecutive>=MaxConsecutiveFaults||total>=MaxTotalFaults;

    // ---- rate-limited logging ----
    // Event lines: the 1st, 10th, 100th, 1000th ... occurrence of a kind. Stats: at most one line per 60 s, only when a
    // counter changed.
    public static bool LogOccurrence(long n)
    {
        if(n<=0)return false;
        while(n%10==0)n/=10;
        return n==1;
    }
    public const long StatsPeriodMs=60000;
    public static bool StatsDue(long now,long last,bool changed)=>changed&&(last==long.MinValue||now<last||now-last>=StatsPeriodMs);

    public static string Num(double v)=>v.ToString("0.######",CultureInfo.InvariantCulture);
}
