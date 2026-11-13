using System;
using System.Collections.Generic;

// Pure rules of CoopDeathSoundFix (Alpha2 unit U7, PLAN-ALPHA2.md issue 1). No game types: the managed checks
// (checks\DeathSoundChecks.cs) compile this file alone and drive every rule through a fake fact source.
//
// The game plays its generic enemy death sound only inside Mob.playDamageSounds (f@1075, Mob.hx:2606-2607), whose only
// caller is the end of Mob.onDamage (f@1072 op1302). A client applies every host-confirmed death as "life=0; onDie()"
// inside the base mod's RunWithAuthoritativeClientMobDie, so no onDamage runs and the client hears nothing. The part
// plays that sound once, from a prefix on the base mod's Hook_Mob_onDie, when Decide says Play.
public static class DeathSoundRules
{
    public const string PartName="CoopDeathSoundFix";
    public const string KillSwitch="COOP_DEATH_SOUND_DISABLE";          // the part's own switch (scheme COOP_<PART>_DISABLE)
    // No pack row switch here: that name belongs to the pack host alone (parts.json row "switch"; PACK-DESIGN 2.5, gate G11).
    public const double RecentWindowSeconds=2.0;   // a die sound already heard for the same mob within this window is not repeated
    public const int RecentCapacity=256;
    public const int StackCapacity=16;
    public const int ConsecutiveFaultLimit=8;      // same thresholds as CoopPackRuntime.PartGate / LobbyCore.FaultGate
    public const int TotalFaultLimit=64;
    public const int DetailLines=3;                // the first plays are logged one by one
    public const double SummaryIntervalSeconds=60; // then at most one summary line per minute, and only when something changed

    // Order = evaluation order: the cheap tests first, the two lock-taking tests (shrine receipts, base death ledger) last.
    public enum Verdict
    {
        Play=0,
        OriginalSkipped=1,   // an earlier prefix skipped the base hook body (CoopSyncFixes' native-fault fence): no native death runs
        NotStackTop=2,       // not the mob of the innermost RunWithAuthoritativeClientMobDie on this thread
        NotClient=3,
        NotAuthoritative=4,  // base authoritativeClientMobDieDepth is 0: the base hook suppresses this onDie
        Destroyed=5,
        AlreadyDone=6,       // native onDieDone already set
        NotDying=7,          // life > 0
        Boss=8,              // bosses play their own death sounds in their onDie overrides; the boss death path stays untouched
        Culled=9,            // isOutOfGame && !isOnScreen (the base defers such deaths; defensive)
        Snapshot=10,         // CoopSyncFixes applies a manifest snapshot (deaths that happened before this client arrived)
        RecentlyPlayed=11,   // the die sound already ran for this mob within RecentWindowSeconds (the client's own lethal hit)
        ShrineRetirement=12, // CoopShrineDeathFix retires this replica with a plain destroy (no native death)
        LedgerDead=13,       // the base death ledger already marks it dead: the base hook returns before the native onDie
    }
    public const int VerdictCount=14;

    public interface IFacts
    {
        bool IsClient();
        int AuthoritativeDepth();
        bool Destroyed();
        bool OnDieDone();
        int Life();
        bool IsBoss();
        bool OutOfGame();
        bool OnScreen();
        int SnapshotDepth();
        bool RecentSound();
        bool ShrineReceipt();
        bool LedgerDead();
    }

