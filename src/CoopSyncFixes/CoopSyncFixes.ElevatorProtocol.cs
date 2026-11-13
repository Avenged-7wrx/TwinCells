using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Newtonsoft.Json;

public sealed partial class CoopSyncFixes
{
    // Enabled by the integrated entry point before either peer connects.
    const string ElevatorCapability="+csfelev1", ElevatorTag="CSFEL1|";
    const BindingFlags ElevatorInstanceFlags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly ConditionalWeakTable<NetNode,ElevatorSession> ElevatorSessions=new();
    sealed class ElevatorMessage
    {
        public int V=1,Sender,Cx,Cy,Left,Right,Top,Bottom,Width,MoveDir;
        public long Epoch,Sequence,Request,BuildSerial;
        public string Kind,Token,Floor,Boot,Track,ConnectionNonce;
        [JsonIgnore] public object ReceivedConnection;
        [JsonIgnore] public long ReceivedAt;
        public double Xr,Yr,Ty,Dy,Bdy,Speed;
        // RIDE only (client -> host, optional token +csfelev2; Alpha2 U11). Default values are not written, so SUB, ACT,
        // ACK and STATE keep the exact Alpha1 JSON text.
        [JsonProperty(DefaultValueHandling=DefaultValueHandling.Ignore)] public int Ride;
        [JsonProperty(DefaultValueHandling=DefaultValueHandling.Ignore)] public double Offset;

