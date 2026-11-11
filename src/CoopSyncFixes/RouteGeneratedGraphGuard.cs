using System;

internal readonly struct RouteGraphIdentity
{
    internal readonly string Level,Branch;
    internal readonly double Seed;
    internal RouteGraphIdentity(string level,double seed,string branch){Level=level;Seed=seed;Branch=branch;}
    internal bool Matches(RouteGraphIdentity other)=>
        !string.IsNullOrEmpty(Level) && !string.IsNullOrEmpty(Branch) && double.IsFinite(Seed) && double.IsFinite(other.Seed) &&
        Level==other.Level && Branch==other.Branch && BitConverter.DoubleToInt64Bits(Seed)==BitConverter.DoubleToInt64Bits(other.Seed);
}

// Shared production/test gate for the only extra graph allowed during an active
// native generator: its host-committed direct Challenge child. Never search all
// frozen graphs by a potentially ambiguous level name.
internal static class RouteGeneratedGraphGuard
{
    internal static bool CanEnter(bool routed,bool activationOnly,int sourceSlot,int stepCount,bool sourceImported,bool importedInCurrentLoad,
        string kind,bool isExit,string childLevel,string childBranch,long childSequence,RouteGraphIdentity expectedSource,RouteGraphIdentity actualSource)
        =>routed && !activationOnly && sourceSlot>=0 && sourceSlot<stepCount && sourceImported && importedInCurrentLoad && kind=="Portal" && !isExit &&
          childLevel=="Challenge" && childBranch=="MAIN" && childSequence>0 && expectedSource.Matches(actualSource);
}

internal sealed class RouteGeneratedGraphReceipt
{
    private readonly int slot;private readonly long sequence;
    internal bool Accepted {get;private set;}
    internal RouteGeneratedGraphReceipt(int slot,long sequence){this.slot=slot;this.sequence=sequence;}
    internal void Acknowledge(int appliedSlot,long appliedSequence)
    {if(appliedSlot==slot && appliedSequence==sequence && sequence>0)Accepted=true;}
}
