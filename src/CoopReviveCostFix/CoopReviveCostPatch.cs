using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using dc.en;
using dc.tool.mainSkills;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using HarmonyLib;
using File=System.IO.File;
using Path=System.IO.Path;

// Part CoopReviveCostFix (Alpha2 unit U6; PLAN-ALPHA2.md issue 5; OWNER-DECISIONS.md item 1): the reviver pays exactly one
// flask charge per downed episode of the partner, a revive is impossible with 0 charges and the label over the downed
// partner says so, a request that cannot arrive costs nothing, and every payment is logged. Reviver side only; nothing goes
// on the wire, so a partner without this part is not affected (no capability token).
// Patches (release member names, IL-hash pinned in CoopReviveCostRules.IlPins; harmony id CoopReviveCostRules.HarmonyId):
//   1. ModEntry.ShowReviveHintFor(int)        replacing prefix (IL:22772): with 0 charges and an unpaid episode it writes our
//      hint on the partner's corpse (null on the others) instead of "Hold to revive." and skips the original; otherwise the
//      original runs. Not a postfix: overwriting the label every frame would rebuild the LightTip (IL:55006, 56293-56340);
//      RemoteDownedCorpse.SetInteractionLabel returns early for an unchanged text (IL:55015-55021), so one text per frame
//      means one LightTip per change.
//   2. ModEntry.TryConsumeOneFlask(Hero)      prefix + postfix (IL:25264). Its only caller is ProcessReviveHold (IL:25080),
//      which reaches it only while _reviveHoldTargetId is the nearest downed partner (IL:25044-25056), and sends the request
//      only when it returns true (IL:25081-25088). Prefix: CoopReviveCostRules.Decide (already paid -> true without a
//      charge, the request is resent for free; no connection / other level -> false, no charge and no request). Postfix:
//      a real payment (true and the charge count went down) latches the partner's current episode, logs it and shows the
//      reviver one system message.
//   3. ModEntry.ConsumeRemoteDownedStates(NetNode)  postfix (IL:23487): ends the latch of every partner whose episode object
//      is gone (revived, pruned, reset, downed again) and logs the episode summary.
// Every body runs in try/catch behind a fault breaker (CoopReviveCostRules.ShouldTrip); a tripped or disabled part passes
// every call through to the unchanged base method. Kill switch COOP_REVIVE_COST_DISABLE=1 (checked first: nothing is
// verified or patched). GameProxy members are pinned symbolically (CoopReviveCostProxyCompatibility), never by raw hash.
public static class CoopReviveCostPatch
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    const int Unknown=int.MinValue;
    static readonly object Gate=new();
    static volatile bool Published;
    static volatile bool Tripped;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}

    // ---- bound members (verified at Install) ----
    static FieldInfo RemoteDowned,RemoteDownedCines,HoldTarget,StateLevel;
    static Func<ModEntry,string> CurrentLevel;
    static Func<NetNode,bool> HasConnection;
    static MethodInfo ShowHint,ConsumeFlask,ConsumeDowned;

    // ---- seams: the native reads and writes; replaced only by checks\Test-Bind.ps1 in its offline process ----
    static Func<Hero,int> ChargeReader=ReadChargesNative;
    static Func<string> LangReader=ReadLangNative;
    static Action<RemoteDownedCorpse,string> LabelWriter=WriteLabelNative;
    static Action<string> MessageSink=PushMessageNative;

    // ---- state (game thread) ----
    static readonly CoopReviveCostLedger Ledger=new();
    static readonly CoopReviveCostLogGate Logs=new();
    static int ConsecutiveFaults,TotalFaults;
    static long HintCalls,NoChargeRefusals,Payments,FreeResends,NotSentNoConnection,NotSentOtherLevel,EpisodesClosed,UnexpectedResults;
    static string LangCache="en";static long LangReadAt;

    static bool Active=>Published&&!Disabled&&!Tripped;
    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static long Ticks(double seconds)=>(long)(seconds*Stopwatch.Frequency);

    public static void Install()
    {
        lock(Gate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier revive cost initialization failed: "+InitializationError);
            if(Environment.GetEnvironmentVariable(CoopReviveCostRules.Switch)=="1"){Disabled=true;Published=true;return;}
            Harmony harmony=null;var patched=new List<MethodBase>();
            try
            {
                var original=typeof(ModEntry).Assembly;
                if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||
                    Hash(Path.Combine(Path.GetDirectoryName(typeof(Hero).Assembly.Location),"hlboot.dat"))!=NativePin)
                    throw new InvalidOperationException("CoopReviveCostFix requires exact community0.5.1/native35.13.5");
                CoopReviveCostProxyCompatibility.ValidateLoaded(typeof(Hero).Assembly);
                Bind(original);
                harmony=new Harmony(CoopReviveCostRules.HarmonyId);
                patched.Add(ShowHint);
                harmony.Patch(ShowHint,prefix:new HarmonyMethod(Own(nameof(BeforeShowHint))));
                patched.Add(ConsumeFlask);
                harmony.Patch(ConsumeFlask,prefix:new HarmonyMethod(Own(nameof(BeforeConsumeFlask))),postfix:new HarmonyMethod(Own(nameof(AfterConsumeFlask))));
                patched.Add(ConsumeDowned);
                harmony.Patch(ConsumeDowned,postfix:new HarmonyMethod(Own(nameof(AfterConsumeDowned))));
                Exactly(ShowHint,1,0);Exactly(ConsumeFlask,1,1);Exactly(ConsumeDowned,0,1);
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;
                if(harmony!=null)foreach(var m in patched)try{harmony.Unpatch(m,HarmonyPatchType.All,CoopReviveCostRules.HarmonyId);}
                    catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    static MethodInfo Own(string name)=>typeof(CoopReviveCostPatch).GetMethod(name,S|BindingFlags.DeclaredOnly)??throw new MissingMethodException(nameof(CoopReviveCostPatch),name);
    static void Exactly(MethodBase target,int prefixes,int postfixes)
    {
        var info=Harmony.GetPatchInfo(target);string id=CoopReviveCostRules.HarmonyId;
        if(info==null||info.Prefixes.Count(p=>p.owner==id)!=prefixes||info.Postfixes.Count(p=>p.owner==id)!=postfixes||
            info.Transpilers.Any(p=>p.owner==id)||info.Finalizers.Any(p=>p.owner==id))
            throw new InvalidOperationException("Revive cost patch not installed exactly once on "+target.Name);
    }
    static FieldInfo Field(Type type,string name,Type fieldType,bool isStatic)
    {
        var field=type.GetField(name,(isStatic?S:I)|BindingFlags.DeclaredOnly);
        if(field==null||(fieldType!=null&&field.FieldType!=fieldType))throw new InvalidOperationException("Pinned field mismatch: "+type.FullName+"."+name);
        return field;
    }
    static MethodInfo Method(Type type,string name,bool isStatic,Type ret,params Type[] args)
    {
        var m=type.GetMethod(name,(isStatic?S:I)|BindingFlags.DeclaredOnly,null,args,null);
        if(m==null||m.ReturnType!=ret)throw new InvalidOperationException("Pinned method signature changed: "+type.FullName+"."+name);
        return m;
    }
    static void Bind(Assembly original)
    {
        // IL body pins of every patched or relied-on base method (lengths = release IL dump headers).
        foreach(var pin in CoopReviveCostRules.IlPins)
        {
            var type=original.GetType(pin.Type,true);
            var found=type.GetMethods(S|I|BindingFlags.DeclaredOnly).Where(m=>m.Name==pin.Method).ToArray();
            if(found.Length!=1)throw new InvalidOperationException("Pinned method not unique: "+pin.Type+"."+pin.Method);
            var il=found[0].GetMethodBody()?.GetILAsByteArray();
            if(il==null||il.Length!=pin.Length||Convert.ToHexString(SHA256.HashData(il))!=pin.Sha256)
                throw new InvalidOperationException("Pinned body changed: "+pin.Type+"."+pin.Method);
        }
        var mod=typeof(ModEntry);
        RemoteDowned=Field(mod,"_remoteDowned",null,false);
        var downedType=RemoteDowned.FieldType;
        if(!downedType.IsGenericType||downedType.GetGenericTypeDefinition()!=typeof(Dictionary<,>)||downedType.GetGenericArguments()[0]!=typeof(int)||
            downedType.GetGenericArguments()[1].FullName!="DeadCellsMultiplayerMod.ModEntry+RemoteDownedState"||!RemoteDowned.IsInitOnly)
            throw new InvalidOperationException("Pinned downed-partner table changed");
        StateLevel=Field(downedType.GetGenericArguments()[1],"LevelId",typeof(string),false);
        RemoteDownedCines=Field(mod,"_remoteDownedCines",typeof(Dictionary<int,RemoteDownedCorpse>),false);
        if(!RemoteDownedCines.IsInitOnly)throw new InvalidOperationException("Pinned corpse table is not readonly");
        HoldTarget=Field(mod,"_reviveHoldTargetId",typeof(int),false);
        if(Field(mod,"me",typeof(Hero),true)==null||Field(mod,"_net",typeof(NetNode),true)==null)throw new InvalidOperationException("Pinned static hero/net changed");
        CurrentLevel=(Func<ModEntry,string>)Delegate.CreateDelegate(typeof(Func<ModEntry,string>),Method(mod,"GetCurrentLevelId",false,typeof(string)));
        HasConnection=(Func<NetNode,bool>)Delegate.CreateDelegate(typeof(Func<NetNode,bool>),Method(typeof(NetNode),"HasAnyConnection",false,typeof(bool)));
        ShowHint=Method(mod,"ShowReviveHintFor",false,typeof(void),typeof(int));
        ConsumeFlask=Method(mod,"TryConsumeOneFlask",false,typeof(bool),typeof(Hero));
        ConsumeDowned=Method(mod,"ConsumeRemoteDownedStates",false,typeof(void),typeof(NetNode));
        if(ShowHint.GetParameters()[0].Name!="userId"||ConsumeFlask.GetParameters()[0].Name!="hero")throw new InvalidOperationException("Pinned parameter names changed");
        Method(typeof(RemoteDownedCorpse),"SetInteractionLabel",false,typeof(void),typeof(string));
        Method(typeof(MultiplayerUI),"PushSystemMessage",true,typeof(void),typeof(string),typeof(double),typeof(double));
    }

    // ---- native reads/writes (GameProxy members are covered by the symbolic pin) ----
    static int ReadChargesNative(Hero hero)
    {
        // The same reads as the base TryConsumeOneFlask (IL:25273-25291).
        var skills=hero.mainSkillsManager;if(skills==null)return Unknown;
        var heal=skills.getMainSkill(Heal.Class) as Heal;if(heal==null)return Unknown;
        return heal.get_healings();
    }
    static string ReadLangNative()=>dc.Lang.Class.LANG?.ToString();
    static void WriteLabelNative(RemoteDownedCorpse corpse,string text)=>corpse.SetInteractionLabel(text);
    static void PushMessageNative(string text)=>MultiplayerUI.PushSystemMessage(text,CoopReviveCostRules.MessageSeconds,CoopReviveCostRules.MessageFadeSeconds);
    static int Charges(Hero hero){if(hero==null)return Unknown;try{return ChargeReader(hero);}catch{return Unknown;}}
    static string Lang()
    {
        long now=Stopwatch.GetTimestamp();
        if(LangReadAt==0||now-LangReadAt>Ticks(2)){LangReadAt=now;try{LangCache=CoopReviveCostRules.NormalizeLang(LangReader());}catch{LangCache="en";}}
        return LangCache;
    }
    static object Episode(ModEntry mod,int userId)
    {
        if(mod==null||userId<=0)return null;
        return (RemoteDowned.GetValue(mod) as IDictionary)?[userId];
    }

    // ---- 1. hint ----
    static bool BeforeShowHint(ModEntry __instance,int userId)
    {
        if(!Active)return true;
        try
        {
            int charges=Charges(ModEntry.me);
            bool latched=Ledger.IsLatched(userId,Episode(__instance,userId));
            if(CoopReviveCostRules.ChooseHint(charges!=Unknown,charges,latched)!=CoopReviveCostHint.NeedCharge){ConsecutiveFaults=0;return true;}
            if(!(RemoteDownedCines.GetValue(__instance) is Dictionary<int,RemoteDownedCorpse> cines)||cines.Count==0){ConsecutiveFaults=0;return false;}
            string text=CoopReviveCostRules.HintText(Lang());
            foreach(var pair in cines)
            {
                if(pair.Value==null)continue;
                // As in the base method: each corpse in its own try, the matching one gets the text, the others none.
                try{LabelWriter(pair.Value,pair.Key==userId?text:null);}catch{}
            }
            Interlocked.Increment(ref HintCalls);
            if(Logs.Allow("hint:"+userId,Stopwatch.GetTimestamp(),Ticks(CoopReviveCostRules.HintLogSeconds),out long held))
                Info($"Revive hint shown: a flask charge is needed (target={userId} charges={Num(charges)} lang={LangCache} held={held})");
            ConsecutiveFaults=0;
            return false;
        }
        catch(Exception error){Fault("hint",error);return true;}
    }

    // ---- 2. payment ----
    static bool BeforeConsumeFlask(ModEntry __instance,Hero hero,ref bool __result,bool __runOriginal,out CoopReviveCostCall __state)
    {
        __state=null;
        if(!Active)return true;
        try
        {
            var call=new CoopReviveCostCall{Target=(int)HoldTarget.GetValue(__instance)};
            call.Episode=Episode(__instance,call.Target);
            call.Before=Charges(hero);
            var net=ModEntry._net;
            bool connected=net!=null&&HasConnection(net);
            int netId=net==null?0:net.id;
            call.Level=CurrentLevel(__instance);
            string targetLevel=call.Episode==null?null:StateLevel.GetValue(call.Episode) as string;
            call.Decision=CoopReviveCostRules.Decide(__runOriginal,call.Target,call.Episode!=null,connected,netId,call.Level,targetLevel,Ledger.IsLatched(call.Target,call.Episode));
            call.TargetLevel=targetLevel;
            __state=call;ConsecutiveFaults=0;
            if(!CoopReviveCostRules.SkipsOriginal(call.Decision))return true;
            __result=CoopReviveCostRules.ResultWhenSkipped(call.Decision);
            return false;
        }
        catch(Exception error){__state=null;Fault("payment check",error);return true;}
    }
    static void AfterConsumeFlask(ModEntry __instance,Hero hero,bool __result,CoopReviveCostCall __state)
    {
        var call=__state;
        if(call==null||!Active)return;
        try
        {
            long now=Stopwatch.GetTimestamp();
            switch(call.Decision)
            {
                case CoopReviveCostDecision.Original:
                {
                    int after=Charges(hero);
                    if(CoopReviveCostRules.IsPayment(__result,call.Before!=Unknown,call.Before,after!=Unknown,after))
                    {
                        var entry=Ledger.Pay(call.Target,call.Episode,now,call.Before,after,call.Level,out var replaced);
                        if(replaced!=null)Closed(replaced,"replaced by a new episode");
                        if(entry==null)break;
                        Interlocked.Increment(ref Payments);
                        Info($"Revive paid: charges {call.Before}->{after} target={call.Target} episode={entry.Number} level={call.Level}");
                        if(after!=call.Before-1)Warn($"Revive payment took {call.Before-after} charges (charges {call.Before}->{after} target={call.Target}); counted as one episode");
                        try{MessageSink(CoopReviveCostRules.PaidText(Lang()));}catch{}
                    }
                    else if(CoopReviveCostRules.IsNoChargeRefusal(__result,call.Before!=Unknown,call.Before))
                    {
                        Interlocked.Increment(ref NoChargeRefusals);
                        if(Logs.Allow("nocharge:"+call.Target,now,Ticks(CoopReviveCostRules.RefusalLogSeconds),out long held))
                            Info($"Revive not possible: no flask charge (target={call.Target} held={held})");
                    }
                    else if(__result)
                    {
                        // True without a lower charge count (another patch or an unknown count): no payment, so no latch.
                        Interlocked.Increment(ref UnexpectedResults);
                        if(Logs.Allow("unexpected",now,Ticks(CoopReviveCostRules.RefusalLogSeconds),out long held))
                            Warn($"Revive request allowed without a counted charge (charges {Num(call.Before)}->{Num(after)} target={call.Target} held={held}); not latched");
                    }
                    break;
                }
                case CoopReviveCostDecision.FreeResend:
                {
                    Interlocked.Increment(ref FreeResends);
                    var entry=Ledger.Resend(call.Target,call.Episode);
                    if(entry!=null&&(entry.FreeResends==1||Logs.Allow("resend:"+call.Target,now,Ticks(CoopReviveCostRules.ResendLogSeconds),out _)))
                        Info($"Revive request resent without a charge: target={call.Target} episode={entry.Number} resend={entry.FreeResends}");
                    break;
                }
                case CoopReviveCostDecision.NoRequestNoConnection:
                case CoopReviveCostDecision.NoRequestOtherLevel:
                {
                    bool noLink=call.Decision==CoopReviveCostDecision.NoRequestNoConnection;
                    if(noLink)Interlocked.Increment(ref NotSentNoConnection);else Interlocked.Increment(ref NotSentOtherLevel);
                    if(Logs.Allow("notsent:"+call.Decision,now,Ticks(CoopReviveCostRules.RefusalLogSeconds),out long held))
                        Info($"Revive request not sent ({(noLink?"no connection":"partner on another level")}); no charge taken (target={call.Target} level={call.Level} partnerLevel={call.TargetLevel} held={held})");
                    break;
                }
            }
            ConsecutiveFaults=0;
        }
        catch(Exception error){Fault("payment record",error);}
    }

    // ---- 3. episode end ----
    static void AfterConsumeDowned(ModEntry __instance)
    {
        if(!Active||Ledger.Count==0)return;
        try
        {
            var table=RemoteDowned.GetValue(__instance) as IDictionary;
            foreach(var e in Ledger.Sweep(t=>table?[t]))Closed(e,"partner up, gone or downed again");
            ConsecutiveFaults=0;
        }
        catch(Exception error){Fault("episode sweep",error);}
    }
    static void Closed(CoopReviveCostLedger.Entry e,string why)
    {
        Interlocked.Increment(ref EpisodesClosed);
        Logs.Forget("resend:"+e.Target);
        double seconds=(Stopwatch.GetTimestamp()-e.PaidTicks)/(double)Stopwatch.Frequency;
        Info($"Revive episode closed: target={e.Target} episode={e.Number} paid=1 freeResends={e.FreeResends} after={seconds.ToString("0.0",CultureInfo.InvariantCulture)}s ({why})");
    }

    // ---- faults and logs ----
    static void Fault(string where,Exception error)
    {
        int consecutive=++ConsecutiveFaults,total=++TotalFaults;
        bool trip=CoopReviveCostRules.ShouldTrip(consecutive,total);
        if(trip)Tripped=true;
        if(total==1||trip)
            Warn($"fault in {where}{(trip?"; part switched off for this session (the base revive flow runs unchanged)":"")}: {error.GetBaseException().GetType().Name}: {error.GetBaseException().Message}");
    }
    // Pre-formatted text as one literal property (the PackLog pattern): the rendered line is exactly "[CoopReviveCostFix] <text>".
    static void Info(string text){try{Serilog.Log.Information("{Text:l}",CoopReviveCostRules.LogPrefix+" "+text);}catch{}}
    static void Warn(string text){try{Serilog.Log.Warning("{Text:l}",CoopReviveCostRules.LogPrefix+" "+text);}catch{}}
    static string Num(int value)=>value==Unknown?"unknown":value.ToString(CultureInfo.InvariantCulture);

    // ---- observation for the unit probe and the offline checks (read-only copies) ----
    public static Dictionary<string,object> Snapshot()
    {
        var rows=Ledger.Entries.Select(e=>(object)new Dictionary<string,object>{["target"]=e.Target,["episode"]=e.Number,["chargesBefore"]=e.ChargesBefore,
            ["chargesAfter"]=e.ChargesAfter,["freeResends"]=e.FreeResends,["level"]=e.Level}).ToList();
        return new Dictionary<string,object>
        {
            ["published"]=Published,["disabled"]=Disabled,["tripped"]=Tripped,["initializationError"]=InitializationError,["faults"]=TotalFaults,
            ["hintCalls"]=Interlocked.Read(ref HintCalls),["noChargeRefusals"]=Interlocked.Read(ref NoChargeRefusals),["payments"]=Interlocked.Read(ref Payments),
            ["freeResends"]=Interlocked.Read(ref FreeResends),["notSentNoConnection"]=Interlocked.Read(ref NotSentNoConnection),
            ["notSentOtherLevel"]=Interlocked.Read(ref NotSentOtherLevel),["episodesClosed"]=Interlocked.Read(ref EpisodesClosed),
            ["unexpectedResults"]=Interlocked.Read(ref UnexpectedResults),["ledgerPayments"]=Ledger.Payments,["latched"]=rows,["lang"]=LangCache,
        };
    }
}

// Prefix -> postfix record of one TryConsumeOneFlask call (Harmony __state).
public sealed class CoopReviveCostCall
{
    public CoopReviveCostDecision Decision;public int Target;public object Episode;public int Before=int.MinValue;public string Level,TargetLevel;
}
