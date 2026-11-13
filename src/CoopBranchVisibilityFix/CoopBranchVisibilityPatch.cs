using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using dc.pr;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Hashlink.Virtuals;
using R=CoopBranchVisibilityRules;

// CoopBranchVisibilityFix (Alpha2 U9): hide the partner while he is in another room/sub-level of the same map
// (PLAN-ALPHA2.md issue 2 steps 1-3 and 5; OWNER-DECISIONS OD3 and Q4), and the R3 head fix of issue 6.
//   1. Postfix on ModEntry.ComputeLevelBranchToken(Level,string) (IL:36748): a sub-level or secret level gets a stable token
//      (CoopBranchVisibilityRules.Key/Token) instead of its index in Game.subLevels; the main map keeps 0. The base sends it
//      in its existing ROOM line (SendCurrentRoomTarget, every hero frame and at every transition), so no new wire tag.
//   2. Postfix on ModEntry.ShouldKeepRemoteKingVisibleInRoom(RemoteSnapshot,string) (IL:38061): only when the original kept
//      the avatar, hide it (result false) after the two players' branch tokens have differed for 0.4 s; show it again as soon
//      as they match. The base then plays walkOut and disposes the shell (QueueClientDisposeWithTransition) and re-creates
//      it when the branches match again. The downed and revive-grace exceptions of the original are kept only when the
//      branches match (the postfix runs after them).
//   3. The same branch test on the downed corpse: a transpiler adds "and not hidden" to the level test of
//      ApplyRemoteDownedGhostPositions (IL:22046, corpse cinematic and shell) and FindNearestReviveTarget (IL:25126, revive
//      hold): a corpse in another branch is neither drawn nor revivable from here (Q4).
//   5. A minimap marker (CoopBranchVisibilityMarker) at the last cell the partner occupied on this map.
//   R3. Postfix on Kinghead.UpdateHeadFxWithKingContext (IL:155203): show the flame batches again once the ghost is visible.
// Local presentation only: nothing new goes on the wire; the ROOM value is the base's own line.
// Every body: try/catch behind a fault gate (8 consecutive / 64 total, as PartGate); kill switch COOP_BRANCH_VISIBILITY_DISABLE
// (and COOP_BRANCH_VISIBILITY_MARKER_DISABLE / _HEAD_DISABLE / _CORPSE_DISABLE for the sub-features).
public static class CoopBranchVisibilityPatch
{
    public const string Owner="CoopBranchVisibilityFix.Branch.20261112";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    delegate bool ClientIndexFn(int localId,int remoteId,out int index);
    static readonly object InstallGate=new();
    static volatile bool Published;
    public static bool Disabled{get;private set;}
    public static bool MarkerDisabled{get;private set;}
    public static bool HeadDisabled{get;private set;}
    public static bool CorpseDisabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static bool Ready=>Published;
    internal static readonly R.Gate VisibilityGate=new(),HeadGate=new();
    public static bool Active=>Published&&!Disabled&&!VisibilityGate.Tripped;

    static FieldInfo MarkerToken,MarkerLevel,DownedUserId,KingField;
    static Action<ModEntry,int> QueueDispose;
    static ClientIndexFn ClientIndex;
    static MethodInfo TokenTarget,VisibleTarget,ApplyDownedTarget,FindReviveTarget,KingFxTarget,FrameTarget;
    static Type DownedStateType;

