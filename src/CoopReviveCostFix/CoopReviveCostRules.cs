using System;
using System.Collections.Generic;

// Pure rules of part CoopReviveCostFix (Alpha2 unit U6, PLAN-ALPHA2.md issue 5, owner decision OD1): no game, base-mod or
// GameProxy types, so the managed checks compile this file on its own (checks\CoopReviveCostChecks.cs).
//
// What the release base mod 0.5.1 (378BE6B0) already does (release IL dump):
//   ProcessReviveHold (IL:25004-25102): after a 0.7 s hold on the nearest downed partner it calls TryConsumeOneFlask(me)
//   (IL:25080) and, only when that returns true, NetNode.SendPlayerReviveRequest(target) (IL:25088); with 0 charges the
//   hold silently resets. TryConsumeOneFlask (IL:25264-25338) takes exactly one Heal charge (set_healings(n-1)).
// The gaps this part closes:
//   1. no hint with 0 charges: ShowReviveHintFor (IL:22772) always writes "Hold to revive." (IL:22799);
//   2. the charge is paid BEFORE the downed side accepts the request, so a rejected (IsReviverPositionValidForLocalCorpse,
//      CSF RequireLivingReviver, homunculus check) or lost request burns it, and holding again pays again;
//   3. a request that is certain to be dropped (SendPlayerReviveRequest returns silently without a connection,
//      IL:16758-16769) is still paid for;
//   4. nothing logs a payment.
// The latch: one payment per downed EPISODE of a partner. An episode is one RemoteDownedState object in ModEntry._remoteDowned:
// ConsumeRemoteDownedStates (IL:23487) creates a new object whenever the partner reports "downed" while it has no entry
// (IL:23578) and removes the entry when the partner reports "up" (IL:23540); PruneRemoteDownedStates (IL:23892) and
// ResetFakeDeathState (IL:25670) also remove entries. So the object identity changes exactly when an episode ends, even
// when "up" and "downed again" arrive in one batch, without hooking the 14-byte (inlining-prone) TryConsumePlayerDownStates.

// Outcome of the TryConsumeOneFlask prefix.
public enum CoopReviveCostDecision
{
    Original,                 // run the base method unchanged (it pays exactly one charge, or refuses with 0 charges)
    FreeResend,               // the partner's current episode is already paid: return true without a charge, the request is resent
    NoRequestNoConnection,    // SendPlayerReviveRequest would drop the request (no connection / no own id): no charge, no request
    NoRequestOtherLevel,      // the partner's downed record names another level: the downed side refuses it for sure
    NotOurs,                  // another prefix already skipped the original: leave everything as it is
}

// Label written over the downed partner by the ShowReviveHintFor prefix.
public enum CoopReviveCostHint
{
    Original,                 // run the base method ("Hold to revive.")
    NeedCharge,               // the reviver has no flask charge and this episode is not paid yet: our hint
}

public static class CoopReviveCostRules
{
    public const string PartName="CoopReviveCostFix";
    public const string LogPrefix="[CoopReviveCostFix]";
    public const string Switch="COOP_REVIVE_COST_DISABLE";
    // The pack row switch is the pack host's alone (parts.json row); a COOP_PACK_ string literal here fails pack gate G11.
    public const string HarmonyId="CoopReviveCostFix.ReviveCost.20261112";
    // Fault breaker: the part switches itself off (all patches pass through) after this many faults in a row or in total.
    public const int MaxConsecutiveFaults=3;
    public const int MaxTotalFaults=16;
    // Rate limits of the event log lines (seconds).
    public const double HintLogSeconds=10;
    public const double RefusalLogSeconds=5;
    public const double ResendLogSeconds=5;
    // System message shown to the reviver when a charge is paid (MultiplayerUI.PushSystemMessage lifetime / fade).
    public const double MessageSeconds=5;
    public const double MessageFadeSeconds=1.5;

