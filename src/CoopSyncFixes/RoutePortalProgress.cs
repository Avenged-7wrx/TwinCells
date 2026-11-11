using System;

// Local replay bookkeeping only. Native effects stay in ReplayCommittedPortal.
// State is owned by one validated WorldJoin and discarded with that transfer.
internal enum RoutePortalAction { Wait, Reveal, Activate }
internal sealed class RoutePortalProgress
{
    internal const long TimeoutMilliseconds=30000;
    readonly long Source,Destination,Portal;
    long PhaseStarted;
    internal bool RevealRequested {get;private set;}
    internal bool PortalRequested {get;private set;}
    internal RoutePortalProgress(long source,long destination,long portal,long now)
    {
        if(source==0 || destination==0 || portal==0 || source==destination)
            throw new InvalidOperationException("Invalid native portal route identity");
        Source=source;Destination=destination;Portal=portal;PhaseStarted=now;
    }
    internal void CheckMap(long current)
    {
        if(current!=Source && current!=Destination)
            throw new InvalidOperationException("Native portal route changed to an uncommitted map");
        if(current==Destination && !PortalRequested)
            throw new InvalidOperationException("Portal route entered destination before activation");
    }
    internal void CheckDeadline(long now)
    {
        if(now<PhaseStarted || now-PhaseStarted>=TimeoutMilliseconds)
            throw new InvalidOperationException(PortalRequested?
                "Committed native portal transition timed out":"Committed SecretLever reveal timed out");
    }
    internal RoutePortalAction Advance(long now,long map,long destination,long portal,bool visible,bool liveHero,bool eligibleReveal)
    {
        CheckDeadline(now);CheckMap(map);
        if(map!=Source || destination!=Destination || portal!=Portal)
            throw new InvalidOperationException("Committed portal instance changed during replay");
        if(!liveHero)throw new InvalidOperationException("Portal catch-up requires its live local hero");
        if(PortalRequested)return RoutePortalAction.Wait;
        if(!visible)
        {
            if(RevealRequested)return RoutePortalAction.Wait;
            if(!eligibleReveal)throw new InvalidOperationException("Hidden portal cannot be revealed by this route");
            RevealRequested=true;PhaseStarted=now;
            return RoutePortalAction.Reveal;
        }
        PortalRequested=true;PhaseStarted=now;
        return RoutePortalAction.Activate;
    }
}
