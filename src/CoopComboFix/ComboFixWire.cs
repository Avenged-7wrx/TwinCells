using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using R=ComboFixRules;

// Part B capability exchange of CoopComboFix (format and timing in ComboFixRules: CCMB1|HI / CCMB1|OK). The client
// multiplies its hit hints by its own combo only while the host answered "on" (PLAN-ALPHA2.md section 4 rule 7: the
// feature stays off when the peer lacks it; a host without this part never answers, a host whose part is off or tripped
// answers "off"). Pattern of CoopLobby's colour part (LobbyColor.cs:26-46), reduced to one request and one answer:
//  - our Priority.First prefix on NetNode.HandleLine consumes every CCMB1| line (__result=true, forwardLine=null, return
//    false), so the base never parses it and the host never forwards it; when an earlier prefix already skipped the
//    original (CoopLobby's refused-sender drop), its verdict stands and the line is left alone (__runOriginal=false);
//  - a postfix on NetNode.RejectIncompatiblePeer sees the client accept a WELCOME (false = accepted; on the client it is
//    called only from HandleLine's WELCOME branch) and starts a connection epoch with a fresh nonce; the client says HI
//    only once the ID line was applied (net.id > 1), the causal proof of a finished handshake (CSF ElevatorProtocol.cs:93-96);
//  - the client sends with NetNode.SendLineSafe (to the host); the host answers with NetNode.SendLineSafe, which
//    broadcasts to its clients: the answer carries the client's nonce, CoopLobby admits one client, and a client without
//    this part drops the line silently (no HandleLine tag matches "CCMB1|", TryParsePositionLine fails, release IL
//    9853-14209). "CCMB1|" starts with no tag the base, CSF ("MOB" lines only) or CoopLobby handle.
//  - HandleLine runs in MainThreadPump jobs on the game thread; still the prefix only parses and enqueues, and every send
//    happens in the frame tick (ModEntry.OnFrameUpdate postfix).
public static class ComboFixWire
{
    sealed class Link{public R.ClientLink Client;public bool WelcomeAccepted;public bool LoggedSupport,LastSupport,LoggedGaveUp;}
    struct Incoming{public NetNode Net;public int Sender;public R.Kind Kind;public string Nonce;public bool On;}
    const string LobbyCapOwner="CoopLobby.Cap.20261110";   // LobbyCap.Id (alpha2-lobby\source\LobbyCap.cs)
    static readonly ConditionalWeakTable<NetNode,Link> Links=new();
    static readonly ConcurrentQueue<Incoming> Inbox=new();
    static int _inboxCount;
    const int InboxCap=32;
    static NetNode _session;
    static long _windowStart=long.MinValue;static int _inWindow;
    static readonly HashSet<string> AnsweredNonces=new(StringComparer.Ordinal);
    static long _hisSent,_answersSent,_answersReceived;

    public static void Install(Harmony harmony,List<MethodBase> patched)
    {
        void Patch(MethodBase target,string prefix,string postfix,int priority)
        {
            patched.Add(target);
            // after: CoopLobby's cap part drops every line of a refused sender in its own Priority.First HandleLine prefix;
            // ours runs after it whatever the registration order (this standalone part initialises before the pack), so a
            // refused sender's CCMB1 line keeps that verdict (__runOriginal=false) and is never answered.
            harmony.Patch(target,prefix:prefix==null?null:new HarmonyMethod(typeof(ComboFixWire).GetMethod(prefix,BindingFlags.Static|BindingFlags.NonPublic)){priority=priority,after=new[]{LobbyCapOwner}},
                postfix:postfix==null?null:new HarmonyMethod(typeof(ComboFixWire).GetMethod(postfix,BindingFlags.Static|BindingFlags.NonPublic)){priority=priority});
            var info=Harmony.GetPatchInfo(target);
            int ours(IEnumerable<Patch> ps){int c=0;foreach(var p in ps)if(p.owner==ComboFixPatch.Owner)c++;return c;}
            if(info==null||ours(info.Prefixes)!=(prefix==null?0:1)||ours(info.Postfixes)!=(postfix==null?0:1)||ours(info.Transpilers)!=0||ours(info.Finalizers)!=0)
                throw new InvalidOperationException("Wire patch on "+target.Name+" was not installed exactly once");
        }
        // Before any connection: a WELCOME accepted before our postfix existed would leave that link without an epoch.
        if(ModEntry._net?.IsAlive==true)throw new InvalidOperationException("the combo wire must install before any connection");
        Patch(ComboFixPatch.HandleLineMethod,nameof(BeforeHandleLine),null,Priority.First);
        Patch(ComboFixPatch.RejectMethod,null,nameof(AfterReject),Priority.Normal);
    }