    // Decision of the TryConsumeOneFlask prefix. runOriginal: Harmony __runOriginal (false when an earlier prefix skipped the
    // base method). targetKnown: the hold target (ModEntry._reviveHoldTargetId) has a RemoteDownedState right now.
    // connected/localNetId: NetNode.HasAnyConnection() and NetNode.id, the two tests SendPlayerReviveRequest makes before
    // it sends anything. localLevel: ModEntry.GetCurrentLevelId(); targetLevel: RemoteDownedState.LevelId. latched: the
    // ledger holds a payment for this target's current episode.
    public static CoopReviveCostDecision Decide(bool runOriginal,int targetId,bool targetKnown,bool connected,int localNetId,string localLevel,string targetLevel,bool latched)
    {
        if(!runOriginal)return CoopReviveCostDecision.NotOurs;
        if(targetId<=0||!targetKnown)return CoopReviveCostDecision.Original;
        if(!connected||localNetId<=0)return CoopReviveCostDecision.NoRequestNoConnection;
        if(LevelsDiffer(localLevel,targetLevel))return CoopReviveCostDecision.NoRequestOtherLevel;
        return latched?CoopReviveCostDecision.FreeResend:CoopReviveCostDecision.Original;
    }
    // Both level ids known and different. The downed side compares OrdinalIgnoreCase (base IL:23859) or Ordinal (CSF
    // RequireLivingReviver); only a difference under both counts as certain. Unknown (empty) ids are never certain.
    public static bool LevelsDiffer(string localLevel,string targetLevel)
    {
        if(string.IsNullOrWhiteSpace(localLevel)||string.IsNullOrWhiteSpace(targetLevel))return false;
        return !string.Equals(localLevel.Trim(),targetLevel.Trim(),StringComparison.OrdinalIgnoreCase);
    }
    // Whether the prefix returns false (skips the base method), and the result it then reports.
    public static bool SkipsOriginal(CoopReviveCostDecision d)=>d==CoopReviveCostDecision.FreeResend||d==CoopReviveCostDecision.NoRequestNoConnection||d==CoopReviveCostDecision.NoRequestOtherLevel;
    public static bool ResultWhenSkipped(CoopReviveCostDecision d)=>d==CoopReviveCostDecision.FreeResend;

    // The base method paid: it returned true and the charge count went down. Anything else is no payment (and no latch).
    public static bool IsPayment(bool result,bool knownBefore,int before,bool knownAfter,int after)=>
        result&&knownBefore&&knownAfter&&before>0&&after>=0&&after<before;
    // The base method refused for lack of a charge (the "impossible with 0 charges" case of OD1).
    public static bool IsNoChargeRefusal(bool result,bool knownBefore,int before)=>!result&&knownBefore&&before<=0;

    // Label rule of the ShowReviveHintFor prefix: our hint only when the charge count is known, there is no charge, and the
    // partner's current episode is not paid yet (a paid episode is revived by a free resend, so "Hold to revive." is right).
    public static CoopReviveCostHint ChooseHint(bool chargesKnown,int charges,bool latched)=>
        chargesKnown&&charges<=0&&!latched?CoopReviveCostHint.NeedCharge:CoopReviveCostHint.Original;

    // Texts. Game language ids: en fr de es-es es pl it ru pt tr zh ko zh-tw ja, workshop ids "<code>:<item>" (CoopLobby
    // LobbyText). Our own words exist in en and ru; every other language uses en. Only glyphs of the game fonts.
    public static string NormalizeLang(string lang)
    {
        if(string.IsNullOrWhiteSpace(lang))return "en";
        var s=lang.Trim();int colon=s.IndexOf(':');if(colon>0)s=s.Substring(0,colon);
        return s.ToLowerInvariant();
    }
    public static bool IsRussian(string lang)=>NormalizeLang(lang)=="ru";
    public static string HintText(string lang)=>IsRussian(lang)?"Нужен заряд фляги.":"A flask charge is needed.";
    public static string PaidText(string lang)=>IsRussian(lang)?"Воскрешение: потрачен 1 заряд фляги.":"Revive: 1 flask charge used.";

    public static bool ShouldTrip(int consecutive,int total)=>consecutive>=MaxConsecutiveFaults||total>=MaxTotalFaults;

    // IL body pins (SHA-256 of MethodBody.GetILAsByteArray, System.Reflection.Metadata dump of the pinned base DLL 378BE6B0;
    // lengths equal the release IL dump headers). Patched: the first three. The rest are the methods the patches rely on or
    // call (the hold pipeline, target choice, hint callers, connection/level reads, label and message sinks).
    public static readonly CoopReviveCostIlPin[] IlPins=
    {
        new("DeadCellsMultiplayerMod.ModEntry","ShowReviveHintFor",114,"E334CB628F9271B81CE635F4F015F11A39E229B7F51050E0056A64F74DD3397B"),
        new("DeadCellsMultiplayerMod.ModEntry","TryConsumeOneFlask",144,"6600C2DD9B21ED42D7BF8749F22B261CAB3CED105EBFE1DE45914BFF412AC397"),
        new("DeadCellsMultiplayerMod.ModEntry","ConsumeRemoteDownedStates",561,"A08E0E856171314203A3DB7927CBCC697A6D2DB6FD0A1CD31335889053AEE100"),
        new("DeadCellsMultiplayerMod.ModEntry","ProcessReviveHold",256,"84346849843A6D490CC0A6620F1E8972A8CEA5B922C1B79456E9F63AA22DACD9"),
        new("DeadCellsMultiplayerMod.ModEntry","FindNearestReviveTarget",361,"0905A6AB51495DF86BB4C1A75BA16F96450D3FED23AFD0F2B34B170DECC22D42"),
        new("DeadCellsMultiplayerMod.ModEntry","UpdateReviveHintsByProximity",57,"1E35795991CEF13BB3FA385BDAB14B316CF34B0047A698691FA145D7EF72CA67"),
        new("DeadCellsMultiplayerMod.ModEntry","ClearReviveHints",83,"AA8CEBCFE4B1DEF44B65BF622526541EF2BDFE1DD13DDA03C7E8F290E34B01F2"),
        new("DeadCellsMultiplayerMod.ModEntry","GetCurrentLevelId",109,"86AC59A43CB2E12E30B119D988FF1E4E3633D895468DCB525A5FCBCFFEAE27DE"),
        new("DeadCellsMultiplayerMod.ModEntry","UpdateFakeDeathFlow",174,"EC0A405F0EAD42DC32DCD5AEB46A4AF73431759BB05714454A0B80C61DFBD85E"),
        new("NetNode","HasAnyConnection",155,"CA858929AA1B0316EE5D9AE1A30851505F6E09237358100C806F32F5B99BF1CB"),
        new("NetNode","get_id",7,"63E05A7D58E743195298B430389DEB04CD66DA4E2794654EA6C89F2675798D9C"),
        new("NetNode","SendPlayerReviveRequest",53,"FB32535F3A9FFEA9ACFA2993735423DC011801261D34FFD40BA846E80C03A9F3"),
        new("DeadCellsMultiplayerMod.RemoteDownedCorpse","SetInteractionLabel",48,"9838C9A245CECF130DC7D85EAB470D742764C2AA721D5C60E5BAB385E13F9502"),
        new("DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI.MultiplayerUI","PushSystemMessage",117,"94BBE0BCEE2A1014093A1B88859A6021960C8D587C895974BF935340CA118A4F"),
    };
}

