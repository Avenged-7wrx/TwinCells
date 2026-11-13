using System;
using System.Threading;

// Part CoopBossOutroFix (Alpha2 unit U1, PLAN-ALPHA2.md issue 10): the pure decisions, no game types. The game side
// (BossOutroPatch.cs) reads the live state and asks these rules; checks\BossOutroChecks.cs runs them with oracle tables
// and mutants. Every name here is part-prefixed (gate G3).
//
// The verified mechanism (PLAN-ALPHA2.md issue 10, re-read in the v35 bytecode for this unit):
//   Behemoth.tryToPreventDeath f@11626 sets life=1, lockAiS(99999) and, when it is the last Behemoth, builds a plain
//   dc.GameCinematic (whose constructor makes it Game.curCine and marks the previous scene destroyed) with the queue
//   ForceField.open... -> play "lethalHit" + onEnd(cm.signal "anim") -> shake/sound -> WAIT signal "anim" -> 500 ms ->
//   Entity.kill (f@846: onDie only while life>0) -> cine.destroyed=true (f@29029). On the client the base mod replays the
//   host's cached boss animation (ApplyBossAnimPayloadForce, IL 145292) whenever ShouldProcessClientVisualState(boss) is
//   true (IL 124083: false only for destroyed or life<=0 bosses), which replaces "lethalHit" and drops its onEnd: the
//   queue waits forever and the hero tick (curCine==null) never runs again.
public static class BossOutroRules
{
    public const string PlainSceneType="dc.GameCinematic";
    // Plan step 4: last-resort release of a plain scene that is still current after this many seconds (the native outro
    // takes about 4 s: lethalHit plus 500 ms).
    public const double StuckSeconds=20.0;
    // The unrecorded-scene test (another boss) runs at most this often while a candidate scene is current.
    public const double RecheckSeconds=0.5;

    public enum Release{None=0,AuthoritativeDeath=1,BossDestroyed=2,Stuck=3}

    public static string Describe(Release r)=>r switch{Release.AuthoritativeDeath=>"authoritative-death",Release.BossDestroyed=>"boss-destroyed",Release.Stuck=>"stuck",_=>"none"};

    // Exact runtime type only: subclasses (item menus, typed boss scenes, transitions) are never touched.
    public static bool IsPlainScene(string typeFullName)=>string.Equals(typeFullName,PlainSceneType,StringComparison.Ordinal);

    // A scene is the same object when both the native pointer and the game's per-process serial (Process.uniqId) match,
    // so a new scene that reuses a freed address is a different scene.
    public static bool SameScene(long ptrA,int uidA,long ptrB,int uidB)=>ptrA!=0&&ptrA==ptrB&&uidA==uidB;

    // Plan step 1: after the client's own Behemoth.tryToPreventDeath returned true, Game.curCine is a NEW live object whose
    // exact runtime type is dc.GameCinematic. Anything else (host, false, no scene, the same scene as before, a typed
    // scene, a destroyed scene) is not recorded.
    public static bool ShouldRecord(bool isClient,bool result,long beforePtr,int beforeUid,long afterPtr,int afterUid,bool afterDestroyed,string afterType)
        =>isClient&&result&&afterPtr!=0&&!afterDestroyed&&IsPlainScene(afterType)&&!SameScene(afterPtr,afterUid,beforePtr,beforeUid);

    // Plan step 2: ShouldProcessClientVisualState(mob) answers false for the recorded boss while its scene is the live
    // current scene (the base mod's own rule for a dying boss). Every other mob, and the boss after its scene, is left to
    // the original method. Step 2b uses the same rule for the replay of a host attack (TryQueueClientMobAttack): the
    // replayed skill's animation would replace "lethalHit" just like the animation replay.
    public static bool GuardApplies(bool armed,long guardBossPtr,long mobPtr,bool sceneCurrent)
        =>armed&&guardBossPtr!=0&&mobPtr==guardBossPtr&&sceneCurrent;