    static bool BeforeHandleLine(NetNode __instance,string line,int? senderId,ref string forwardLine,ref bool __result,bool __runOriginal)
    {
        if(line==null||!line.StartsWith(R.Tag,StringComparison.Ordinal))return true;
        if(!__runOriginal)return false;
        __result=true;forwardLine=null;
        try
        {
            if(__instance==null||!ReferenceEquals(__instance,ModEntry._net)||!__instance.IsAlive)return false;
            if(!R.TryParse(line,out var kind,out var nonce,out var on))return false;
            if(__instance.IsHost)
            {
                // Transport identity only (senderId), never an id from the payload; the host is id 1.
                if(kind!=R.Kind.Hi||!senderId.HasValue||senderId.Value<=1)return false;
                Enqueue(new Incoming{Net=__instance,Sender=senderId.Value,Kind=kind,Nonce=nonce});
            }
            else
            {
                if(kind!=R.Kind.Answer||!Links.TryGetValue(__instance,out var link)||!link.WelcomeAccepted||link.Client==null||__instance.id<=1)return false;
                Enqueue(new Incoming{Net=__instance,Sender=1,Kind=kind,Nonce=nonce,On=on});
            }
        }
        catch(Exception e){ComboFixPatch.Fault(e);}
        return false;
    }
    static void Enqueue(Incoming item)
    {
        if(Interlocked.Increment(ref _inboxCount)>InboxCap){Interlocked.Decrement(ref _inboxCount);return;}
        Inbox.Enqueue(item);
    }
    static bool Dequeue(out Incoming item)
    {
        if(!Inbox.TryDequeue(out item))return false;
        Interlocked.Decrement(ref _inboxCount);return true;
    }
    static void AfterReject(NetNode __instance,string expectedTag,bool __result)
    {
        try
        {
            // __result=true: rejected; the link drops. On the client this is called only from the WELCOME branch.
            if(__result||__instance==null||__instance.IsHost||expectedTag!="WELCOME")return;
            var link=Links.GetValue(__instance,_=>new Link());
            link.WelcomeAccepted=true;link.Client=new R.ClientLink(Guid.NewGuid().ToString("N"));link.LoggedGaveUp=false;
        }
        catch(Exception e){ComboFixPatch.Fault(e);}
    }

    public static bool ClientSupported(NetNode net,long now)=>!ComboFixPatch.Tripped&&net!=null&&Links.TryGetValue(net,out var link)&&link.WelcomeAccepted&&link.Client!=null&&link.Client.Supported(now);
    static bool HostOn()=>ComboFixPatch.Installed&&ComboFixPatch.PartBOn&&!ComboFixPatch.Tripped;

    // Game thread, every frame (ComboFixPatch.AfterFrame).
    public static void Tick(long now)
    {
        var net=ModEntry._net;
        var current=net!=null&&net.IsAlive?net:null;
        if(!ReferenceEquals(current,_session))
        {
            _session=current;AnsweredNonces.Clear();
            while(Dequeue(out _)){}
        }
        if(current==null)return;
        if(current.IsHost)HostTick(current,now);else ClientTick(current,now);
    }
    static void HostTick(NetNode net,long now)
    {
        while(Dequeue(out var m))
        {
            if(!ReferenceEquals(m.Net,net)||m.Kind!=R.Kind.Hi)continue;
            if(!R.AnswerAllowed(now,ref _windowStart,ref _inWindow))continue;
            bool on=HostOn();
            Send(net,R.Answer(m.Nonce,on));_answersSent++;
            if(AnsweredNonces.Count<64&&AnsweredNonces.Add(m.Nonce))
                ComboFixPatch.Info("Partner (id "+m.Sender+") asked whether the host keeps its own combo out of the partner's hits; answered "+(on?"on":"off"));
        }
    }
    static void ClientTick(NetNode net,long now)
    {
        Links.TryGetValue(net,out var link);
        while(Dequeue(out var m))
        {
            if(!ReferenceEquals(m.Net,net)||m.Kind!=R.Kind.Answer||link?.Client==null)continue;
            if(link.Client.Accept(m.Nonce,m.On,now))_answersReceived++;
        }
        if(link==null||!link.WelcomeAccepted||link.Client==null||net.id<=1)return;
        bool supported=link.Client.Supported(now);
        if(!link.LoggedSupport||supported!=link.LastSupport)
        {
            if(link.Client.Answered||link.LoggedSupport)
            {
                link.LoggedSupport=true;link.LastSupport=supported;
                ComboFixPatch.Info(supported?"Host combo support: on (the client's own combo multiplier is sent with its hits)":
                    "Host combo support: off (hits are sent without the client's combo multiplier)");
            }
        }
        if(link.Client.GaveUp&&!link.LoggedGaveUp)
        {
            link.LoggedGaveUp=true;
            ComboFixPatch.Info("Host did not answer the combo support request (host without CoopComboFix); hits are sent without the client's combo multiplier");
        }
        if(link.Client.HiDue(now))
        {
            Send(net,R.Hi(link.Client.Nonce));link.Client.SentHi(now);_hisSent++;
        }
    }
    static void Send(NetNode net,string line)=>ComboFixPatch.SendLineMethod.Invoke(net,new object[]{line});

    public static string Status(long now)
    {
        var net=ModEntry._net;bool supported=false,answered=false,on=false;int unanswered=0;
        if(net!=null&&!net.IsHost&&Links.TryGetValue(net,out var link)&&link.Client!=null)
        {supported=link.Client.Supported(now);answered=link.Client.Answered;on=link.Client.On;unanswered=link.Client.Unanswered;}
        return "clientSupported="+(supported?1:0)+";answered="+(answered?1:0)+";answerOn="+(on?1:0)+";unanswered="+unanswered+
            ";hisSent="+_hisSent+";answersSent="+_answersSent+";answersReceived="+_answersReceived;
    }
}
