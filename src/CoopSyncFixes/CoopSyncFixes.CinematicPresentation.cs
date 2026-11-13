using System;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using dc.pr;
using dc.libs.heaps.slib;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using HarmonyLib;
using HaxeProxy.Runtime;
using ModCore.Utilities;

public sealed partial class CoopSyncFixes
{
    // Enabled by the integrated entry point before any network connection.
    // This module never advances Weapon, changes readiness, or creates a ghost.
    private const string CineAnimTag="CSFCINEANIM1|";
    private const string CineCapability="+csfcine1";
    private static bool CineModuleInitialized;
    private sealed class CineHandshake {public bool WelcomeAccepted;}
    private static readonly ConditionalWeakTable<NetNode,CineHandshake> CineHandshakes=new();
    private sealed class CineVisualAnimation
    {public IntPtr Level,Cine;public long Expires;public int Sender;public string RemoteLevel,Animation;public int? Queue;public bool? Flag;}
    private static readonly ConditionalWeakTable<NetNode,List<CineVisualAnimation>> CineVisualQueues=new();
    private static readonly BindingFlags CineInstance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly Type CineWeaponSupport=typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.Ghost.KingWeaponSupport",true);
    // This partial's initializer runs before the shared readonly Flags field.
    // Use a constant mask so the native animation filter cannot become null.
    private const BindingFlags CineStatic=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly MethodInfo CineUnsafeAnim=CineWeaponSupport.GetMethod("IsUnsafeRemoteGhostAnimation",CineStatic);
    private static long CinePresentationFrames,CinePresentationPositions,CinePresentationAnimations,CinePresentationSent,CinePresentationReceived;
    // Alpha2 U9 (PLAN-ALPHA2.md issue 2 step 4 and issue 6 R1). Switches: COOP_SYNC_DISABLE_CINEMATIC_BRANCH,
    // COOP_SYNC_DISABLE_CINEMATIC_HEADS. No initializer here reads the shared Flags field (see above).
    private static bool CineBranchOff,CineHeadsOff,CineBranchApiLooked;
    private static Func<bool> CineBranchActive;
    private static Func<int,string,int,bool,bool> CineBranchHide;
    private static readonly Dictionary<int,bool> CineBranchSkipped=new();
    private static readonly List<GhostKing> CinePresentedGhosts=new();
    private static long CinePresentationBranchSkips,CinePresentationHeads;