public readonly struct CoopReviveCostIlPin
{
    public readonly string Type,Method,Sha256;public readonly int Length;
    public CoopReviveCostIlPin(string type,string method,int length,string sha256){Type=type;Method=method;Length=length;Sha256=sha256;}
}

// One payment per downed episode. Keyed by the partner's user id; an entry is live only while that partner's current
// episode token (the RemoteDownedState object) is the one that was paid for. Game thread only.
public sealed class CoopReviveCostLedger
{
    public sealed class Entry
    {
        public int Target;public object Episode;public int Number;public long PaidTicks;public int ChargesBefore,ChargesAfter;
        public int FreeResends;public string Level;
    }
    readonly Dictionary<int,Entry> _entries=new();
    int _numbers;
    public int Count=>_entries.Count;
    public int Payments=>_numbers;
    public IEnumerable<Entry> Entries=>_entries.Values;

    public bool IsLatched(int target,object episode)=>
        target>0&&episode!=null&&_entries.TryGetValue(target,out var e)&&ReferenceEquals(e.Episode,episode);

    // A real payment for target's current episode. Returns the new entry; replaced receives a previous entry of the same
    // target (an episode that ended without a sweep), or null.
    public Entry Pay(int target,object episode,long ticks,int before,int after,string level,out Entry replaced)
    {
        replaced=null;
        if(target<=0||episode==null)return null;
        if(_entries.TryGetValue(target,out var old))
        {
            if(ReferenceEquals(old.Episode,episode))return null;     // already paid for this episode: never a second entry
            replaced=old;
        }
        var e=new Entry{Target=target,Episode=episode,Number=++_numbers,PaidTicks=ticks,ChargesBefore=before,ChargesAfter=after,Level=level??""};
        _entries[target]=e;
        return e;
    }
    // A request resent without a charge for target's current (paid) episode; null when it is not latched.
    public Entry Resend(int target,object episode)
    {
        if(!IsLatched(target,episode))return null;
        var e=_entries[target];e.FreeResends++;return e;
    }
    // Ends every entry whose partner no longer has the paid episode object (revived, pruned, reset, or downed again with a
    // new object). currentEpisode(target) returns the partner's current RemoteDownedState or null.
    public List<Entry> Sweep(Func<int,object> currentEpisode)
    {
        var closed=new List<Entry>();
        if(_entries.Count==0)return closed;
        foreach(var e in _entries.Values)
        {
            object now=null;
            try{now=currentEpisode(e.Target);}catch{now=null;}
            if(!ReferenceEquals(now,e.Episode))closed.Add(e);
        }
        foreach(var e in closed)_entries.Remove(e.Target);
        return closed;
    }
    public Entry Get(int target)=>_entries.TryGetValue(target,out var e)?e:null;
}

// Per-key rate limit of log lines: Allow returns true at most once per interval per key and reports how many calls were
// held back since the last allowed one.
public sealed class CoopReviveCostLogGate
{
    readonly Dictionary<string,(long Last,long Held)> _keys=new(StringComparer.Ordinal);
    public bool Allow(string key,long nowTicks,long intervalTicks,out long held)
    {
        held=0;
        if(_keys.TryGetValue(key,out var s))
        {
            if(nowTicks-s.Last<intervalTicks){_keys[key]=(s.Last,s.Held+1);return false;}
            held=s.Held;
        }
        _keys[key]=(nowTicks,0);
        return true;
    }
    public void Forget(string key){_keys.Remove(key);}
}