    // Plan steps 3 and 4 for the recorded outro (only while its scene is still the live current scene):
    //   the host's authoritative boss death was applied to this boss -> end the scene at once (its "lethalHit" can never
    //   fire on a destroyed boss); the boss was destroyed by any other path -> the same; otherwise after StuckSeconds.
    public static Release DecideRecorded(bool sceneCurrent,bool authoritativeDeathApplied,bool bossDestroyed,double ageSeconds)
    {
        if(!sceneCurrent)return Release.None;
        if(authoritativeDeathApplied)return Release.AuthoritativeDeath;
        if(bossDestroyed)return Release.BossDestroyed;
        if(ageSeconds>=StuckSeconds)return Release.Stuck;
        return Release.None;
    }

    // One boss that was alive when an unrecorded scene began: it counts as lethal or gone when the host's authoritative
    // death was applied to it, it left the base mod's tracking, it is destroyed, or its life is at most 1 (the native
    // tryToPreventDeath value).
    public static bool BossLethalOrGone(bool stillTracked,bool deathApplied,bool destroyed,int life)
        =>deathApplied||!stillTracked||destroyed||life<=1;

    // Plan step 4 for a scene this part did not record (another boss's death scene): a client in a run, on a BossLevelIds
    // level, the current scene is a live plain dc.GameCinematic older than StuckSeconds, at least one boss of that level
    // was alive when the scene began (so a dialog that starts after the fight is never cut), and one of those bosses is
    // now lethal or gone. The boss is never killed locally.
    public static Release DecideUnrecorded(bool isClient,bool inRun,bool bossLevel,string sceneType,bool sceneDestroyed,double ageSeconds,int bossesAliveAtStart,bool anyBossLethalOrGone)
        =>isClient&&inRun&&bossLevel&&IsPlainScene(sceneType)&&!sceneDestroyed&&ageSeconds>=StuckSeconds&&bossesAliveAtStart>0&&anyBossLethalOrGone
            ?Release.Stuck:Release.None;

    // After the base mod's own release routines ran: the fallback (destroyed=true, clear curCine, unlock controls) is
    // needed while the scene is still current or still not marked destroyed.
    public static bool NeedsFallback(bool sceneStillCurrent,bool sceneMarkedDestroyed)=>sceneStillCurrent||!sceneMarkedDestroyed;
}

// The shape of a generated GameProxy hook accessor (add_/remove_<event>): it starts with ldc.i4 <HL function index>
// (CoopOpt OptMinimapRules.StartsWithFunction). Hook_Behemoth.add_tryToPreventDeath is 20 6A2D0000 02 28 ... 2A (f@11626).
public static class BossOutroHookShape
{
    public static bool StartsWithFunction(byte[] il,int findex)=>il!=null&&il.Length>=5&&il[0]==0x20&&(il[1]|(il[2]<<8)|(il[3]<<16)|(il[4]<<24))==findex;
}

// Per-part fault gate (PLAN-ALPHA2.md section 4 rule 4). Same thresholds as the pack's PartGate and LobbyCore.FaultGate:
// trips at 8 consecutive or 64 total faults; a tripped part does nothing for the rest of the session.
public sealed class BossOutroFaultGate
{
    public const int ConsecutiveLimit=8;
    public const int TotalLimit=64;
    int _consecutive;long _total;
    volatile bool _tripped;
    public bool Tripped=>_tripped;
    public long Faults=>Interlocked.Read(ref _total);
    public void Ok(){_consecutive=0;}
    // Returns (first, trippedNow): first = the session's first fault (log it), trippedNow = this fault tripped the gate.
    public (bool First,bool TrippedNow) Fail()
    {
        long n=Interlocked.Increment(ref _total);
        bool was=_tripped;
        if(++_consecutive>=ConsecutiveLimit||n>=TotalLimit)_tripped=true;
        return (n==1,!was&&_tripped);
    }
}

// Rate limit for event lines (rule 4: rate-limited logs). Each category may log Max lines per session; the line after the
// last one says that further lines are counted, not logged.
public sealed class BossOutroLogBudget
{
    readonly int _max;int _used;long _suppressed;
    public BossOutroLogBudget(int max){_max=Math.Max(1,max);}
    public long Suppressed=>Interlocked.Read(ref _suppressed);
    public enum Grant{Log,LogLast,Silent}
    public Grant Take()
    {
        if(_used<_max){_used++;return _used==_max?Grant.LogLast:Grant.Log;}
        Interlocked.Increment(ref _suppressed);return Grant.Silent;
    }
}