    // ---- state (game thread) ----
    sealed class RoomSeen{public string Level;public int Token=-1;public bool HasRoom;public long At;}
    static readonly Dictionary<int,R.Track> Tracks=new();
    static readonly Dictionary<int,RoomSeen> Rooms=new();
    static object Session;
    static IntPtr TokenLevelPtr;static int TokenLevelUid;static int TokenValue;static string TokenKey;static bool TokenCached;
    static IntPtr LoggedLevel;static int LoggedToken=int.MinValue;
    static readonly R.LogBudget Budget=new(400,Stopwatch.Frequency);
    // Events that repeat every frame while they hold (corpse/revive filter, head restore): one line per key per minute.
    static readonly R.LogBudget RepeatBudget=new(120,Stopwatch.Frequency*60);
    static long HiddenDecisions,ShownDecisions,HiddenFrames,CorpseFiltered,ReviveFiltered,HeadRestores,PresentationHides;
    public static long HiddenCount=>HiddenDecisions;
    public static long ShownCount=>ShownDecisions;
    public static long HiddenFrameCount=>HiddenFrames;
    public static long CorpseFilterCount=>CorpseFiltered;
    public static long ReviveFilterCount=>ReviveFiltered;
    public static long HeadRestoreCount=>HeadRestores;
    public static long PresentationHideCount=>PresentationHides;
    public static long Faults=>VisibilityGate.Faults+HeadGate.Faults+CoopBranchVisibilityMarker.Gate.Faults;

