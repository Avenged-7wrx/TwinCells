using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using dc.en;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using File=System.IO.File;
using Path=System.IO.Path;

// Keeps the partner's avatar alive on MY screen while I am in an NPC dialog or item menu.
//
// 0.5.1: IOnHeroUpdate.OnHeroUpdate returns at once unless IsHeroRuntimeReadyForCoop, whose last
// test is Game.curCine == null. ReceiveGhostCoords (the only per-frame place that applies the
// partner's position, facing and body/head animation) is reachable only through that gate.
// A cinematic does not pause the level, so the partner's packets keep arriving and are simply
// not applied until the dialog ends.
//
// This module is a postfix on the mod's ungated per-frame callback ModEntry.OnFrameUpdate (the
// anchor the installed CoopSyncFixes cinematic presenter uses too). While the local Game.curCine
// is an admitted dialog cinematic (DialogPresenceRules) and every OTHER condition of the mod's own
// tick holds, it calls the mod's own ReceiveGhostCoords once per frame. Mirrored conditions: ModCore
// only broadcasts the hero tick while its HeroInstance is set; IsHeroRuntimeReadyForCoop minus the
// curCine test; and the avatar must belong to the current hero and game (otherwise the mod's
// bootstrap would rebuild it first, which is left to the mod). Nothing else of the gated tick is
// run: no sending, no fake-death flow, no head rebuilds (Alpha2: only the existing partner heads'
// effects are advanced, UpdatePartnerHeads), no weapons or attacks, and readiness itself is
// not changed. Cinematics owned by the installed presenter are left to it.
// Local presentation only: the receiver puts nothing on the wire, so the two players cannot desync
// through it. (If the receiver has to create the avatar on the host it bumps the mod's local
// combat-state revision, exactly as it does on any avatar creation outside dialogs.)
public static class DialogPresencePatch
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string Owner="CoopDialogPresenceFix.LivePartner.20261110";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static readonly object Gate=new();
    static volatile bool Published;
    static FieldInfo Ready,Role,OwnerHero,OwnerGame;
    static Action<ModEntry> Receive;
    static Func<bool> LocalDowned,InActualRun;
    static HashSet<string> BossLevels;
    // Current cinematic, judged once per cinematic object and level. Identity is the native pointer plus the
    // game's own per-process serial (Process.uniqId), so a new cinematic that reuses a freed address is judged
    // again; the level pointer is part of the key because the boss-level answer belongs to the level.
    static IntPtr CinePtr,CineLevelPtr;
    static int CineUid;
    static DialogPresenceRules.Verdict CineVerdict;
    static string CineType="",CineLevel="";
    static long CineStarted,CineFrames;
    static readonly HashSet<string> ReportedTypes=new(StringComparer.Ordinal);
    static int ConsecutiveFaults;
    static long Frames,Dialogs,Faults,Skipped;
    static volatile bool Tripped;
    public static bool ReadyToRun=>Published;
    public static bool Disabled{get;private set;}
    public static bool SwitchedOffAfterFaults=>Tripped;
    public static string InitializationError{get;private set;}
    public static long PresentedFrames=>Interlocked.Read(ref Frames);
    public static long PresentedDialogs=>Interlocked.Read(ref Dialogs);
    public static long GuardSkippedFrames=>Interlocked.Read(ref Skipped);
    public static long Errors=>Interlocked.Read(ref Faults);

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static FieldInfo Field(Type type,string name,Type fieldType,bool isStatic)
    {
        var field=type.GetField(name,(isStatic?S:I)|BindingFlags.DeclaredOnly);
        if(field==null||(fieldType!=null&&field.FieldType!=fieldType))throw new InvalidOperationException("Pinned field mismatch: "+type.FullName+"."+name);
        return field;
    }
    public static void Install()
    {
        lock(Gate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier dialog presence initialization failed: "+InitializationError);
            Harmony harmony=null;MethodInfo target=null;
            try
            {
                var original=typeof(ModEntry).Assembly;
                if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||
                    Hash(Path.Combine(Path.GetDirectoryName(typeof(Hero).Assembly.Location),"hlboot.dat"))!=NativePin)
                    throw new InvalidOperationException("CoopDialogPresenceFix requires exact community0.5.1/native35.13.5");
                var mod=typeof(ModEntry);
                Ready=Field(mod,"_ready",typeof(bool),false);
                Role=Field(mod,"_netRole",null,false);
                if(!Role.FieldType.IsEnum)throw new InvalidOperationException("Pinned role field is not an enum");
                OwnerHero=Field(mod,"_ghostOwnerHero",typeof(Hero),false);
                OwnerGame=Field(mod,"_ghostOwnerGame",typeof(dc.pr.Game),false);
                var receive=mod.GetMethod("ReceiveGhostCoords",I|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null);
                if(receive?.ReturnType!=typeof(void)||receive.IsStatic)throw new InvalidOperationException("Pinned ghost receiver signature changed");
                Receive=(Action<ModEntry>)Delegate.CreateDelegate(typeof(Action<ModEntry>),receive);
                var downed=mod.GetMethod("IsLocalPlayerDowned",S|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null);
                if(downed?.ReturnType!=typeof(bool))throw new InvalidOperationException("Pinned downed query changed");
                LocalDowned=(Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),downed);
                var lobby=original.GetType("DeadCellsMultiplayerMod.LobbySession",true);
                var run=lobby.GetMethod("IsInActualRun",S|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null);
                if(run?.ReturnType!=typeof(bool))throw new InvalidOperationException("Pinned run query changed");
                InActualRun=(Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),run);
                if(!(Field(mod,"BossLevelIds",typeof(HashSet<string>),true).GetValue(null) is HashSet<string> boss)||boss.Count<10)
                    throw new InvalidOperationException("Pinned boss level list changed");
                BossLevels=boss;
                target=mod.GetMethod("OnFrameUpdate",I|BindingFlags.DeclaredOnly,null,new[]{typeof(double)},null);
                if(target?.ReturnType!=typeof(void)||target.IsStatic)throw new InvalidOperationException("Pinned frame callback signature changed");
                Disabled=Environment.GetEnvironmentVariable("COOP_DIALOG_PRESENCE_DISABLE")=="1";
                harmony=new Harmony(Owner);
                harmony.Patch(target,postfix:new HarmonyMethod(typeof(DialogPresencePatch).GetMethod(nameof(AfterFrame),S)){priority=Priority.Last});
                var installed=Harmony.GetPatchInfo(target);
                if(installed==null||installed.Postfixes.Count(p=>p.owner==Owner)!=1)throw new InvalidOperationException("Dialog presence postfix was not installed exactly once");
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;
                if(harmony!=null&&target!=null)try{harmony.Unpatch(target,HarmonyPatchType.All,Owner);}
                    catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }

    // Runs after every ModEntry.OnFrameUpdate, also on the title screen and during teardown, so
    // every read is guarded and nothing may escape: an exception here would reach Boot.update.
    static void AfterFrame(ModEntry __instance)
    {
        if(!Published||Tripped||__instance==null)return;
        try
        {
            var game=dc.pr.Game.Class.ME;
            if(game==null||game.HashlinkPointer==IntPtr.Zero){Forget();return;}
            var cine=game.curCine;
            if(cine==null||cine.HashlinkPointer==IntPtr.Zero){Forget();return;}   // normal play: the mod's own tick is in charge
            if(!ReferenceEquals(__instance,ModEntry.Instance)||!(bool)Ready.GetValue(__instance)||Convert.ToInt32(Role.GetValue(__instance))==0)return;
            var net=ModEntry._net;
            if(net==null||!net.IsAlive||!net.HasRemote)return;
            // The mod's readiness conditions, all except the last one (curCine == null).
            if(game.destroyed||game.user==null||game.data==null||game.controller==null)return;
            var hero=game.hero;
            if(hero==null||hero.HashlinkPointer==IntPtr.Zero||hero.destroyed||!hero.awake||!hero.initDone)return;
            if(ModCore.Modules.Game.Instance?.HeroInstance==null)return;      // ModCore broadcasts the mod's hero tick only while this is set
            var me=ModEntry.me;
            if(me==null||me.HashlinkPointer!=hero.HashlinkPointer)return;      // the receiver works on ModEntry.me
            var level=hero._level;
            if(level==null||level.HashlinkPointer==IntPtr.Zero||level.destroyed||level.map==null)return;
            var owner=level.game;
            if(owner!=null&&owner.HashlinkPointer!=game.HashlinkPointer)return;
            var current=game.curLevel;
            if(current!=null&&current.HashlinkPointer!=level.HashlinkPointer)return;
            var cooldown=hero.cd;
            if(cooldown==null||cooldown.fastCheck==null)return;

            int uid=cine.uniqId;
            if(DialogPresenceRules.NeedsJudgement(cine.HashlinkPointer,uid,level.HashlinkPointer,CinePtr,CineUid,CineLevelPtr,CineVerdict))Judge(cine,level,uid);
            if(CineVerdict!=DialogPresenceRules.Verdict.Present)return;
            if(ModEntry._ghost==null||!InActualRun()||LocalDowned()){Interlocked.Increment(ref Skipped);return;}
            // The mod's bootstrap disposes and rebuilds an avatar that belongs to another hero or game before it
            // receives. That rebuild is not done here, so such a frame is skipped.
            if(!(OwnerHero.GetValue(__instance) is Hero ghostHero)||ghostHero.HashlinkPointer!=hero.HashlinkPointer||
                !(OwnerGame.GetValue(__instance) is dc.pr.Game ghostGame)||ghostGame.HashlinkPointer!=game.HashlinkPointer){Interlocked.Increment(ref Skipped);return;}
            if(Disabled)return;
            Receive(__instance);                       // the mod's own consumer; it keeps its own transition guards
            CineFrames++;Interlocked.Increment(ref Frames);ConsecutiveFaults=0;
            UpdatePartnerHeads(game,hero,level);
        }
        catch(Exception error)
        {
            long n=Interlocked.Increment(ref Faults);
            if(DialogPresenceRules.ShouldTrip(++ConsecutiveFaults))Tripped=true;
            if(n==1||Tripped)
                try{Serilog.Log.Warning("[CoopDialogPresenceFix] Frame skipped{Off}: {Error}",Tripped?"; switched off for this session":"",error.GetBaseException().Message);}catch{}
        }
    }
    // Alpha2 U9 (PLAN-ALPHA2.md issue 6 R1). The receiver above moves the partner's body during the dialog, but his head is
    // advanced only by ModEntry.UpdateGhostHeads at the end of the gated hero tick, so it froze at a stale offset while the
    // body moved. Only Kinghead.updateHeadFx runs here, on heads the mod already built for avatars of this level (never
    // UpdateGhostHeads: it can rebuild heads in the middle of a scene). Kinghead swaps the local hero's lastHeadPos and
    // visible and restores them in a finally (Kinghead.cs:281-314); both are checked after every call, restored if changed,
    // and the head path then stops for the session. A head fault stops only the head path, never the body presentation.
    // Own switch: COOP_DIALOG_PRESENCE_HEADS_DISABLE=1.
    static bool HeadsOff=Environment.GetEnvironmentVariable("COOP_DIALOG_PRESENCE_HEADS_DISABLE")=="1";
    static long HeadFrames;
    public static long PresentedHeadFrames=>Interlocked.Read(ref HeadFrames);
    public static bool HeadsSwitchedOff=>HeadsOff;
    static void UpdatePartnerHeads(dc.pr.Game game,Hero hero,dc.pr.Level level)
    {
        if(HeadsOff)return;
        var clients=ModEntry.clients;var heads=ModEntry.clientHeads;
        for(int i=0;i<clients.Length&&i<heads.Length;i++)
        {
            var ghost=clients[i];var head=heads[i];
            if(ghost==null||head==null||ghost.HashlinkPointer==IntPtr.Zero||head.HashlinkPointer==IntPtr.Zero||ghost.destroyed||
                ghost._level==null||ghost._level.HashlinkPointer!=level.HashlinkPointer)continue;
            bool visible=hero.visible;var last=hero.lastHeadPos;var lastPointer=last?.HashlinkPointer??IntPtr.Zero;
            try{head.updateHeadFx(game.ftime);Interlocked.Increment(ref HeadFrames);}
            catch(Exception error)
            {
                HeadsOff=true;
                try{Serilog.Log.Warning("[CoopDialogPresenceFix] Partner head update switched off for this session: {Error}",error.GetBaseException().Message);}catch{}
                return;
            }
            if(hero.visible!=visible||(hero.lastHeadPos?.HashlinkPointer??IntPtr.Zero)!=lastPointer)
            {
                hero.visible=visible;hero.lastHeadPos=last;HeadsOff=true;
                try{Serilog.Log.Warning("[CoopDialogPresenceFix] Partner head update changed the local hero; restored and switched off for this session");}catch{}
                return;
            }
        }
    }
    // A new cinematic object (or the same one on another level): classify it once and say so once in the log.
    static void Judge(dc.GameCinematic cine,dc.pr.Level level,int uid)
    {
        Forget();CineUid=uid;CineLevelPtr=level.HashlinkPointer;
        string type=cine.GetType().FullName??"";string levelId=level.map.id?.ToString()??"";
        CinePtr=cine.HashlinkPointer;CineType=type;CineLevel=levelId;CineStarted=Stopwatch.GetTimestamp();CineFrames=0;
        CineVerdict=DialogPresenceRules.Classify(type,levelId,id=>BossLevels.Contains(id));
        if(CineVerdict==DialogPresenceRules.Verdict.Present)
        {
            Interlocked.Increment(ref Dialogs);
            try{Serilog.Log.Information("[CoopDialogPresenceFix] Partner stays live during {Type} on {Level}; disabled={Disabled}",type,levelId,Disabled);}catch{}
        }
        else if(CineVerdict!=DialogPresenceRules.Verdict.PresenterOwned&&ReportedTypes.Count<64&&ReportedTypes.Add(type+"|"+CineVerdict))
        {
            // Once per type and reason per session: shows in a player's log which scenes are not covered.
            try{Serilog.Log.Information("[CoopDialogPresenceFix] Not presenting during {Type} on {Level}: {Verdict}",type,levelId,CineVerdict);}catch{}
        }
    }
    static void Forget()
    {
        if(CinePtr==IntPtr.Zero)return;
        if(CineVerdict==DialogPresenceRules.Verdict.Present)
            try{Serilog.Log.Information("[CoopDialogPresenceFix] {Type} ended after {Seconds:0.0} s; partner updated on {Frames} frames",CineType,(Stopwatch.GetTimestamp()-CineStarted)/(double)Stopwatch.Frequency,CineFrames);}catch{}
        CinePtr=IntPtr.Zero;CineLevelPtr=IntPtr.Zero;CineUid=0;CineVerdict=DialogPresenceRules.Verdict.Unknown;CineType="";CineLevel="";CineFrames=0;
    }
}