    // Each fact is asked for only when every earlier test passed (the checks count the calls).
    public static Verdict Decide<T>(ref T f,bool runOriginal,bool stackTop) where T:IFacts
    {
        if(!runOriginal)return Verdict.OriginalSkipped;
        if(!stackTop)return Verdict.NotStackTop;
        if(!f.IsClient())return Verdict.NotClient;
        if(f.AuthoritativeDepth()<=0)return Verdict.NotAuthoritative;
        if(f.Destroyed())return Verdict.Destroyed;
        if(f.OnDieDone())return Verdict.AlreadyDone;
        if(f.Life()>0)return Verdict.NotDying;
        if(f.IsBoss())return Verdict.Boss;
        if(Culled(f.OutOfGame(),f.OnScreen()))return Verdict.Culled;
        if(f.SnapshotDepth()>0)return Verdict.Snapshot;
        if(f.RecentSound())return Verdict.RecentlyPlayed;
        if(f.ShrineReceipt())return Verdict.ShrineRetirement;
        if(f.LedgerDead())return Verdict.LedgerDead;
        return Verdict.Play;
    }
    // The base mod's own culling test (MobsSynchronization.IsMobCulledLocally).
    public static bool Culled(bool outOfGame,bool onScreen)=>outOfGame&&!onScreen;
    // A real authoritative client death candidate (counted in the statistics); the first four verdicts are routine.
    public static bool Counted(Verdict v)=>v==Verdict.Play||v>=Verdict.Destroyed;
    public static bool SwitchOn(string value)=>value=="1";

    // Mobs whose die sound ran recently: native pointer + level identity token + time (seconds, monotonic).
    // A token of 0 means "identity not ready" and matches any token (a level change keeps the pointer distinct anyway).
    public sealed class RecentSounds
    {
        struct Entry{public IntPtr Ptr;public int Token;public double At;}
        readonly List<Entry> entries=new List<Entry>();
        public int Count=>entries.Count;
        public void Remember(IntPtr ptr,int token,double now)
        {
            if(ptr==IntPtr.Zero||double.IsNaN(now))return;
            Prune(now);
            for(int i=entries.Count-1;i>=0;i--)if(entries[i].Ptr==ptr)entries.RemoveAt(i);
            entries.Add(new Entry{Ptr=ptr,Token=token,At=now});
            while(entries.Count>RecentCapacity)entries.RemoveAt(0);
        }
        public bool Recent(IntPtr ptr,int token,double now)
        {
            if(ptr==IntPtr.Zero||double.IsNaN(now))return false;
            foreach(var e in entries)
            {
                if(e.Ptr!=ptr)continue;
                if(e.Token!=token&&e.Token!=0&&token!=0)continue;
                double age=now-e.At;
                if(age>=0&&age<RecentWindowSeconds)return true;
            }
            return false;
        }
        public void Clear()=>entries.Clear();
        void Prune(double now){entries.RemoveAll(e=>now-e.At>=RecentWindowSeconds||now<e.At);}
    }

    // The mobs of the RunWithAuthoritativeClientMobDie calls active on one thread (innermost last). Pushes past the
    // capacity are counted but not stored, so the depth stays balanced and an overflowed scope is never "top".
    public sealed class ScopeStack
    {
        readonly IntPtr[] items=new IntPtr[StackCapacity];
        int depth;
        public int Depth=>depth;
        public void Push(IntPtr ptr){if(depth<items.Length)items[depth]=ptr;depth++;}
        public void Pop(){if(depth<=0)return;depth--;if(depth<items.Length)items[depth]=IntPtr.Zero;}
        public bool IsTop(IntPtr ptr)=>ptr!=IntPtr.Zero&&depth>0&&depth<=items.Length&&items[depth-1]==ptr;
    }

    // Per-part fault gate: trips at ConsecutiveFaultLimit consecutive or TotalFaultLimit total faults; a tripped gate
    // stays tripped for the session (the patches then return at once).
    public sealed class FaultGate
    {
        int consecutive;long total;
        public bool Tripped{get;private set;}
        public long Total=>total;
        public void Ok(){consecutive=0;}
        public bool Fail(){total++;consecutive++;if(consecutive>=ConsecutiveFaultLimit||total>=TotalFaultLimit)Tripped=true;return Tripped;}
    }

    // Rate limit of the part's own log lines.
    public sealed class LogLimiter
    {
        int details;double lastSummary=double.NegativeInfinity;long lastSignature=-1;
        public bool Detail(){if(details>=DetailLines)return false;details++;return true;}
        public bool Summary(double now,long signature)
        {
            if(signature==lastSignature||now-lastSummary<SummaryIntervalSeconds)return false;
            lastSummary=now;lastSignature=signature;return true;
        }
    }
}