    public static void Install()
    {
        lock(InstallGate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier branch visibility initialization failed: "+InitializationError);
            Harmony harmony=null;var targets=new List<MethodBase>();
            try
            {
                var mod=typeof(ModEntry);
                CoopBranchVisibilityPins.RequireExactBinaries(mod.Assembly,typeof(dc.en.Hero).Assembly);
                CoopBranchVisibilityPins.VerifySymbolic();
                TokenTarget=CoopBranchVisibilityPins.Base(mod,"ComputeLevelBranchToken",false,new[]{typeof(Level),typeof(string)},typeof(int),CoopBranchVisibilityPins.ComputeLevelBranchTokenIl,CoopBranchVisibilityPins.ComputeLevelBranchTokenSha);
                VisibleTarget=CoopBranchVisibilityPins.Base(mod,"ShouldKeepRemoteKingVisibleInRoom",false,new[]{typeof(NetNode.RemoteSnapshot),typeof(string)},typeof(bool),CoopBranchVisibilityPins.ShouldKeepVisibleIl,CoopBranchVisibilityPins.ShouldKeepVisibleSha);
                ApplyDownedTarget=CoopBranchVisibilityPins.Base(mod,"ApplyRemoteDownedGhostPositions",false,new[]{typeof(NetNode)},typeof(void),CoopBranchVisibilityPins.ApplyDownedIl,CoopBranchVisibilityPins.ApplyDownedSha);
                var downed=CoopBranchVisibilityPins.BaseField(mod,"_remoteDowned",null,false);
                if(!downed.FieldType.IsGenericType||downed.FieldType.GetGenericTypeDefinition()!=typeof(Dictionary<,>)||downed.FieldType.GetGenericArguments()[0]!=typeof(int))
                    throw new InvalidOperationException("Pinned downed table shape changed");
                DownedStateType=downed.FieldType.GetGenericArguments()[1];
                if(DownedStateType.IsValueType)throw new InvalidOperationException("Pinned downed state is not a class");
                DownedUserId=CoopBranchVisibilityPins.BaseField(DownedStateType,"UserId",typeof(int),false);
                CoopBranchVisibilityPins.BaseField(DownedStateType,"LevelId",typeof(string),false);
                FindReviveTarget=CoopBranchVisibilityPins.Base(mod,"FindNearestReviveTarget",false,Type.EmptyTypes,DownedStateType,CoopBranchVisibilityPins.FindReviveIl,CoopBranchVisibilityPins.FindReviveSha);
                var king=mod.Assembly.GetType("DeadCellsMultiplayerMod.KingHead.Kinghead",true);
                KingFxTarget=CoopBranchVisibilityPins.Base(king,"UpdateHeadFxWithKingContext",false,new[]{typeof(double)},typeof(void),CoopBranchVisibilityPins.KingHeadFxIl,CoopBranchVisibilityPins.KingHeadFxSha);
                KingField=CoopBranchVisibilityPins.BaseField(king,"king",null,false);
                if(!typeof(dc.Entity).IsAssignableFrom(KingField.FieldType))throw new InvalidOperationException("Pinned Kinghead.king is not an entity");
                FrameTarget=CoopBranchVisibilityPins.Base(mod,"OnFrameUpdate",false,new[]{typeof(double)},typeof(void),CoopBranchVisibilityPins.OnFrameUpdateIl,CoopBranchVisibilityPins.OnFrameUpdateSha);
                var queue=CoopBranchVisibilityPins.Base(mod,"QueueClientDisposeWithTransition",false,new[]{typeof(int)},typeof(void),CoopBranchVisibilityPins.QueueDisposeIl,CoopBranchVisibilityPins.QueueDisposeSha);
                QueueDispose=(Action<ModEntry,int>)Delegate.CreateDelegate(typeof(Action<ModEntry,int>),queue);
                var index=CoopBranchVisibilityPins.Base(mod,"TryGetClientIndex",true,new[]{typeof(int),typeof(int),typeof(int).MakeByRefType()},typeof(bool),CoopBranchVisibilityPins.TryGetClientIndexIl,CoopBranchVisibilityPins.TryGetClientIndexSha);
                ClientIndex=(ClientIndexFn)Delegate.CreateDelegate(typeof(ClientIndexFn),index);
                MarkerToken=CoopBranchVisibilityPins.BaseField(mod,"_localLastDoorMarkerToken",typeof(int),false);
                MarkerLevel=CoopBranchVisibilityPins.BaseField(mod,"_localLastDoorMarkerLevelId",typeof(string),false);
                CoopBranchVisibilityMarker.Bind();
                Disabled=Environment.GetEnvironmentVariable("COOP_BRANCH_VISIBILITY_DISABLE")=="1";
                MarkerDisabled=Environment.GetEnvironmentVariable("COOP_BRANCH_VISIBILITY_MARKER_DISABLE")=="1";
                HeadDisabled=Environment.GetEnvironmentVariable("COOP_BRANCH_VISIBILITY_HEAD_DISABLE")=="1";
                CorpseDisabled=Environment.GetEnvironmentVariable("COOP_BRANCH_VISIBILITY_CORPSE_DISABLE")=="1";
                harmony=new Harmony(Owner);
                void Patch(MethodBase target,string prefix,string postfix,string transpiler,int priority)
                {
                    targets.Add(target);
                    harmony.Patch(target,
                        prefix:prefix==null?null:new HarmonyMethod(typeof(CoopBranchVisibilityPatch).GetMethod(prefix,S)){priority=priority},
                        postfix:postfix==null?null:new HarmonyMethod(typeof(CoopBranchVisibilityPatch).GetMethod(postfix,S)){priority=priority},
                        transpiler:transpiler==null?null:new HarmonyMethod(typeof(CoopBranchVisibilityPatch).GetMethod(transpiler,S)));
                    var info=Harmony.GetPatchInfo(target);
                    if(info==null||info.Prefixes.Count(p=>p.owner==Owner)!=(prefix==null?0:1)||info.Postfixes.Count(p=>p.owner==Owner)!=(postfix==null?0:1)||
                       info.Transpilers.Count(p=>p.owner==Owner)!=(transpiler==null?0:1)||info.Finalizers.Any(p=>p.owner==Owner))
                        throw new InvalidOperationException("Branch visibility patch on "+target.Name+" was not installed exactly once");
                }
                Patch(TokenTarget,null,nameof(AfterBranchToken),null,Priority.Normal);
                Patch(VisibleTarget,null,nameof(AfterVisibility),null,Priority.Last);
                Patch(ApplyDownedTarget,null,null,nameof(DownedLevelTestTranspiler),Priority.Normal);
                Patch(FindReviveTarget,null,null,nameof(DownedLevelTestTranspiler),Priority.Normal);
                Patch(KingFxTarget,null,nameof(AfterKingHeadFx),null,Priority.Normal);
                Patch(FrameTarget,null,nameof(AfterFrame),null,Priority.Normal);
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;
                if(harmony!=null)foreach(var t in targets)try{harmony.Unpatch(t,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }

    static void Fault(R.Gate gate,string what,Exception error)
    {
        bool before=gate.Tripped,tripped=gate.Fail();
        if(gate.Faults==1)try{Serilog.Log.Warning("[CoopBranchVisibilityFix] {What} check failed; the base behaviour is kept: {Error}",what,error.GetBaseException().Message);}catch{}
        if(tripped&&!before)try{Serilog.Log.Warning("[CoopBranchVisibilityFix] {What} switched off for this session after {Faults} faults",what,gate.Faults);}catch{}
    }
    static void Info(string key,string template,params object[] values)
    {
        if(!Budget.Allow(key,Stopwatch.GetTimestamp()))return;
        try{Serilog.Log.Information("[CoopBranchVisibilityFix] "+template,values);}catch{}
    }
    static void InfoOnce(string key,string template,params object[] values)
    {
        if(!RepeatBudget.Allow(key,Stopwatch.GetTimestamp()))return;
        try{Serilog.Log.Information("[CoopBranchVisibilityFix] "+template,values);}catch{}
    }

    // ================= 1. stable branch token =================
    // Cached per Level (pointer + Haxe __uid): the base asks every hero frame (SendCurrentRoomTarget(force:false)).
    public static int TokenFor(Level level,out string key)
    {
        key=null;
        if(level==null||level.HashlinkPointer==IntPtr.Zero)return -1;
        bool sub=level.isSubLevel,secret=level.isSecret;
        if(!sub&&!secret){key="MAIN";return 0;}
        int uid=level.__uid;
        if(TokenCached&&level.HashlinkPointer==TokenLevelPtr&&uid==TokenLevelUid){key=TokenKey;return TokenValue;}
        var map=level.map;if(map==null||map.HashlinkPointer==IntPtr.Zero)return -1;
        var ids=new List<int>();bool zchild=false;var rooms=map.rooms;
        if(rooms!=null)for(int i=0;i<rooms.length;i++)
            if(rooms.getDyn(i) is dc.level.Room room&&room.isZChild)
            {
                zchild=true;var links=room.zLinks;
                if(links!=null)for(int j=0;j<links.length;j++)if(links.getDyn(j) is virtual_contentClue_doorId_id_map_ link)ids.Add(link.id);
            }
        bool secretOnly=secret&&!sub;
        key=R.Key(map.id?.ToString(),map.seed,map.wid,map.hei,secretOnly,zchild,ids);
        if(key==null)return -1;
        int token=R.Token(key);
        if(R.KeyComplete(secretOnly,zchild,ids.Count)){TokenCached=true;TokenLevelPtr=level.HashlinkPointer;TokenLevelUid=uid;TokenValue=token;TokenKey=key;}
        return token;
    }
    static void AfterBranchToken(Level currentLevel,ref int __result)
    {
        if(!Published||Disabled||VisibilityGate.Tripped||currentLevel==null)return;
        try
        {
            int token=TokenFor(currentLevel,out var key);
            if(token>0)__result=token;
            if(token>=0&&(currentLevel.HashlinkPointer!=LoggedLevel||token!=LoggedToken))
            {
                LoggedLevel=currentLevel.HashlinkPointer;LoggedToken=token;
                Info("token:"+token+":"+currentLevel.HashlinkPointer.ToInt64(),"Local branch token={Token} ({Branch}) level={Level} key={Key}",token,R.Describe(token),currentLevel.map?.id?.ToString()??"?",key??"?");
            }
            VisibilityGate.Ok();
        }
        catch(Exception error){Fault(VisibilityGate,"Branch token",error);}
    }

    // ================= 2. visibility =================
    static void NewSession()
    {
        var net=ModEntry._net;
        if(ReferenceEquals(net,Session))return;
        Session=net;Tracks.Clear();Rooms.Clear();CoopBranchVisibilityMarker.Forget("session");
    }
    static R.Track TrackOf(int id){if(!Tracks.TryGetValue(id,out var t))Tracks[id]=t=new R.Track();return t;}
    static void ReadLocalMarker(ModEntry mod,out string level,out int token)
    {
        level=MarkerLevel.GetValue(mod) as string;token=(int)MarkerToken.GetValue(mod);
    }
    // One decision per remote and frame, shared by the avatar (ReceiveGhostCoords), the CSF presenter and the corpse paths.
    static bool Decide(ModEntry mod,int remoteId,bool hasRoom,string roomLevel,int? roomToken,string localLevelId)
    {
        NewSession();
        if(!Rooms.TryGetValue(remoteId,out var seen))Rooms[remoteId]=seen=new RoomSeen();
        if(seen.HasRoom!=hasRoom||seen.Token!=(roomToken??-1)||!string.Equals(seen.Level,roomLevel,StringComparison.Ordinal))
        {
            seen.HasRoom=hasRoom;seen.Token=roomToken??-1;seen.Level=roomLevel;seen.At=Stopwatch.GetTimestamp();
            if(hasRoom)Info("remote:"+remoteId+":"+seen.Token+":"+roomLevel,"Remote branch token={Token} ({Branch}) of partner {Id} level={Level}",seen.Token,R.Describe(seen.Token),remoteId,roomLevel??"?");
        }
        ReadLocalMarker(mod,out var markerLevel,out var markerToken);
        var relation=R.Relate(hasRoom,roomLevel,roomToken,markerLevel,markerToken,localLevelId);
        var track=TrackOf(remoteId);long now=Stopwatch.GetTimestamp();
        bool hidden=R.Step(track,relation,now,R.HoldTicks(Stopwatch.Frequency),out bool changed,out bool started);
        if(started)CoopBranchVisibilityMarker.Capture(remoteId);
        if(changed)
        {
            if(hidden){HiddenDecisions++;Info("hide:"+remoteId,"Partner {Id} hidden: in another room (local={Local} remote={Remote} level={Level})",remoteId,R.Describe(markerToken),R.Describe(roomToken??-1),localLevelId??"?");}
            else{ShownDecisions++;Info("show:"+remoteId,"Partner {Id} shown: {Relation} (local={Local} remote={Remote})",remoteId,relation==R.Relation.Same?"same room":"room not comparable",R.Describe(markerToken),R.Describe(roomToken??-1));}
        }
        return hidden;
    }
    static void AfterVisibility(ModEntry __instance,NetNode.RemoteSnapshot remote,string localLevelId,ref bool __result)
    {
        if(!Published||Disabled||VisibilityGate.Tripped||__instance==null)return;
        try
        {
            if(!__result)
            {
                // The base already hides (another level): our hysteresis starts afresh on the next same-level frame.
                NewSession();if(Tracks.TryGetValue(remote.Id,out var t)&&(t.Hidden||t.DifferentSince!=0)){t.Reset();CoopBranchVisibilityMarker.Release(remote.Id,"other level");}
                VisibilityGate.Ok();return;
            }
            bool hidden=Decide(__instance,remote.Id,remote.HasRoom,remote.RoomLevelId,remote.RoomId,localLevelId);
            if(hidden){__result=R.KeepVisible(true,true);HiddenFrames++;}
            VisibilityGate.Ok();
        }
        catch(Exception error){Fault(VisibilityGate,"Partner visibility",error);}
    }
    public static bool IsHidden(int remoteId)=>Active&&Tracks.TryGetValue(remoteId,out var t)&&t.Hidden;
    internal static bool IsPendingOrHidden(int remoteId)=>Active&&Tracks.TryGetValue(remoteId,out var t)&&(t.Hidden||t.DifferentSince!=0);

    // The CSF cinematic presenter (CoopSyncFixes.CinematicPresentation.cs) moves the partner itself during admitted NPC
    // scenes, outside ReceiveGhostCoords. It asks here with the snapshot's ROOM marker; when hidden, the base's own disposal
    // (walkOut, then DisposeClientSlot) runs exactly as ReceiveGhostCoords would run it, and the presenter skips the partner.
    public static bool PresentationHide(int remoteId,string roomLevel,int roomToken,bool hasRoom)
    {
        if(!Active)return false;
        try
        {
            var mod=ModEntry.Instance;var net=ModEntry._net;var level=Game.Class.ME?.hero?._level;
            if(mod==null||net==null||level?.map==null)return false;
            bool hidden=Decide(mod,remoteId,hasRoom,roomLevel,roomToken<0?null:roomToken,level.map.id?.ToString());
            if(hidden&&ClientIndex(net.id,remoteId,out int slot)&&slot>=0&&slot<ModEntry.clients.Length)
            {
                QueueDispose(mod,slot);PresentationHides++;
            }
            VisibilityGate.Ok();return hidden;
        }
        catch(Exception error){Fault(VisibilityGate,"Presenter visibility",error);return false;}
    }
    internal static bool SlotOf(int remoteId,out int slot)
    {
        slot=-1;var net=ModEntry._net;
        return net!=null&&ClientIndex(net.id,remoteId,out slot)&&slot>=0&&slot<ModEntry.clients.Length;
    }

    // ================= 3. downed corpse and revive target =================
    // Both bodies test "state.LevelId == local level id" with String.Equals(a,b,Ordinal) right after loading the state
    // local (IL_00C7-IL_00D0 of IL:22046 and IL_00B2-IL_00BB of IL:25126). The transpiler keeps that call and ANDs its result with
    // CorpseSameBranch(result, state). The IL is pinned (CoopBranchVisibilityPins), so exactly one such site exists.
    static IEnumerable<CodeInstruction> DownedLevelTestTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        var code=instructions.ToList();
        var equals=typeof(string).GetMethod("Equals",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(string),typeof(string),typeof(StringComparison)},null);
        var filter=typeof(CoopBranchVisibilityPatch).GetMethod(__originalMethod.Name=="FindNearestReviveTarget"?nameof(ReviveSameBranch):nameof(CorpseSameBranch),S);
        int sites=0;
        for(int i=4;i<code.Count;i++)
        {
            if(!code[i].Calls(equals)||code[i-1].opcode!=OpCodes.Ldc_I4_4)continue;
            if(code[i-3].opcode!=OpCodes.Ldfld||!(code[i-3].operand is FieldInfo f)||f.Name!="LevelId"||f.DeclaringType!=DownedStateType)continue;
            var load=code[i-4];
            if(load.opcode!=OpCodes.Ldloc_S&&load.opcode!=OpCodes.Ldloc&&load.opcode!=OpCodes.Ldloc_0&&load.opcode!=OpCodes.Ldloc_1&&load.opcode!=OpCodes.Ldloc_2&&load.opcode!=OpCodes.Ldloc_3)continue;
            code.Insert(i+1,load.Clone());
            code.Insert(i+2,new CodeInstruction(OpCodes.Call,filter));
            sites++;i+=2;
        }
        if(sites!=1)throw new InvalidOperationException("Expected exactly one downed level test in "+__originalMethod.Name+", found "+sites);
        return code;
    }
    public static bool CorpseSameBranch(bool levelEquals,object state)=>DownedInScope(levelEquals,state,false);
    public static bool ReviveSameBranch(bool levelEquals,object state)=>DownedInScope(levelEquals,state,true);
    static bool DownedInScope(bool levelEquals,object state,bool revive)
    {
        if(!levelEquals||!Active||CorpseDisabled||state==null)return levelEquals;
        try
        {
            int id=(int)DownedUserId.GetValue(state);
            bool hidden=IsHidden(id);
            bool keep=R.CorpseInScope(levelEquals,hidden);
            if(!keep)
            {
                if(revive)ReviveFiltered++;else CorpseFiltered++;
                InfoOnce((revive?"revive:":"corpse:")+id,revive?"Downed partner {Id} is in another room: not revivable from here":"Downed partner {Id} is in another room: corpse not drawn here",id);
            }
            VisibilityGate.Ok();return keep;
        }
        catch(Exception error){Fault(VisibilityGate,"Downed partner",error);return levelEquals;}
    }

    // ================= R3: head flames after the ghost is visible again =================
    static void AfterKingHeadFx(object __instance)
    {
        if(!Published||Disabled||HeadDisabled||HeadGate.Tripped||!(__instance is dc.tool.HeroHead head))return;
        try
        {
            if(!(KingField.GetValue(__instance) is dc.Entity ghost)||ghost.HashlinkPointer==IntPtr.Zero||ghost.destroyed){HeadGate.Ok();return;}
            var normal=head.headNormalSb;var add=head.headAddSb;
            bool normalVisible=normal==null||normal.visible,addVisible=add==null||add.visible;
            if(R.RestoreHeadBatches(ghost.visible,normalVisible,addVisible))
            {
                if(normal!=null&&!normal.visible)normal.set_visible(true);
                if(add!=null&&!add.visible)add.set_visible(true);
                HeadRestores++;
                InfoOnce("head:"+ghost.HashlinkPointer.ToInt64(),"Partner head flames shown again (the avatar is visible)");
            }
            HeadGate.Ok();
        }
        catch(Exception error){Fault(HeadGate,"Partner head",error);}
    }

    // ================= frame tick: minimap marker =================
    static void AfterFrame()
    {
        if(!Published||Disabled)return;
        try{NewSession();CoopBranchVisibilityMarker.Tick();}
        catch(Exception error){Fault(CoopBranchVisibilityMarker.Gate,"Minimap marker",error);}
    }

    // ---- probe / API read-out ----
    public static Dictionary<string,object> Snapshot()
    {
        var mod=ModEntry.Instance;string markerLevel=null;int markerToken=int.MinValue;
        try{if(mod!=null&&MarkerLevel!=null)ReadLocalMarker(mod,out markerLevel,out markerToken);}catch{}
        var remotes=new List<object>();
        foreach(var pair in Tracks)
        {
            Rooms.TryGetValue(pair.Key,out var seen);
            remotes.Add(new Dictionary<string,object>{["id"]=pair.Key,["relation"]=pair.Value.Last.ToString(),["hidden"]=pair.Value.Hidden,["changes"]=pair.Value.Changes,
                ["differentFor"]=pair.Value.DifferentSince==0?0.0:(Stopwatch.GetTimestamp()-pair.Value.DifferentSince)/(double)Stopwatch.Frequency,
                ["roomLevel"]=seen?.Level,["roomToken"]=seen?.Token,["roomHas"]=seen?.HasRoom});
        }
        return new Dictionary<string,object>{["ready"]=Published,["active"]=Active,["disabled"]=Disabled,["markerDisabled"]=MarkerDisabled,["headDisabled"]=HeadDisabled,
            ["corpseDisabled"]=CorpseDisabled,["visibilityTripped"]=VisibilityGate.Tripped,["headTripped"]=HeadGate.Tripped,["markerTripped"]=CoopBranchVisibilityMarker.Gate.Tripped,
            ["faults"]=Faults,["localMarkerLevel"]=markerLevel,["localMarkerToken"]=markerToken,["localMarkerBranch"]=R.Describe(markerToken),
            ["hiddenDecisions"]=HiddenDecisions,["shownDecisions"]=ShownDecisions,["hiddenFrames"]=HiddenFrames,["corpseFiltered"]=CorpseFiltered,["reviveFiltered"]=ReviveFiltered,
            ["headRestores"]=HeadRestores,["presentationHides"]=PresentationHides,["remotes"]=remotes,["marker"]=CoopBranchVisibilityMarker.Snapshot(),
            ["symbolicPin"]=CoopBranchVisibilityPins.SymbolicPin,["logLines"]=Budget.Used};
    }
}
