using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;

// Position lines only when there is something to say.
//
// Community mod 0.5.1 sends the local player's position on every hero update with no change test
// (ModEntry.SendHeroCoords -> NetNode.TickSend): one line per frame, 110-200 lines per second per player.
// Measured: 85 % of all lines the host sends and 92 % of the client's, each one a synchronous send on the game
// thread (about 50 microseconds on loopback TCP, one Steam packet on Steam) and one game-thread dispatch on the
// partner's side.
//
// The receiver does not need the repeats: it stores the last position and re-applies it to the partner's avatar
// every frame by itself (NetNode.TryConsumeRemoteSnapshot keeps the state, ModEntry.ReceiveGhostCoords calls
// setPosPixel unconditionally), and it already lives with gaps of seconds whenever the sender is in a cinematic.
//
// Gate (prefix on NetNode.TickSend), only while the original would really send (a connection exists, id assigned):
//   * a call whose x, y, facing and level are bit-for-bit the values of the last line let through is skipped,
//   * except the first FrameFixRules.TrailingRepeats identical calls after a change (the resting position still
//     goes out three frames in a row, so one lost packet does not matter),
//   * except every 100 ms (refresh for packet loss and for a partner who just connected or reset),
//   * except when the mod itself asked for a forced send (ResetLocalHeroPositionSendCache).
// A moving player sends exactly as before. No wire change; an unpatched partner sees the same positions.
public static class PositionGate
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string Owner="CoopFrameFix.PositionGate.20261110";
    sealed class State{public bool Has;public double X,Y;public int Dir;public string Level;public long LastTicks;public int Trailing;}
    static readonly ConditionalWeakTable<NetNode,State> States=new();
    static readonly long RefreshTicks=(long)(Stopwatch.Frequency*FrameFixRules.PositionRefreshSeconds);
    static Func<NetNode,bool> HasAnyConnection;
    static volatile bool ForceNext;
    static long PassedCount,SkippedCount,IdleCount,FaultCount;
    static volatile bool Published;
    public static bool Ready=>Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static long Passed=>Interlocked.Read(ref PassedCount);
    public static long Skipped=>Interlocked.Read(ref SkippedCount);
    public static long NotConnected=>Interlocked.Read(ref IdleCount);
    public static long Faults=>Interlocked.Read(ref FaultCount);

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);

    public static void Install()
    {
        if(Published)return;
        if(InitializationError!=null)throw new InvalidOperationException("Earlier position gate initialization failed: "+InitializationError);
        Disabled=Environment.GetEnvironmentVariable("COOP_FRAME_POSGATE_DISABLE")=="1";
        if(Disabled){Published=true;return;}
        var harmony=new Harmony(Owner);MethodInfo tick=null,reset=null;
        try
        {
            var assembly=typeof(ModEntry).Assembly;
            if(assembly.GetName().Version!=new Version(0,5,1,0)||Hash(assembly.Location)!=BasePin)throw new InvalidOperationException("CoopFrameFix position gate requires exact community 0.5.1");
            const BindingFlags any=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
            tick=typeof(NetNode).GetMethod("TickSend",any,null,new[]{typeof(double),typeof(double),typeof(int),typeof(string)},null);
            reset=typeof(ModEntry).GetMethod("ResetLocalHeroPositionSendCache",any,null,Type.EmptyTypes,null);
            var connected=typeof(NetNode).GetMethod("HasAnyConnection",any,null,Type.EmptyTypes,null);
            if(tick==null||tick.ReturnType!=typeof(void)||tick.IsStatic||reset==null||connected==null||connected.IsStatic||connected.ReturnType!=typeof(bool))
                throw new InvalidOperationException("Pinned position send members changed");
            HasAnyConnection=(Func<NetNode,bool>)Delegate.CreateDelegate(typeof(Func<NetNode,bool>),connected);
            harmony.Patch(reset,postfix:new HarmonyMethod(typeof(PositionGate).GetMethod(nameof(AfterForcedReset),BindingFlags.Static|BindingFlags.NonPublic)));
            harmony.Patch(tick,prefix:new HarmonyMethod(typeof(PositionGate).GetMethod(nameof(BeforeTickSend),BindingFlags.Static|BindingFlags.NonPublic)));
            Published=true;
        }
        catch(Exception error)
        {
            Published=false;InitializationError=error.GetBaseException().Message;
            try{if(tick!=null)harmony.Unpatch(tick,HarmonyPatchType.All,Owner);}catch{}
            try{if(reset!=null)harmony.Unpatch(reset,HarmonyPatchType.All,Owner);}catch{}
            throw;
        }
    }

    static void AfterForcedReset(){ForceNext=true;}

    static bool BeforeTickSend(NetNode __instance,double __0,double __1,int __2,string __3)
    {
        try
        {
            if(__instance==null)return true;
            // The original returns without sending while there is no connection or no id yet. Nothing is recorded then,
            // so a forced or first line is not "used up" by a call that puts nothing on the wire.
            if(!HasAnyConnection(__instance)||__instance.id<=0){Interlocked.Increment(ref IdleCount);return true;}
            var state=States.GetOrCreateValue(__instance);
            long now=Stopwatch.GetTimestamp();
            bool same=state.Has&&FrameFixRules.SamePosition(__0,__1,__2,__3,state.X,state.Y,state.Dir,state.Level);
            var decision=FrameFixRules.DecidePosition(state.Has,ForceNext,same,state.Trailing,now,state.LastTicks,RefreshTicks);
            if(decision==FrameFixRules.Position.Skip){Interlocked.Increment(ref SkippedCount);return false;}
            ForceNext=false;
            state.Trailing=FrameFixRules.NextTrailing(decision,state.Trailing);
            state.Has=true;state.X=__0;state.Y=__1;state.Dir=__2;state.Level=__3;state.LastTicks=now;
            Interlocked.Increment(ref PassedCount);
            return true;
        }
        catch{Interlocked.Increment(ref FaultCount);return true;}
    }
}
