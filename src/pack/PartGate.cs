using System;
using System.Threading;

namespace CoopPackRuntime
{
    // Per-part fault gate for forwarded DCCM events (PACK-DESIGN.md section 2.4). Same thresholds as LobbyCore.FaultGate:
    // trips at 8 consecutive or 64 total faults. Logs the first fault and the trip.
    public sealed class PartGate
    {
        public const int ConsecutiveLimit=8;
        public const int TotalLimit=64;
        readonly string _part;
        int _consecutive;long _total;bool _tripLogged;
        public volatile bool Tripped;

        public PartGate(string part){_part=part;}
        public long Faults=>Interlocked.Read(ref _total);
        public void Ok(){_consecutive=0;}

        // Returns true when the part's forwarding is switched off for the session.
        public bool Fail(string what,Exception error)
        {
            long n=Interlocked.Increment(ref _total);
            if(++_consecutive>=ConsecutiveLimit||n>=TotalLimit)Tripped=true;
            if(n==1)PackLog.Warn(_part+" "+what+" fault: "+PackHost.Innermost(error));
            if(Tripped&&!_tripLogged)
            {
                _tripLogged=true;
                PackLog.Warn(_part+" "+what+" update switched off for this session after "+n+" faults");
            }
            return Tripped;
        }
    }
}
