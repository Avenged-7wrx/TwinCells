using System;
using System.Collections;
using System.Collections.Generic;

// Production predicates and the single queue transaction. Native readiness and
// source identity are sampled by the caller while holding the original Sync.
public static class ShrineLateDeathGuard
{
    public sealed class Evidence
    {
        public int RetirementCalls,RetirementReturns,RetirementCompleted,RetirementFaults,OriginalPlainCalls,ObserverErrors;
        public int PresentationCalls,PresentationReturns,PresentationFaults;
        public bool Enrolled,Terminal;
    }
    public static bool PlainRetiredWithoutNativeDeath(Evidence e)=>e!=null&&e.RetirementCalls==1&&e.RetirementReturns==1&&
        e.RetirementCompleted==1&&e.RetirementFaults==0&&e.OriginalPlainCalls==0&&e.ObserverErrors==0;
    public static bool Eligible(Evidence e,bool current,bool exactSource,bool originalDead,bool nativeReady,bool unbound,bool oldUntracked,bool pendingFree)=>
        PlainRetiredWithoutNativeDeath(e)&&!e.Enrolled&&!e.Terminal&&
        e.PresentationCalls==1&&e.PresentationReturns==1&&e.PresentationFaults==0&&
        current&&exactSource&&originalDead&&nativeReady&&unbound&&oldUntracked&&pendingFree;

    // Both collections belong to the original mod. Caller holds Sync. Preserve
    // an existing entry; rollback only entries added by this exact transaction.
    public static bool Commit<T>(HashSet<T> pending,IDictionary firstFrames,T actor,double frame)
    {
        if(pending==null||firstFrames==null||actor==null||!double.IsFinite(frame)||frame<0||
            pending.Contains(actor)||firstFrames.Contains(actor))return false;
        bool added=false;
        try
        {
            added=pending.Add(actor);if(!added)return false;
            firstFrames.Add(actor,frame);
            return true;
        }
        catch
        {
            if(added){if(firstFrames.Contains(actor))firstFrames.Remove(actor);pending.Remove(actor);}
            throw;
        }
    }
}