    private static void InitializeCinematicPresentation(Harmony harmony)
    {
        if(CineModuleInitialized || Disabled("CINEMATIC_PRESENTATION"))return;
        CineBranchOff=Disabled("CINEMATIC_BRANCH");CineHeadsOff=Disabled("CINEMATIC_HEADS");
        if(CineUnsafeAnim==null)throw new MissingMethodException("Pinned cinematic animation safety filter is unavailable");
        if(ModEntry._net?.IsAlive==true)throw new InvalidOperationException("Initialize cinematic capability before connecting peers");
        foreach(var name in new[]{"BuildHelloLine","BuildWelcomeLine"})
            harmony.Patch(typeof(NetNode).GetMethod(name,Flags),postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(AdvertiseCinematicPresentation),Flags)){priority=HarmonyLib.Priority.Last});
        harmony.Patch(typeof(NetNode).GetMethod("TryValidateHandshakeLine",Flags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(RequireCinematicPresentation),Flags)){priority=HarmonyLib.Priority.First});
        harmony.Patch(typeof(NetNode).GetMethod("RejectIncompatiblePeer",CineInstance),
            postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ObserveCinematicHandshake),Flags)));
        harmony.Patch(typeof(ModEntry).GetMethod("OnFrameUpdate",CineInstance),
            postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(TickCinematicPresentation),Flags)));
        harmony.Patch(typeof(ModEntry).GetMethod("Hook_AnimManager_play",CineInstance),
            postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(SendCinematicAttackAnimation),Flags)));
        harmony.Patch(typeof(NetNode).GetMethod("HandleLine",CineInstance),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ReadCinematicAttackAnimation),Flags)));
        InitializeCinematicAttackExpiry(harmony);
        InitializeCineVisualManagers(harmony);
        CineModuleInitialized=true;
    }

    private static void AdvertiseCinematicPresentation(ref string __result)
    {if(CineModuleInitialized)__result=__result.TrimEnd('\r','\n')+CineCapability+"\n";}
    private static bool RequireCinematicPresentation(ref string line,out int remoteProtocol,out string remoteBuild,out string failure,ref bool __result)
    {
        remoteProtocol=0;remoteBuild="unknown";failure="";
        if(!CineModuleInitialized)return true;
        var parts=(line??"").TrimEnd('\r','\n').Split('|');
        var build=parts.Length==3?parts[2]:"";var at=build.IndexOf(CineCapability,StringComparison.Ordinal);
        if(at<0 || build.IndexOf(CineCapability,at+CineCapability.Length,StringComparison.Ordinal)>=0 ||
            at+CineCapability.Length<build.Length && build[at+CineCapability.Length]!='+'){
            __result=false;failure="Both players require the cinematic presentation capability";return false;
        }
        // Remove only our exact capability. The elevator validator independently
        // strips +csfelev1 in either order; the integrated base capability then validates normally.
        parts[2]=build.Remove(at,CineCapability.Length);line=string.Join("|",parts);return true;
    }
    private static void ObserveCinematicHandshake(NetNode __instance,string expectedTag,bool __result)
    {
        if(CineModuleInitialized && !__instance.IsHost && expectedTag=="WELCOME" && !__result)
            CineHandshakes.GetValue(__instance,_=>new CineHandshake()).WelcomeAccepted=true;
    }
    private static List<(int Id,object Connection,bool Steam,string Nonce)> CinematicPeers(NetNode net)
    {
        var result=new List<(int,object,bool,string)>();
        if(net==null || RejectedConnections.TryGetValue(net,out _))return result;
        var gate=typeof(NetNode).GetField("_clientsLock",CineInstance).GetValue(net);
        lock(gate)foreach(var field in new[]{"_clients","_steamClients"}){
            var peers=(IDictionary)typeof(NetNode).GetField(field,CineInstance).GetValue(net);if(peers==null)continue;
            foreach(DictionaryEntry peer in peers){var connection=peer.Value;
                if(connection==null || !(bool)connection.GetType().GetProperty("HandshakeComplete").GetValue(connection) ||
                    !HostConnections.TryGetValue(connection,out var context))continue;
                result.Add(((int)peer.Key,connection,field=="_steamClients",context.Nonce));
            }
        }
        return result;
    }
    private static void SendCinematicPeer(NetNode net,(int Id,object Connection,bool Steam,string Nonce) peer,string[] fields)
    {
        // Validate incarnation after releasing _clientsLock, and target only that
        // completed connection. Never broadcast extension tags to incomplete peers.
        var current=CinematicPeers(net).Find(p=>p.Id==peer.Id && ReferenceEquals(p.Connection,peer.Connection));
        if(current.Connection==null || current.Nonce!=peer.Nonce)return;
        fields[2]=peer.Nonce;var line=string.Join("|",fields)+"\n";
        var method=typeof(NetNode).GetMethod(peer.Steam?"SendLineToSteamClientSafe":"SendLineToClientSafe",CineInstance);
        method.Invoke(net,peer.Steam?new object[]{peer.Connection,line,null}:new object[]{peer.Connection,line});CinePresentationSent++;
    }

    private static bool IsPresentationCine(string name)=>name=="dc.cine.HealFountain" || name=="dc.cine.MeetCollector" ||
        name=="dc.cine.MeetCollectorIntern" || name=="dc.cine.MeetScribe" || name=="dc.cine.MeetBlacksmith" ||
        name=="dc.cine.MeetSmallBS" || name=="dc.cine.TailorDaughterCine";

    private static bool CinematicPresentationContext(out Game game,out Level level)
    {
        game=Game.Class.ME;level=game?.hero?._level;var net=ModEntry._net;
        if(!CineModuleInitialized || net==null || RejectedConnections.TryGetValue(net,out _) || !net.IsAlive || !net.HasRemote || game==null || game.destroyed || level==null || level.destroyed ||
            level.map==null || !level.map.initDone || level.isSubLevel || level.isSecret || game.hero.destroyed || !game.hero.awake || !game.hero.initDone ||
            game.hero.controller?.parent==null || game.hero.cd?.fastCheck==null || game.curCine==null ||
            game.curCine.destroyed || !IsPresentationCine(game.curCine.GetType().FullName))return false;
        if(!(bool)Lobby.GetMethod("IsInActualRun",Flags).Invoke(null,null) ||
            (bool)typeof(ModEntry).GetMethod("IsLocalPlayerDowned",Flags).Invoke(null,null) ||
            ModEntry.me?.HashlinkPointer!=game.hero.HashlinkPointer || level.game?.HashlinkPointer!=game.HashlinkPointer ||
            game.curLevel?.HashlinkPointer!=level.HashlinkPointer || CineRun(net)==null)return false;
        // MobsSynchronization intentionally retains its previous playable level in
        // stable T_* collector/healing rooms. Its mob-world pointer cannot identify
        // the actual interactive scene. Bind presentation to the initialized native
        // Game/Hero/Level and accepted run; packets/ghosts remain independently scoped.
        foreach(var name in new[]{"IsRemoteKingTransitionActive","IsRemoteKingCreationBlocked"})
            if((bool)(typeof(ModEntry).GetProperty(name,Flags)?.GetValue(null)??false))return false;
        // Some interactive cinematics expose their owner. Never present through
        // a cinematic owned by a different Hero, or a transition/corpse cinematic.
        if(game.curCine.GetType().GetProperty("hero",CineInstance)?.GetValue(game.curCine) is dc.en.Hero owner &&
            owner.HashlinkPointer!=game.hero.HashlinkPointer)return false;
        return true;
    }

    private static GhostKing PresentationGhost(NetNode net,Level level,int remoteId,string remoteLevel)
    {
        if(remoteId<=0 || remoteId==net.id || remoteLevel!=level.map.id.ToString() ||
            (bool)typeof(ModEntry).GetMethod("IsRemotePlayerDowned",Flags).Invoke(null,new object[]{remoteId}))return null;
        var args=new object[]{net.id,remoteId,0};
        if(!(bool)typeof(ModEntry).GetMethod("TryGetClientIndex",Flags).Invoke(null,args))return null;
        var ghost=ModEntry.clients[(int)args[2]];
        return ghost!=null && !ghost.destroyed && ghost.life>0 && ghost._level?.HashlinkPointer==level.HashlinkPointer &&
            ghost.spr!=null && !ghost.spr.destroyed?ghost:null;
    }

    private static bool PlayPresentationSprite(HSprite sprite,string animation,int? queue,bool? flag,bool force)
    {
        if(sprite==null || sprite.destroyed || sprite._animManager==null || string.IsNullOrWhiteSpace(animation) || animation.Length>128 ||
            (bool)CineUnsafeAnim.Invoke(null,new object[]{animation}))return false;
        var name=animation.AsHaxeString();if(sprite.lib?.groups?.exists(name)!=true)return false;
        if(!force && sprite.groupName?.ToString()==animation)return false;
        bool loop=(bool)typeof(ModEntry).GetMethod("ShouldLoopRemoteAnim",Flags).Invoke(null,new object[]{animation});
        // Generated optional-argument signatures reference runtime CoreLib;
        // bridge nullable arguments by reflection instead of facade mixing.
        var visual=GetCineVisualManager(sprite);if(visual==null)return false;
        var playback=(AnimManager)typeof(AnimManager).GetMethod("play").Invoke(visual,new object[]{name,queue,flag});
        if(loop)typeof(AnimManager).GetMethod("loop").Invoke(playback,new object[]{null});
        else playback.stopOnLastFrame(Ref<bool>.Null);
        CinePresentationAnimations++;return true;
    }

    private static void TickCinematicPresentation()
    {
        try {
            if(!CinematicPresentationContext(out var game,out var level)){
                if(ModEntry._net!=null)CineVisualQueues.Remove(ModEntry._net);CineBranchSkipped.Clear();return;
            }
            CinePresentationFrames++;var net=ModEntry._net;CinePresentedGhosts.Clear();
            if(net.TryConsumeRemoteSnapshot(out var snapshots)){
            try {foreach(var remote in snapshots){
                // A partner in a sub-level or secret room of this map is not presented here (issue 2).
                if(CinematicRemoteInOtherBranch(level,remote.Id,remote.RoomLevelId,remote.RoomId,remote.HasRoom))continue;
                var ghost=PresentationGhost(net,level,remote.Id,remote.LevelId);
                if(ghost==null || !double.IsFinite(remote.X) || !double.IsFinite(remote.Y))continue;
                ghost.setPosPixel(remote.X,remote.Y);ghost.dir=remote.Dir;CinePresentationPositions++;
                if(remote.HasAnim)PlayPresentationSprite(ghost.spr,remote.Anim,remote.AnimQueue,remote.AnimG,false);
                if(remote.HasHeadAnim)PlayPresentationSprite(ghost.head?.customHeadSpr,remote.HeadAnim,null,null,false);
                CinePresentedGhosts.Add(ghost);
            }}finally {NetNode.ReleaseConsumedList(snapshots);}
            }
            PresentCinematicHeads(game);
            // Ordered HandleLine callbacks run in the frame's network pump.
            // Apply fresh attack visuals after snapshot locomotion, so an older
            // pending idle ANIM cannot erase a newly arrived swing in this frame.
            if(CineVisualQueues.TryGetValue(net,out var events)){
                try {foreach(var visual in events){
                    if(visual.Level!=level.HashlinkPointer || visual.Cine!=game.curCine.HashlinkPointer || visual.Expires<Environment.TickCount64)continue;
                    if(CineBranchSkipped.TryGetValue(visual.Sender,out var otherBranch) && otherBranch)continue;
                    var ghost=PresentationGhost(net,level,visual.Sender,visual.RemoteLevel);
                    if(ghost!=null)PlayPresentationSprite(ghost.spr,visual.Animation,visual.Queue,visual.Flag,true);
                }}finally {events.Clear();}
            }
        } catch(Exception error){Serilog.Log.Warning(error,"[CoopSyncFixes] Cinematic presentation frame skipped");}
    }

    // Issue 2 (PLAN-ALPHA2.md step 4): a sub-level shares LevelMap.id with its parent, so the level test of PresentationGhost
    // alone would move the partner to sub-level coordinates inside this map. With CoopBranchVisibilityFix present and active
    // its decision is used (same 0.4 s rule as the base path; when it hides, the base's own walkOut/dispose runs, as
    // ReceiveGhostCoords would run it). Without it: this presenter runs only on a main map (CinematicPresentationContext), whose
    // ROOM token is 0 for every peer version, so a partner whose ROOM marker for this level is non-zero is not presented.
    private static bool CinematicRemoteInOtherBranch(Level level,int remoteId,string roomLevel,int? roomId,bool hasRoom)
    {
        if(CineBranchOff)return false;
        bool skip;
        BindCinematicBranchApi();
        if(CineBranchHide!=null && CineBranchActive())skip=CineBranchHide(remoteId,roomLevel,roomId??-1,hasRoom);
        else skip=hasRoom && roomId.HasValue && roomId.Value!=0 && roomLevel==level.map.id.ToString();
        CineBranchSkipped[remoteId]=skip;
        if(skip)CinePresentationBranchSkips++;
        return skip;
    }
    private static void BindCinematicBranchApi()
    {
        if(CineBranchApiLooked)return;
        CineBranchApiLooked=true;
        try {
            Type api=null;int found=0;
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies()){
                Type type=null;try {type=assembly.GetType("CoopBranchVisibilityApi",false);} catch {}
                if(type!=null){api=type;found++;}
            }
            var hide=found==1?api.GetMethod("PresentationHide",CineStatic,null,new[]{typeof(int),typeof(string),typeof(int),typeof(bool)},null):null;
            var active=found==1?api.GetProperty("Active",CineStatic)?.GetGetMethod():null;
            if(hide?.ReturnType==typeof(bool) && active?.ReturnType==typeof(bool)){
                CineBranchHide=(Func<int,string,int,bool,bool>)Delegate.CreateDelegate(typeof(Func<int,string,int,bool,bool>),hide);
                CineBranchActive=(Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),active);
            }
            Serilog.Log.Information("[CoopSyncFixes] Cinematic presenter room check uses {Source}",CineBranchHide!=null?"CoopBranchVisibilityFix":"the ROOM marker only");
        } catch(Exception error){CineBranchHide=null;CineBranchActive=null;Serilog.Log.Warning(error,"[CoopSyncFixes] Cinematic presenter room check uses the ROOM marker only");}
    }
    // Issue 6 R1: during an admitted presentation the base mod's hero tick, and with it UpdateGhostHeads, is gated off by
    // Game.curCine, so the presented partner's head froze at a stale offset while his body moved. Only Kinghead.updateHeadFx
    // runs here, on the head the base mod built for that avatar (never ModEntry.UpdateGhostHeads: it can rebuild heads in the
    // middle of a scene). Kinghead swaps the local hero's lastHeadPos and visible and restores them in a finally
    // (Kinghead.cs:281-314); both are checked after every call, restored if changed, and the head path then stops.
    private static void PresentCinematicHeads(Game game)
    {
        if(CineHeadsOff || CinePresentedGhosts.Count==0)return;
        var hero=game.hero;
        foreach(var ghost in CinePresentedGhosts){
            var head=PresentedHead(ghost);if(head==null)continue;
            bool visible=hero.visible;var last=hero.lastHeadPos;var lastPointer=last?.HashlinkPointer??IntPtr.Zero;
            try {head.updateHeadFx(game.ftime);CinePresentationHeads++;}
            catch(Exception error){CineHeadsOff=true;Serilog.Log.Warning(error,"[CoopSyncFixes] Cinematic partner head update switched off for this session");return;}
            if(hero.visible!=visible || (hero.lastHeadPos?.HashlinkPointer??IntPtr.Zero)!=lastPointer){
                hero.visible=visible;hero.lastHeadPos=last;CineHeadsOff=true;
                Serilog.Log.Warning("[CoopSyncFixes] Cinematic partner head update changed the local hero; restored and switched off for this session");return;
            }
        }
    }
    private static DeadCellsMultiplayerMod.KingHead.Kinghead PresentedHead(GhostKing ghost)
    {
        var clients=ModEntry.clients;var heads=ModEntry.clientHeads;
        for(int i=0;i<clients.Length && i<heads.Length;i++)
            if(clients[i]!=null && clients[i].HashlinkPointer==ghost.HashlinkPointer && heads[i]!=null && heads[i].HashlinkPointer!=IntPtr.Zero)return heads[i];
        return null;
    }

    private static object CineRun(NetNode net)=>Coordinator.GetMethod(net.IsHost?"GetCurrentHostDescriptor":"GetCurrentRemoteDescriptor",Flags).Invoke(null,null);
    private static object CineProperty(object value,string name)=>value?.GetType().GetProperty(name)?.GetValue(value);
    private static string CineEncode(string value)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static void SendCinematicAttackAnimation(AnimManager self,dc.String plays,int? queueAnim,bool? g)
    {
        try {
            var net=ModEntry._net;var hero=Game.Class.ME?.hero;
            if(!CineModuleInitialized || net==null || RejectedConnections.TryGetValue(net,out _) || !net.IsAlive || !net.HasRemote || hero==null || hero.destroyed || hero.life<=0 ||
                hero.spr?._animManager?.HashlinkPointer!=self?.HashlinkPointer ||
                (bool)typeof(ModEntry).GetMethod("IsLocalPlayerDowned",Flags).Invoke(null,null) ||
                (bool)CineWeaponSupport.GetProperty("IsInKingContext",Flags).GetValue(null))return;
            var animation=plays?.ToString();
            if(string.IsNullOrEmpty(animation) || animation.Length>128 || !(bool)typeof(ModEntry).GetMethod("IsAttackAnim",Flags).Invoke(null,new object[]{animation}) ||
                (bool)CineUnsafeAnim.Invoke(null,new object[]{animation}))return;
            var run=CineRun(net);if(run==null || hero._level?.map==null)return;
            var line=CineAnimTag+net.id+"|-|"+((Guid)CineProperty(run,"SessionId")).ToString("N")+"|"+
                ((long)CineProperty(run,"RunId")).ToString(CultureInfo.InvariantCulture)+"|"+
                ((int)CineProperty(run,"Sequence")).ToString(CultureInfo.InvariantCulture)+"|"+
                CineEncode(hero._level.map.id.ToString())+"|"+CineEncode(animation)+"|"+
                (queueAnim?.ToString(CultureInfo.InvariantCulture)??"-")+"|"+(g.HasValue?(g.Value?"1":"0"):"-");
            if(net.IsHost){var fields=line.Split('|');foreach(var peer in CinematicPeers(net))SendCinematicPeer(net,peer,fields);}
            else if(CineHandshakes.TryGetValue(net,out var handshake) && handshake.WelcomeAccepted && ClientConnections.TryGetValue(net,out var context)){
                var fields=line.Split('|');fields[2]=context.Nonce;
                typeof(NetNode).GetMethod("SendLineSafe",CineInstance).Invoke(net,new object[]{string.Join("|",fields)+"\n"});CinePresentationSent++;
            }
        } catch(Exception error){Serilog.Log.Warning(error,"[CoopSyncFixes] Cinematic attack animation skipped");}
    }

    private static bool ReadCinematicAttackAnimation(NetNode __instance,string line,int? senderId,ref string forwardLine,ref bool __result)
    {
        if(line?.StartsWith(CineAnimTag,StringComparison.Ordinal)!=true)return true;
        __result=true;forwardLine=null;
        // An earlier prefix may consume extension lines before the common join
        // guard runs. Preserve its terminal rejection here as well.
        if(RejectedConnections.TryGetValue(__instance,out _)){__result=false;return false;}
        try {
            if(!CineModuleInitialized || !ReferenceEquals(__instance,ModEntry._net) || !__instance.IsAlive || line.Length>2048)return false;
            var parts=line.TrimEnd('\r','\n').Split('|');
            if(parts.Length!=10 || !int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var id) || id<=0 || id==__instance.id ||
                __instance.IsHost && (!senderId.HasValue || senderId.Value!=id) ||
                !Guid.TryParseExact(parts[2],"N",out _) || !Guid.TryParseExact(parts[3],"N",out var session) ||
                !long.TryParse(parts[4],NumberStyles.None,CultureInfo.InvariantCulture,out var runId) ||
                !int.TryParse(parts[5],NumberStyles.None,CultureInfo.InvariantCulture,out var sequence))return false;
            if(__instance.IsHost){
                if(!CinematicPeers(__instance).Exists(p=>p.Id==id && p.Nonce==parts[2]))return false;
            } else if(!CineHandshakes.TryGetValue(__instance,out var handshake) || !handshake.WelcomeAccepted ||
                !ClientConnections.TryGetValue(__instance,out var context) || context.Nonce!=parts[2])return false;
            var run=CineRun(__instance);
            if(run==null || session!=(Guid)CineProperty(run,"SessionId") || runId!=(long)CineProperty(run,"RunId") || sequence!=(int)CineProperty(run,"Sequence"))return false;
            var remoteLevel=Encoding.UTF8.GetString(Convert.FromBase64String(parts[6]));
            var animation=Encoding.UTF8.GetString(Convert.FromBase64String(parts[7]));
            if(remoteLevel.Length<1 || remoteLevel.Length>128 || animation.Length<1 || animation.Length>128)return false;
            int? queue=null;bool? flag=null;
            if(parts[8]!="-"){if(!int.TryParse(parts[8],NumberStyles.Integer,CultureInfo.InvariantCulture,out var parsed))return false;queue=parsed;}
            if(parts[9]!="-"){if(parts[9]!="0" && parts[9]!="1")return false;flag=parts[9]=="1";}
            if(__instance.IsHost)foreach(var peer in CinematicPeers(__instance))if(peer.Id!=id)SendCinematicPeer(__instance,peer,parts);
            CinePresentationReceived++;
            if(CinematicPresentationContext(out var game,out var level)){
                var ghost=PresentationGhost(__instance,level,id,remoteLevel);
                if(ghost!=null){
                    var events=CineVisualQueues.GetValue(__instance,_=>new List<CineVisualAnimation>());
                    if(events.Count>=64)events.RemoveAt(0);
                    events.Add(new CineVisualAnimation {Level=level.HashlinkPointer,Cine=game.curCine.HashlinkPointer,
                        Expires=Environment.TickCount64+250,Sender=id,RemoteLevel=remoteLevel,Animation=animation,Queue=queue,Flag=flag});
                }
            }
        } catch(Exception error){Serilog.Log.Warning(error,"[CoopSyncFixes] Cinematic animation message rejected");}
        return false;
    }
}