        // ---- gc-opt P04: the wire line without Newtonsoft's objects (no member is added that Newtonsoft would see) ----
        // WriteJson writes what JsonConvert.SerializeObject(this, Formatting.None) writes with default settings: the public
        // fields in declaration order, no whitespace, ints and longs as invariant digits, doubles as ToString("R", invariant)
        // with ".0" added when the text has no '.', 'E' or 'e' and NaN / Infinity / -Infinity quoted (FloatFormatHandling.String),
        // strings with Newtonsoft's escapes (StringEscapeHandling.Default) or null, Ride and Offset left out when 0
        // (DefaultValueHandling.Ignore: Offset +0 and -0). Used only while Canonical() holds.
        internal void WriteJson(CsfElevatorScratch s)
        {
            CsfElevatorJson.Begin(s);
            CsfElevatorJson.Whole(s,"{\"V\":",V);CsfElevatorJson.Whole(s,",\"Sender\":",Sender);CsfElevatorJson.Whole(s,",\"Cx\":",Cx);CsfElevatorJson.Whole(s,",\"Cy\":",Cy);
            CsfElevatorJson.Whole(s,",\"Left\":",Left);CsfElevatorJson.Whole(s,",\"Right\":",Right);CsfElevatorJson.Whole(s,",\"Top\":",Top);CsfElevatorJson.Whole(s,",\"Bottom\":",Bottom);
            CsfElevatorJson.Whole(s,",\"Width\":",Width);CsfElevatorJson.Whole(s,",\"MoveDir\":",MoveDir);
            CsfElevatorJson.Whole(s,",\"Epoch\":",Epoch);CsfElevatorJson.Whole(s,",\"Sequence\":",Sequence);CsfElevatorJson.Whole(s,",\"Request\":",Request);CsfElevatorJson.Whole(s,",\"BuildSerial\":",BuildSerial);
            CsfElevatorJson.Text(s,",\"Kind\":",Kind);CsfElevatorJson.Text(s,",\"Token\":",Token);CsfElevatorJson.Text(s,",\"Floor\":",Floor);CsfElevatorJson.Text(s,",\"Boot\":",Boot);
            CsfElevatorJson.Text(s,",\"Track\":",Track);CsfElevatorJson.Text(s,",\"ConnectionNonce\":",ConnectionNonce);
            CsfElevatorJson.Number(s,",\"Xr\":",Xr);CsfElevatorJson.Number(s,",\"Yr\":",Yr);CsfElevatorJson.Number(s,",\"Ty\":",Ty);CsfElevatorJson.Number(s,",\"Dy\":",Dy);
            CsfElevatorJson.Number(s,",\"Bdy\":",Bdy);CsfElevatorJson.Number(s,",\"Speed\":",Speed);
            if(Ride!=0)CsfElevatorJson.Whole(s,",\"Ride\":",Ride);
            if(Offset!=0)CsfElevatorJson.Number(s,",\"Offset\":",Offset);   // NaN != 0: written, as Double.Equals(0d) is false for it
            CsfElevatorJson.End(s);
        }
        // ElevatorEncode's line: tag, Base64 of the UTF-8 bytes of WriteJson's text (Encoding.UTF8, lone surrogates as U+FFFD), "\n".
        internal string EncodeLine()
        {
            var s=CsfElevatorScratch.Take();
            try{WriteJson(s);CsfElevatorJson.ToLine(s,ElevatorTag);return new string(s.Line,0,s.LineLength);}
            finally{CsfElevatorScratch.Give(s);}
        }
        // ReadElevatorWire's parse. A line is read here only if it is EXACTLY the line ElevatorEncode makes for the message
        // read from it (the Base64 text after the tag and the CR/LF trim is compared char for char with the one written
        // again). For such a line the old expression decodes exactly that JSON text, and Newtonsoft reads that text back
        // into exactly these values (ReadAsInt32 / ReadAsInt64 / ReadAsString / ReadAsDouble = double.TryParse, also for the
        // quoted NaN and infinities). Every other line, and every line while Canonical() does not hold, goes through the
        // old expression itself: same result, same exception.
        internal static ElevatorMessage FromLine(string line)=>(line!=null?ReadCanonical(line):null)??
            JsonConvert.DeserializeObject<ElevatorMessage>(Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(ElevatorTag.Length).TrimEnd('\r','\n'))));
        static ElevatorMessage ReadCanonical(string line)=>line.Length>=ElevatorTag.Length && Canonical()?Parse(line):null;
        static ElevatorMessage Parse(string line)
        {
            var s=CsfElevatorScratch.Take();
            try
            {
                var text=line.AsSpan(ElevatorTag.Length).TrimEnd("\r\n");
                if(!CsfElevatorJson.FromBase64(s,text))return null;
                var m=new ElevatorMessage();int at=0;
                if(!(CsfElevatorJson.Expect(s,ref at,"{\"V\":") && CsfElevatorJson.Whole(s,ref at,out m.V) && CsfElevatorJson.Expect(s,ref at,",\"Sender\":") && CsfElevatorJson.Whole(s,ref at,out m.Sender) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Cx\":") && CsfElevatorJson.Whole(s,ref at,out m.Cx) && CsfElevatorJson.Expect(s,ref at,",\"Cy\":") && CsfElevatorJson.Whole(s,ref at,out m.Cy) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Left\":") && CsfElevatorJson.Whole(s,ref at,out m.Left) && CsfElevatorJson.Expect(s,ref at,",\"Right\":") && CsfElevatorJson.Whole(s,ref at,out m.Right) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Top\":") && CsfElevatorJson.Whole(s,ref at,out m.Top) && CsfElevatorJson.Expect(s,ref at,",\"Bottom\":") && CsfElevatorJson.Whole(s,ref at,out m.Bottom) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Width\":") && CsfElevatorJson.Whole(s,ref at,out m.Width) && CsfElevatorJson.Expect(s,ref at,",\"MoveDir\":") && CsfElevatorJson.Whole(s,ref at,out m.MoveDir) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Epoch\":") && CsfElevatorJson.Whole(s,ref at,out m.Epoch) && CsfElevatorJson.Expect(s,ref at,",\"Sequence\":") && CsfElevatorJson.Whole(s,ref at,out m.Sequence) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Request\":") && CsfElevatorJson.Whole(s,ref at,out m.Request) && CsfElevatorJson.Expect(s,ref at,",\"BuildSerial\":") && CsfElevatorJson.Whole(s,ref at,out m.BuildSerial) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Kind\":") && CsfElevatorJson.Text(s,ref at,out m.Kind) && CsfElevatorJson.Expect(s,ref at,",\"Token\":") && CsfElevatorJson.Text(s,ref at,out m.Token) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Floor\":") && CsfElevatorJson.Text(s,ref at,out m.Floor) && CsfElevatorJson.Expect(s,ref at,",\"Boot\":") && CsfElevatorJson.Text(s,ref at,out m.Boot) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Track\":") && CsfElevatorJson.Text(s,ref at,out m.Track) && CsfElevatorJson.Expect(s,ref at,",\"ConnectionNonce\":") && CsfElevatorJson.Text(s,ref at,out m.ConnectionNonce) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Xr\":") && CsfElevatorJson.Number(s,ref at,out m.Xr) && CsfElevatorJson.Expect(s,ref at,",\"Yr\":") && CsfElevatorJson.Number(s,ref at,out m.Yr) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Ty\":") && CsfElevatorJson.Number(s,ref at,out m.Ty) && CsfElevatorJson.Expect(s,ref at,",\"Dy\":") && CsfElevatorJson.Number(s,ref at,out m.Dy) &&
                    CsfElevatorJson.Expect(s,ref at,",\"Bdy\":") && CsfElevatorJson.Number(s,ref at,out m.Bdy) && CsfElevatorJson.Expect(s,ref at,",\"Speed\":") && CsfElevatorJson.Number(s,ref at,out m.Speed)))return null;
                if(CsfElevatorJson.Expect(s,ref at,",\"Ride\":") && !CsfElevatorJson.Whole(s,ref at,out m.Ride))return null;
                if(CsfElevatorJson.Expect(s,ref at,",\"Offset\":") && !CsfElevatorJson.Number(s,ref at,out m.Offset))return null;
                if(!CsfElevatorJson.Expect(s,ref at,"}") || at!=s.InputLength)return null;
                m.WriteJson(s);CsfElevatorJson.ToLine(s,ElevatorTag);
                return CsfElevatorJson.SameLine(s,ElevatorTag.Length,text)?m:null;
            }
            catch{return null;}   // never expected; the old expression then decides
            finally{CsfElevatorScratch.Give(s);}
        }
        // Canonical(): no global Newtonsoft default settings now (JsonConvert.DefaultSettings is what the old calls apply),
        // the Newtonsoft build is the one the parity checks ran on, and on first use WriteJson and Parse agree with
        // JsonConvert on a probe set (else the old expressions are used for good).
        internal static bool Canonical()
        {
            if(JsonConvert.DefaultSettings!=null)return false;
            var verdict=Volatile.Read(ref CsfElevatorJson.Verdict);
            if(verdict==0){verdict=AgreesWithNewtonsoft()?1:2;Volatile.Write(ref CsfElevatorJson.Verdict,verdict);}
            return verdict==1;
        }
        // Called once at module install (InitializeElevatorProtocol, before either peer connects): the Newtonsoft probe of
        // Canonical() runs here and not inside the first ElevatorEncode / FromLine (a game frame, or a receive thread whose
        // line age is already counting), and the type initializers of the holders used inside lock(NetNode._sync) /
        // lock(NetNode._clientsLock) run here and not under those locks. Nothing else changes: Canonical() still tests
        // DefaultSettings at every call (set now, the verdict is decided at a later first use, as before), and a holder whose
        // binding fails keeps its cached TypeInitializationException, thrown at its first use exactly as before.
        internal static void Warm()
        {
            try{Canonical();}catch{ /* decided at the first use instead */ }
            foreach(var holder in new[]{typeof(CsfElevatorBoxes),typeof(CsfElevatorConnections),typeof(CsfElevatorRemote),typeof(CsfElevatorMod)})
                try{RuntimeHelpers.RunClassConstructor(holder.TypeHandle);}catch{ /* surfaces at the holder's first use, as before */ }
        }
        static bool AgreesWithNewtonsoft()
        {
            try
            {
                if(typeof(JsonConvert).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion!=CsfElevatorJson.Version)return false;
                const string odd="\0\u0001\b\t\n\u000b\f\r\u001f \"'/\\\u007f\u0080\u0085\u00e9\u2028\u2029\ud83d\ude00\ud800x\udfff\ufffd";
                var probes=new[]{
                    new ElevatorMessage{Kind="SUB",Token="0123456789abcdef0123456789abcdef",Floor="00000000-0000-0000-0000-000000000000:1:2:PrisonStart:4607182418800017408:-5",BuildSerial=1,ConnectionNonce="n"},
                    new ElevatorMessage{Kind="STATE",Sequence=long.MaxValue,Epoch=long.MinValue,Track="-2:3:0:0:1",Left=int.MinValue,Right=int.MaxValue,Xr=-0.0,Yr=double.Epsilon,Ty=1e300,Dy=-1.5e-7,Bdy=0.1,Speed=1.0/3},
                    new ElevatorMessage{Kind="RIDE",Ride=1,Offset=-12.5,Xr=double.NaN,Yr=double.PositiveInfinity,Ty=double.NegativeInfinity,Dy=123456789012345678,Bdy=2.2250738585072014E-308,Speed=4.9E-324},
                    new ElevatorMessage{Kind=odd,Token="",Floor=odd+odd,Boot=null,Track="\u2028",Ride=-7,Offset=double.NaN,V=0,Sender=-1,Cx=12,Cy=-3,Ty=2304,Dy=-0.3642,Speed=2},
                    new ElevatorMessage{Kind="ACK",Offset=-0.0,Ride=0,Xr=0.6358333333333334,Yr=1e15,Ty=1e16,Dy=9007199254740993,Bdy=-1e-5,Speed=double.MaxValue}};
                foreach(var probe in probes)
                {
                    var line=ElevatorTag+Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(probe,Formatting.None)))+"\n";
                    if(probe.EncodeLine()!=line)return false;
                    var old=JsonConvert.DeserializeObject<ElevatorMessage>(Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(ElevatorTag.Length).TrimEnd('\r','\n'))));
                    var now=Parse(line);
                    if(old==null || now==null || !Same(old,now))return false;
                }
                return true;
            }
            catch{return false;}
        }
        static bool Same(ElevatorMessage a,ElevatorMessage b)=>
            a.V==b.V && a.Sender==b.Sender && a.Cx==b.Cx && a.Cy==b.Cy && a.Left==b.Left && a.Right==b.Right && a.Top==b.Top && a.Bottom==b.Bottom && a.Width==b.Width && a.MoveDir==b.MoveDir &&
            a.Epoch==b.Epoch && a.Sequence==b.Sequence && a.Request==b.Request && a.BuildSerial==b.BuildSerial && a.Ride==b.Ride &&
            string.Equals(a.Kind,b.Kind,StringComparison.Ordinal) && string.Equals(a.Token,b.Token,StringComparison.Ordinal) && string.Equals(a.Floor,b.Floor,StringComparison.Ordinal) &&
            string.Equals(a.Boot,b.Boot,StringComparison.Ordinal) && string.Equals(a.Track,b.Track,StringComparison.Ordinal) && string.Equals(a.ConnectionNonce,b.ConnectionNonce,StringComparison.Ordinal) &&
            BitConverter.DoubleToInt64Bits(a.Xr)==BitConverter.DoubleToInt64Bits(b.Xr) && BitConverter.DoubleToInt64Bits(a.Yr)==BitConverter.DoubleToInt64Bits(b.Yr) &&
            BitConverter.DoubleToInt64Bits(a.Ty)==BitConverter.DoubleToInt64Bits(b.Ty) && BitConverter.DoubleToInt64Bits(a.Dy)==BitConverter.DoubleToInt64Bits(b.Dy) &&
            BitConverter.DoubleToInt64Bits(a.Bdy)==BitConverter.DoubleToInt64Bits(b.Bdy) && BitConverter.DoubleToInt64Bits(a.Speed)==BitConverter.DoubleToInt64Bits(b.Speed) &&
            BitConverter.DoubleToInt64Bits(a.Offset)==BitConverter.DoubleToInt64Bits(b.Offset) && a.ReceivedConnection==b.ReceivedConnection && a.ReceivedAt==b.ReceivedAt;
    }
    sealed class ElevatorPeer
    {
        public object Connection;public bool Steam;public string Token,Floor;
        public long LastRequest,BuildSerial,LastSeen;
        public object PositionRecord;public long PositionSequence,PositionSeenAt;
        // Host view of this client's last RIDE report and of its presentation episode (U11).
        public string RideTrack;public bool RideOn,RidePresenting;public double RideOffset,RideMaxCorrection;
        public long RideAt,LastRide,RideLastMoving,RidePinned,RideStartedAt,RideMaxAge,RideMaxHold;
    }
    sealed class ElevatorSession
    {
        public readonly ConcurrentQueue<ElevatorMessage> Incoming=new();public int Queued;
        public readonly Dictionary<int,ElevatorPeer> Peers=new();
        public readonly Dictionary<string,ElevatorMessage> Latest=new();
        public readonly Dictionary<string,long> AcceptedSequences=new();
        public IntPtr Pointer;public int Build;public string Floor,Token,Boot=Guid.NewGuid().ToString("N"),RemoteBoot;
        public long Epoch,RemoteEpoch,Sequence,Request,LastSend,LastSubscribe,LocalBuildSerial;
        public bool WelcomeAccepted;
        // Client side of U11: the host's WELCOME carried +csfelev2; the RIDE being reported; the ride-log episode.
        public bool RemoteRide,RideLogMoved;public string RideTrack,RideLogType,RideLogLevelId,RideLogTrack;
        public long RideSequence,LastRideSend,RideLogStart,RideLogSent,RideLogApplied,RideLogSkipped;public IntPtr RideLogLift,RideLogLevel;public long[] RideLogBuckets;
        // gc-opt P04: the lists of the host's 10 Hz send in PumpElevators (game thread), taken out while in use.
        public List<string> SendTracks;public List<dc.en.inter.Elevator> SendUnique;public List<KeyValuePair<int,ElevatorPeer>> SendPeers;
    }
    static void InitializeElevatorProtocol(Harmony harmony)
    {
        InitializeReceiveAgeBridge(harmony);
        // The elevator and cinematic tokens must be removed before the integrated
        // base validator inspects its own suffix.
        foreach(var name in new[]{"BuildHelloLine","BuildWelcomeLine"})
            harmony.Patch(typeof(NetNode).GetMethod(name,Flags),postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(AdvertiseElevators),Flags)){priority=HarmonyLib.Priority.Last});
        harmony.Patch(typeof(NetNode).GetMethod("TryValidateHandshakeLine",Flags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(RequireElevators),Flags)){priority=HarmonyLib.Priority.First});
        // Optional +csfelev2 (U11 RIDE): stripped whenever this module runs, also with COOP_SYNC_DISABLE_ELEVATOR_RIDE=1
        // or after a RIDE fault trip, so a partner that advertises it always passes the integrated EndsWith validator.
        var validate=typeof(NetNode).GetMethod("TryValidateHandshakeLine",Flags);
        if(!CoopSyncFixesElevatorRide.PinMatches("NetNode","TryValidateHandshakeLine",validate?.GetMethodBody()?.GetILAsByteArray()))
            throw new InvalidOperationException("pinned IL changed: NetNode.TryValidateHandshakeLine");
        harmony.Patch(validate,prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(StripElevatorRideToken),Flags)){priority=HarmonyLib.Priority.First});
        harmony.Patch(typeof(NetNode).GetMethod("HandleLine",ElevatorInstanceFlags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ReadElevatorWire),Flags)){priority=HarmonyLib.Priority.First});
        harmony.Patch(typeof(NetNode).GetMethod("RejectIncompatiblePeer",ElevatorInstanceFlags),
            postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ObserveElevatorHandshake),Flags)));
        ElevatorMessage.Warm();   // gc-opt P04: the one-time first-use costs now, before either peer connects
    }
    static void ObserveElevatorHandshake(NetNode __instance,string expectedTag,bool __result)
    {
        if(ElevatorModuleInitialized && !__instance.IsHost && expectedTag=="WELCOME" && !__result)
            ElevatorSessions.GetValue(__instance,_=>new ElevatorSession()).WelcomeAccepted=true;
    }
    static void AdvertiseElevators(ref string __result)
    {if(ElevatorModuleInitialized)__result=__result.TrimEnd('\r','\n')+ElevatorCapability+"\n";}
    // U11: +csfelev2 is optional (PLAN-ALPHA2.md section 4 rule 7): advertised only while RIDE runs here; its absence
    // never fails a handshake, it only keeps RIDE off for that partner (the client sends RIDE only to a host that
    // advertised it; a host receives RIDE only from such a client).
    static void AdvertiseElevatorRide(ref string __result)
    {try{if(ElevatorRideReady() && __result!=null)__result=CoopSyncFixesElevatorRide.AppendToken(__result);}catch(Exception e){ElevatorRideFault(e);}}
    static void StripElevatorRideToken(ref string line)
    {try{if(ElevatorModuleInitialized)CoopSyncFixesElevatorRide.TryStripToken(ref line);}catch{ /* A strip failure leaves the line to the strict validators. */ }}
    // RejectIncompatiblePeer(line, expectedTag) passes the unstripped line (the validator's own copy is the one stripped).
    static void ObserveElevatorRideHandshake(NetNode __instance,string __0,string expectedTag,bool __result)
    {
        try{if(ElevatorModuleInitialized && __instance!=null && !__instance.IsHost && expectedTag=="WELCOME" && !__result)
            ElevatorSessions.GetValue(__instance,_=>new ElevatorSession()).RemoteRide=CoopSyncFixesElevatorRide.HasToken(__0);}
        catch(Exception e){ElevatorRideFault(e);}
    }
    static bool RequireElevators(ref string line,out int remoteProtocol,out string remoteBuild,out string failure,ref bool __result)
    {
        remoteProtocol=0;remoteBuild="unknown";failure="";
        if(!ElevatorModuleInitialized || !TryStripElevatorCapability(ref line))
        {__result=false;failure="Both peers require the explicit experimental elevator protocol";return false;}
        return true;
    }
    static bool TryStripElevatorCapability(ref string line)
    {
        var clean=(line??"").TrimEnd('\r','\n');
        var parts=clean.Split('|');int at=parts.Length==3?parts[2].IndexOf(ElevatorCapability,StringComparison.Ordinal):-1;
        // Each optional experiment removes only its own complete token. This commutes
        // with +csfcine1 regardless of which Priority.First validator runs first.
        if(at<0 ||
            parts[2].IndexOf(ElevatorCapability,at+ElevatorCapability.Length,StringComparison.Ordinal)>=0 ||
            at+ElevatorCapability.Length<parts[2].Length && parts[2][at+ElevatorCapability.Length]!='+')
            return false;
        parts[2]=parts[2].Remove(at,ElevatorCapability.Length);line=string.Join("|",parts);return true;
    }
    static bool ReadElevatorWire(NetNode __instance,string line,int? senderId,ref string forwardLine,ref bool __result)
    {
        if(line==null || !line.StartsWith(ElevatorTag,StringComparison.Ordinal))return true;
        forwardLine=null;__result=true; // Never forward an extension message through the base router.
        try
        {
            if(!CurrentElevatorNode(__instance) || line.Length>8192)return false;
            if(!TakeReceivedLineAge(__instance,line,senderId,out var receivedAt))return false;
            // ID is the causal proof that the strict handshake finished. An extension
            // packet before ID is consumed without parsing or touching game objects.
            if(!__instance.IsHost && (!ElevatorSessions.TryGetValue(__instance,out var negotiated) || !negotiated.WelcomeAccepted ||
                !ClientConnections.TryGetValue(__instance,out _) || __instance.id<=1))return false;
            if(__instance.IsHost && (!senderId.HasValue || senderId.Value<=1))return false;
            var value=ElevatorMessage.FromLine(line);   // gc-opt P04: the old expression's result for every line
            if(value==null || value.V!=1 || !Guid.TryParseExact(value.Token,"N",out _) ||
                value.BuildSerial<=0 || string.IsNullOrEmpty(value.Floor) || value.Floor.Length>1024 ||
                (__instance.IsHost ? CoopSyncFixesElevatorRide.HostKind(value.Kind,ElevatorRideReady())==CoopSyncFixesElevatorRide.HostDrop : value.Kind!="ACK" && value.Kind!="STATE"))return false;
            value.Sender=__instance.IsHost?senderId.Value:1;
            if(__instance.IsHost)
            {
                value.ReceivedConnection=ElevatorConnection(__instance,value.Sender,out _);
                if(value.ReceivedConnection==null || !HostConnections.TryGetValue(value.ReceivedConnection,out var context) || context.Nonce!=value.ConnectionNonce)return false;
            }
            else if(!ClientConnections.TryGetValue(__instance,out var context) || context.Nonce!=value.ConnectionNonce)return false;
            value.ReceivedAt=receivedAt; // Local monotonic time; never trust or serialize a peer timestamp.
            if(!FreshElevatorMessage(value,Environment.TickCount64))return false;
            var session=ElevatorSessions.GetValue(__instance,_=>new ElevatorSession());
            if(Interlocked.Increment(ref session.Queued)>256){Interlocked.Decrement(ref session.Queued);return false;}
            try{session.Incoming.Enqueue(value);}catch{Interlocked.Decrement(ref session.Queued);throw;}
        }
        catch { /* Malformed extension data cannot disconnect the underlying game session. */ }
        return false;
    }
    // gc-opt P04: the same line, written by ElevatorMessage.EncodeLine into kept buffers (one string per line) while
    // ElevatorMessage.Canonical() holds; otherwise, and for null, the old expression.
    static string ElevatorEncode(ElevatorMessage message)
    {
        if(message!=null && ElevatorMessage.Canonical())
        {
            try{return message.EncodeLine();}catch{ /* never expected: the old expression makes the line below */ }
        }
        return ElevatorTag+Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message,Formatting.None)))+"\n";
    }
    static bool CurrentElevatorNode(NetNode net) => ElevatorModuleInitialized && net!=null &&
        ReferenceEquals(net,ModEntry._net) && !RejectedConnections.TryGetValue(net,out _) && net.IsAlive;
    static bool FreshElevatorMessage(ElevatorMessage message,long now) => message.ReceivedAt>0 &&
        now>=message.ReceivedAt && now-message.ReceivedAt<=(message.Kind=="SUB"?3000:1500);
    static object ElevatorConnection(NetNode net,int id,out bool steam)
    {
        steam=false;var gate=typeof(NetNode).GetField("_clientsLock",ElevatorInstanceFlags).GetValue(net);
        lock(gate)
        {
            // gc-opt P04: the two dictionaries in the same order without a name array, a shared key box, and HandshakeComplete
            // through a typed getter of the connection's (sealed) class instead of a boxed PropertyInfo.GetValue.
            var key=CsfElevatorBoxes.Int(id);
            for(int pass=0;pass<2;pass++)
            {
                var values=(IDictionary)typeof(NetNode).GetField(pass==0?"_clients":"_steamClients",ElevatorInstanceFlags).GetValue(net);
                if(values==null || !values.Contains(key))continue;
                var connection=values[key];
                if(!(pass==0?CsfElevatorConnections.ClientReady:CsfElevatorConnections.SteamReady)(connection))continue;
                steam=pass==1;return connection;
            }
        }
        return null;
    }
    static void ElevatorSendPeer(NetNode net,int id,ElevatorPeer peer,ElevatorMessage message)
    {
        if(!CurrentElevatorNode(net))return;
        var connection=ElevatorConnection(net,id,out var steam);
        if(connection==null || !ReferenceEquals(connection,peer.Connection) || steam!=peer.Steam)return;
        if(!HostConnections.TryGetValue(connection,out var context))return;
        message.ConnectionNonce=context.Nonce;message.BuildSerial=peer.BuildSerial;
        var method=typeof(NetNode).GetMethod(steam?"SendLineToSteamClientSafe":"SendLineToClientSafe",ElevatorInstanceFlags);
        // gc-opt P04: the same MethodInfo.Invoke with a kept argument array (Invoke copies the arguments; cleared after).
        var line=ElevatorEncode(message);var arguments=CsfElevatorArgs.Take(steam?3:2);arguments[0]=connection;arguments[1]=line;
        try{method.Invoke(net,arguments);}finally{CsfElevatorArgs.Give(arguments);}
    }
    static void ElevatorSendHost(NetNode net,ElevatorMessage message)
    {
        if(!CurrentElevatorNode(net) || !ClientConnections.TryGetValue(net,out var context) || !ElevatorSessions.TryGetValue(net,out var session) || !session.WelcomeAccepted)return;
        message.ConnectionNonce=context.Nonce;
        message.BuildSerial=session.LocalBuildSerial;
        var method=typeof(NetNode).GetMethod("SendLineSafe",ElevatorInstanceFlags);
        var line=ElevatorEncode(message);var arguments=CsfElevatorArgs.Take(1);arguments[0]=line;   // gc-opt P04: kept argument array
        try{method.Invoke(net,arguments);}finally{CsfElevatorArgs.Give(arguments);}
    }

    // ---- gc-opt P04: buffers and text rules of the elevator wire line (nested Csf* holders, no outer member) ----
    // One set of buffers per thread, taken out of its slot while in use (a nested use gets a new one).
    private sealed class CsfElevatorScratch
    {
        [ThreadStatic] private static CsfElevatorScratch free;
        internal char[] Json=new char[512],Input=new char[512],Line=new char[1024],Text=new char[128];internal byte[] Bytes=new byte[1024];
        internal int JsonLength,InputLength,LineLength,ByteLength,Turn;
        internal readonly string[] Strings=new string[128];   // strings read recently on this thread, by content (two places per hash)
        private const int KeepMost=256;   // longer strings are not kept: at most 128 x 256 chars stay reachable per receiving thread
        internal static CsfElevatorScratch Take(){var s=free;free=null;return s??new CsfElevatorScratch();}
        internal static void Give(CsfElevatorScratch s){free=s;}
        // The string of Text[0..length): the kept one when a recent string has exactly these chars, else a new one.
        internal string Keep(int length)
        {
            var chars=Text.AsSpan(0,length);if(length>KeepMost)return new string(chars);   // a long string is never kept
            uint hash=2166136261u^(uint)length;
            for(int i=0;i<chars.Length;i++)hash=(hash^chars[i])*16777619u;
            int first=(int)(hash&127),second=(int)((hash>>16)&127);
            if(Holds(first,chars))return Strings[first];
            if(Holds(second,chars))return Strings[second];
            var made=new string(chars);
            if(Strings[first]==null)Strings[first]=made;else if(Strings[second]==null)Strings[second]=made;else Strings[(Turn++&1)==0?first:second]=made;
            return made;
        }
        private bool Holds(int slot,ReadOnlySpan<char> chars)=>Strings[slot]!=null && chars.SequenceEqual(Strings[slot]);
    }
    // Argument arrays for MethodInfo.Invoke, per thread, taken out while in use and given back cleared.
    private sealed class CsfElevatorArgs
    {
        [ThreadStatic] private static object[] one,two,three;
        internal static object[] Take(int count)
        {
            object[] kept;
            if(count==1){kept=one;one=null;}else if(count==2){kept=two;two=null;}else{kept=three;three=null;}
            return kept??new object[count];
        }
        internal static void Give(object[] arguments)
        {
            Array.Clear(arguments);
            if(arguments.Length==1)one=arguments;else if(arguments.Length==2)two=arguments;else three=arguments;
        }
    }
    private static class CsfElevatorJson
    {
        internal const string Version="13.0.4+4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a";   // the Newtonsoft.Json build the parity checks used
        internal static int Verdict;   // 0 not decided yet, 1 the writer and the reader agree with Newtonsoft, 2 they do not (old expressions only)
        internal static void Begin(CsfElevatorScratch s)=>s.JsonLength=0;
        internal static void End(CsfElevatorScratch s)=>Put(s,'}');
        private static void Room(CsfElevatorScratch s,int more){if(s.Json.Length-s.JsonLength<more)Array.Resize(ref s.Json,Math.Max(s.Json.Length*2,s.JsonLength+more));}
        private static void Put(CsfElevatorScratch s,char c){Room(s,1);s.Json[s.JsonLength++]=c;}
        private static void Put(CsfElevatorScratch s,string text){Room(s,text.Length);text.CopyTo(0,s.Json,s.JsonLength,text.Length);s.JsonLength+=text.Length;}
        private static char Hex(int n){n&=15;return (char)(n<=9?'0'+n:'a'+n-10);}
        // JsonTextWriter.WriteValue(int / long): the invariant digits, '-' first when negative.
        internal static void Whole(CsfElevatorScratch s,string name,long value)
        {Put(s,name);Room(s,20);value.TryFormat(s.Json.AsSpan(s.JsonLength),out var n,default,CultureInfo.InvariantCulture);s.JsonLength+=n;}
        // JsonTextWriter.WriteValue(double): JsonConvert.ToString(value, FloatFormatHandling.String, '"', false).
        internal static void Number(CsfElevatorScratch s,string name,double value)
        {
            Put(s,name);bool symbol=double.IsNaN(value) || double.IsInfinity(value);
            if(symbol)Put(s,'"');
            Room(s,32);value.TryFormat(s.Json.AsSpan(s.JsonLength),out var n,"R",CultureInfo.InvariantCulture);
            bool bare=!symbol && s.Json.AsSpan(s.JsonLength,n).IndexOfAny('.','E','e')<0;s.JsonLength+=n;
            if(symbol)Put(s,'"');else if(bare){Put(s,'.');Put(s,'0');}
        }
        // JsonTextWriter.WriteValue(string): null, or quoted with JavaScriptUtils' escapes for '"' (StringEscapeHandling.Default):
        // \t \n \r \f \b \\ \", the other chars below U+0020 and U+0085 U+2028 U+2029 as \u with lower-case hex; the rest as it is.
        internal static void Text(CsfElevatorScratch s,string name,string value)
        {
            Put(s,name);if(value==null){Put(s,"null");return;}
            Put(s,'"');
            for(int i=0;i<value.Length;i++)
            {
                char c=value[i];
                if(c<128?c>=' ' && c!='"' && c!='\\':c!='\u0085' && c!='\u2028' && c!='\u2029'){Put(s,c);continue;}
                switch(c)
                {
                    case '\t':Put(s,"\\t");break;
                    case '\n':Put(s,"\\n");break;
                    case '\r':Put(s,"\\r");break;
                    case '\f':Put(s,"\\f");break;
                    case '\b':Put(s,"\\b");break;
                    case '\\':Put(s,"\\\\");break;
                    case '"':Put(s,"\\\"");break;
                    default:Put(s,'\\');Put(s,'u');Put(s,Hex(c>>12));Put(s,Hex(c>>8));Put(s,Hex(c>>4));Put(s,Hex(c));break;
                }
            }
            Put(s,'"');
        }
        // tag + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\n" into s.Line.
        internal static void ToLine(CsfElevatorScratch s,string tag)
        {
            int most=Encoding.UTF8.GetMaxByteCount(s.JsonLength);if(s.Bytes.Length<most)s.Bytes=new byte[Math.Max(most,s.Bytes.Length*2)];
            s.ByteLength=Encoding.UTF8.GetBytes(s.Json.AsSpan(0,s.JsonLength),s.Bytes);
            int chars=(s.ByteLength+2)/3*4,length=tag.Length+chars+1;
            if(s.Line.Length<length)s.Line=new char[Math.Max(length,s.Line.Length*2)];
            tag.CopyTo(0,s.Line,0,tag.Length);
            if(!Convert.TryToBase64Chars(s.Bytes.AsSpan(0,s.ByteLength),s.Line.AsSpan(tag.Length,chars),out var written) || written!=chars)throw new InvalidOperationException("Base64 length");
            s.Line[length-1]='\n';s.LineLength=length;
        }
        internal static bool SameLine(CsfElevatorScratch s,int from,ReadOnlySpan<char> text)=>s.Line.AsSpan(from,s.LineLength-from-1).SequenceEqual(text);
        // ---- reading (lenient on purpose: whatever is read is accepted only if writing it again gives the same line) ----
        internal static bool FromBase64(CsfElevatorScratch s,ReadOnlySpan<char> text)
        {
            int most=text.Length/4*3+3;if(s.Bytes.Length<most)s.Bytes=new byte[Math.Max(most,s.Bytes.Length*2)];
            if(!Convert.TryFromBase64Chars(text,s.Bytes,out var count))return false;
            int chars=Encoding.UTF8.GetMaxCharCount(count);if(s.Input.Length<chars)s.Input=new char[Math.Max(chars,s.Input.Length*2)];
            s.InputLength=Encoding.UTF8.GetChars(s.Bytes.AsSpan(0,count),s.Input);return true;
        }
        internal static bool Expect(CsfElevatorScratch s,ref int at,string text)
        {
            if(s.InputLength-at<text.Length || !s.Input.AsSpan(at,text.Length).SequenceEqual(text))return false;
            at+=text.Length;return true;
        }
        internal static bool Whole(CsfElevatorScratch s,ref int at,out long value)
        {
            int start=at;if(at<s.InputLength && s.Input[at]=='-')at++;
            while(at<s.InputLength && (uint)(s.Input[at]-'0')<=9)at++;
            return long.TryParse(s.Input.AsSpan(start,at-start),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out value);
        }
        internal static bool Whole(CsfElevatorScratch s,ref int at,out int value)
        {
            value=0;if(!Whole(s,ref at,out long wide) || wide<int.MinValue || wide>int.MaxValue)return false;
            value=(int)wide;return true;
        }
        // As JsonTextReader.ReadAsDouble: a number with double.TryParse(Float, invariant), a quoted one (NaN, Infinity,
        // -Infinity) with double.TryParse(Float | AllowThousands, invariant).
        internal static bool Number(CsfElevatorScratch s,ref int at,out double value)
        {
            value=0;
            if(at<s.InputLength && s.Input[at]=='"')
            {
                int from=at+1,to=from;while(to<s.InputLength && s.Input[to]!='"')to++;
                if(to>=s.InputLength || !double.TryParse(s.Input.AsSpan(from,to-from),NumberStyles.Float|NumberStyles.AllowThousands,CultureInfo.InvariantCulture,out value))return false;
                at=to+1;return true;
            }
            int start=at;while(at<s.InputLength && "+-.0123456789eE".IndexOf(s.Input[at])>=0)at++;
            return double.TryParse(s.Input.AsSpan(start,at-start),NumberStyles.Float,CultureInfo.InvariantCulture,out value);
        }
        internal static bool Text(CsfElevatorScratch s,ref int at,out string value)
        {
            value=null;
            if(Expect(s,ref at,"null"))return true;
            if(at>=s.InputLength || s.Input[at]!='"')return false;
            at++;int length=0;
            while(true)
            {
                if(at>=s.InputLength)return false;
                char c=s.Input[at++];
                if(c=='"')break;
                if(c=='\\')
                {
                    if(at>=s.InputLength)return false;
                    c=s.Input[at++];
                    switch(c)
                    {
                        case '"':case '\\':case '/':break;
                        case 'b':c='\b';break;
                        case 'f':c='\f';break;
                        case 'n':c='\n';break;
                        case 'r':c='\r';break;
                        case 't':c='\t';break;
                        case 'u':
                            if(s.InputLength-at<4 || !ushort.TryParse(s.Input.AsSpan(at,4),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out var code))return false;
                            c=(char)code;at+=4;break;
                        default:return false;
                    }
                }
                if(length==s.Text.Length)Array.Resize(ref s.Text,s.Text.Length*2);
                s.Text[length++]=c;
            }
            value=s.Keep(length);return true;
        }
    }
}
