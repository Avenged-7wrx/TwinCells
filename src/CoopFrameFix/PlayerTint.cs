using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using dc.libs.heaps.slib;
using DeadCellsMultiplayerMod;

// Player colours without a handler on every sprite draw.
//
// The installed CoopSyncFixes (community-integration build, FFF00CA3...) colours the players' body sprites
// (host orange, the others cyan / per-player colours) from a handler on HSprite.draw: for the sprites listed in
// its BodyTints table it multiplies sprite.color before the draw and restores it afterwards. HSprite.draw runs
// for EVERY sprite, about 330 times per frame, and with a handler attached each call builds the framework's
// handler chain (about 1 KB of garbage and several microseconds) although only 1-4 sprites per frame are player
// bodies. Measured: about half of all hook dispatches of a play session.
//
// This module takes that one handler over: it removes the owner's draw handler and applies the same
// multiplication once per frame around Boot.render (the function inside which all sprite draws happen). Which
// sprites are tinted and with which player id comes from the owner's own BodyTints table (it keeps refreshing it
// every frame); the id-to-palette rule and the multipliers are the owner's (PlayerPaletteId with its
// TwoPlayerColors switch, compared with the owner's own function at takeover). Same colours on the same sprites;
// the colour is multiplied for the whole render instead of for each draw call. Known difference: everything that
// runs inside Boot.render other than the draws themselves (the scene's sync pass with its animation callbacks, hook
// handlers, the tasks the framework flushes after rendering) would see the multiplied colour of a player body;
// nothing in the game, the mod or the owner's modules reads or writes that vector there (checked in the bytecode
// and sources). A body sprite with its own h2d filter would differ too; the game puts filters only on UI roots.
// All draws of the scene go through Boot.render (the forced renders of loading screens included).
//
// HSprite.draw stays hooked by the framework with an empty handler list. That is the framework's ordinary state
// after any mod removes a handler; a call then costs about 1.3 microseconds and almost no garbage (measured).
// Nothing in the framework itself is changed by this part.
//
// Fail-safe: an error during the takeover or the tint, body sprites that cannot be read for 120 tinted frames in a
// row, or the owner's handler found active again puts the owner's handler back (adding a handler that is still
// registered changes nothing), removes this module's render handler and switches this part off for the session.
public static class PlayerTint
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string OwnerModulePin="FFF00CA3F9297B00EBD7688A9E4D223FD2A14F2D0EC8DFE34801F8490C7342E9";
    const BindingFlags Any=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
    public enum Mode{NotInstalled,Waiting,TakenOver,Disabled,OwnerAbsent,Failed}
    struct Saved{public dc.h3d.Vector Color;public double R,G,B;}
    const int MaxFailedFramesInARow=120;

    static readonly object Gate=new();
    static volatile Mode State=Mode.NotInstalled;
    static Type OwnerType;
    static bool OwnerChecked,PairProfile,RenderHookRemoved;
    static FieldInfo OwnerDrawHook,OwnerTints;
    static long[] OwnerDrawCounts;
    static EventInfo DrawEvent,RenderEvent;
    static Delegate RenderHook,RemovedOwnerHook;
    static readonly Saved[] Slots=new Saved[5];
    static int Used,RenderDepth,FailedFramesInARow;
    static long Frames,TintedSprites,Faults,WaitFrames,OwnerCountAfterTint;
    public static Mode Current=>State;
    public static bool TwoPlayerProfile=>PairProfile;
    public static string InitializationError{get;private set;}
    public static long TintedFrames=>Interlocked.Read(ref Frames);
    public static long TintedSpriteCount=>Interlocked.Read(ref TintedSprites);
    public static long Errors=>Interlocked.Read(ref Faults);

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);

    public static void Install()
    {
        lock(Gate)
        {
            if(State!=Mode.NotInstalled)return;
            try
            {
                if(Environment.GetEnvironmentVariable("COOP_FRAME_TINT_DISABLE")=="1"){State=Mode.Disabled;return;}
                if(Environment.GetEnvironmentVariable("COOP_SYNC_DISABLE_COLORS")=="1"){State=Mode.OwnerAbsent;return;}   // the owner's colours are switched off: nothing to take over
                // The tint reads ModEntry.clients of the community mod: only the exact build this was verified against.
                var mod=typeof(ModEntry).Assembly;
                if(mod.GetName().Version!=new Version(0,5,1,0)||Hash(mod.Location)!=BasePin)throw new InvalidOperationException("CoopFrameFix player colours require exact community 0.5.1");
                var proxy=typeof(HSprite).Assembly;
                DrawEvent=proxy.GetType("dc.libs.heaps.slib.Hook_HSprite",true).GetEvent("draw")??throw new InvalidOperationException("HSprite.draw hook event missing");
                RenderEvent=proxy.GetType("dc.Hook_Boot",true).GetEvent("render")??throw new InvalidOperationException("Boot.render hook event missing");
                // Render adapter with the exact delegate signature of the generated hook type (its delegate types cannot be named from here).
                var invoke=RenderEvent.EventHandlerType.GetMethod("Invoke");var parameters=invoke.GetParameters();
                if(parameters.Length<1||invoke.ReturnType!=typeof(void))throw new InvalidOperationException("Unexpected Boot.render hook signature");
                var types=parameters.Select(p=>p.ParameterType).ToArray();var origInvoke=types[0].GetMethod("Invoke");
                if(origInvoke==null||origInvoke.ReturnType!=typeof(void)||origInvoke.GetParameters().Length!=types.Length-1)throw new InvalidOperationException("Unexpected Boot.render original signature");
                var adapter=new DynamicMethod("CoopFrameFix_RenderTint",typeof(void),types,typeof(PlayerTint).Module,true);
                var il=adapter.GetILGenerator();
                il.Emit(OpCodes.Call,typeof(PlayerTint).GetMethod(nameof(BeforeRender),BindingFlags.Static|BindingFlags.NonPublic));     // never throws
                il.BeginExceptionBlock();
                for(int i=0;i<types.Length;i++)il.Emit(OpCodes.Ldarg,(short)i);
                il.Emit(OpCodes.Callvirt,origInvoke);
                il.BeginFinallyBlock();
                il.Emit(OpCodes.Call,typeof(PlayerTint).GetMethod(nameof(AfterRender),BindingFlags.Static|BindingFlags.NonPublic));      // never throws
                il.EndExceptionBlock();il.Emit(OpCodes.Ret);
                RenderHook=adapter.CreateDelegate(RenderEvent.EventHandlerType);RuntimeHelpers.PrepareDelegate(RenderHook);
                RenderEvent.AddEventHandler(null,RenderHook);
                State=Mode.Waiting;
            }
            catch(Exception error){State=Mode.Failed;InitializationError=error.GetBaseException().Message;throw;}
        }
    }

    // Looks for the owner's draw handler; takes it over once it exists. Game thread (inside Boot.render).
    static void TryTakeOver()
    {
        if(OwnerType==null)
        {
            // Mods are loaded before the first frame, so the first look normally finds it; later looks are spaced out.
            if(Interlocked.Read(ref WaitFrames)%30==0)
                OwnerType=global::CoopPackRuntime.PackParts.FindType("CoopSyncFixes","CoopSyncFixes");
            if(OwnerType==null){if(Interlocked.Increment(ref WaitFrames)>3600)State=Mode.OwnerAbsent;return;}      // no CoopSyncFixes in this installation
        }
        if(!OwnerChecked)
        {
            // Checked once: only the exact pinned build is taken over; any other build keeps its own draw handler.
            if(string.IsNullOrEmpty(OwnerType.Assembly.Location)||OwnerType.Assembly!=typeof(PlayerTint).Assembly){State=Mode.OwnerAbsent;return;}
            OwnerDrawHook=OwnerType.GetField("BodyDrawHook",Any);OwnerTints=OwnerType.GetField("BodyTints",Any);
            var pair=OwnerType.GetField("TwoPlayerColors",Any);var draws=OwnerType.GetField("TintDraws",Any);
            var palette=OwnerType.GetMethod("PlayerPaletteId",Any,null,new[]{typeof(int),typeof(bool)},null);
            if(OwnerDrawHook==null||OwnerTints==null||pair==null||palette==null||draws==null||OwnerTints.FieldType!=typeof(Dictionary<IntPtr,int>)||
                !typeof(Delegate).IsAssignableFrom(OwnerDrawHook.FieldType)||pair.FieldType!=typeof(bool)||palette.ReturnType!=typeof(int)||draws.FieldType!=typeof(long[]))
                throw new InvalidOperationException("Pinned CoopSyncFixes colour members changed");
            PairProfile=(bool)pair.GetValue(null);
            OwnerDrawCounts=draws.GetValue(null) as long[];
            if(OwnerDrawCounts==null)throw new InvalidOperationException("Pinned CoopSyncFixes draw counters missing");
            // This module's palette rule must be the owner's rule, for every id and both profiles.
            for(int id=-1;id<=6;id++)foreach(bool profile in new[]{false,true})
                if((int)palette.Invoke(null,new object[]{id,profile})!=FrameFixRules.PaletteId(id,profile))throw new InvalidOperationException("CoopSyncFixes palette rule differs from the one this module applies");
            OwnerChecked=true;
        }
        var hook=OwnerDrawHook.GetValue(null) as Delegate;
        if(hook==null){if(Interlocked.Increment(ref WaitFrames)>3600)State=Mode.OwnerAbsent;return;}               // the owner never installed its colours
        // Remembered before the removal: if the removal throws half-way, GiveBack can still put the handler back
        // (adding a handler that is still registered is a no-op in the hook manager).
        RemovedOwnerHook=hook;
        DrawEvent.RemoveEventHandler(null,hook);
        State=Mode.TakenOver;
        try{Serilog.Log.Information("[CoopFrameFix] Player colours now applied once per frame; twoPlayerProfile={Pair}",PairProfile);}catch{}
    }

    static void GiveBack(Exception error)
    {
        Interlocked.Increment(ref Faults);
        bool restored=PutOwnerHandlerBack();
        try
        {
            if(restored)Serilog.Log.Warning("[CoopFrameFix] Player colour takeover switched off, owner handler restored: {Error}",error.GetBaseException().Message);
            else Serilog.Log.Error("[CoopFrameFix] Player colour takeover switched off and the owner handler could NOT be restored (players are not coloured until restart): {Error}",error.GetBaseException().Message);
        }
        catch{}
    }
    // Ends the takeover. Adding a handler that is still registered is a no-op in the hook manager, so this is safe in
    // every state. Returns false only when a removed handler could not be added again.
    static bool PutOwnerHandlerBack()
    {
        var hook=RemovedOwnerHook;RemovedOwnerHook=null;State=Mode.Failed;
        if(hook==null)return true;                       // nothing was removed
        try{DrawEvent.AddEventHandler(null,hook);return true;}
        catch{Interlocked.Increment(ref Faults);return false;}
    }
    static long OwnerCountSum(){var counts=OwnerDrawCounts;long sum=0;if(counts!=null)for(int i=0;i<counts.Length;i++)sum+=counts[i];return sum;}

    // Called from the render adapter outside its try block: must never throw. Everything that touches other
    // assemblies is in Tint(), so even a failure to compile that method is caught here.
    static void BeforeRender()
    {
        try
        {
            if(!FrameFixRules.EnterRender(ref RenderDepth))return;     // only the outermost render of a frame tints
            if(Used>0)Restore();                                       // never expected: a previous render that ended without its restore
            var state=State;
            if(state!=Mode.TakenOver&&state!=Mode.Waiting)return;
            try{Tint(state);}
            catch(Exception error){Restore();GiveBack(error);}
        }
        catch{}
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Tint(Mode state)
    {
        if(state==Mode.Waiting){TryTakeOver();if(State!=Mode.TakenOver)return;}
        var tints=OwnerTints.GetValue(null) as Dictionary<IntPtr,int>;       // the owner still refreshes this table every frame
        if(tints!=null&&tints.Count>0)
        {
            // A sprite that cannot be read this frame (being destroyed, level change) is skipped for this frame only, as
            // the owner's own table refresh does. Only a long unbroken run of such frames switches the part off.
            bool failed=false;
            try{Consider(dc.pr.Game.Class.ME?.hero?.spr,tints);}catch{failed=true;}
            var clients=ModEntry.clients;
            if(clients!=null)for(int i=0;i<clients.Length&&Used<Slots.Length;i++){try{Consider(clients[i]?.spr,tints);}catch{failed=true;}}
            if(failed)
            {
                Interlocked.Increment(ref Faults);
                if(++FailedFramesInARow>=MaxFailedFramesInARow)throw new InvalidOperationException("player body sprites could not be read for "+MaxFailedFramesInARow+" frames in a row");
            }
            else FailedFramesInARow=0;
            if(Used>0){Interlocked.Increment(ref Frames);Interlocked.Add(ref TintedSprites,Used);}
        }
        OwnerCountAfterTint=OwnerCountSum();       // compared after the render, see AfterRender
    }
    // Same rule as the owner's BeforePlayerDraw: the sprite is in the table, has a colour vector, and its id has a palette.
    static void Consider(HSprite sprite,Dictionary<IntPtr,int> tints)
    {
        if(sprite==null||Used>=Slots.Length)return;
        if(!tints.TryGetValue(sprite.HashlinkPointer,out int id))return;
        var color=sprite.color;if(color==null)return;
        int palette=FrameFixRules.PaletteId(id,PairProfile);if(palette==0)return;
        for(int i=0;i<Used;i++)if(Slots[i].Color.HashlinkPointer==color.HashlinkPointer)return;      // one colour vector is multiplied once
        var saved=new Saved{Color=color,R=color.x,G=color.y,B=color.z};
        FrameFixRules.TintMultipliers(palette,out double r,out double g,out double b);
        Slots[Used++]=saved;                 // recorded before the first write: whatever happens next, Restore puts the three values back
        color.x=saved.R*r;color.y=saved.G*g;color.z=saved.B*b;
        var counts=OwnerDrawCounts;if(counts!=null&&id>=0&&id<counts.Length)counts[id]++;           // the owner's "tinted" counter stays alive (now per frame, not per draw)
    }
    static void Restore()
    {
        for(int i=Used-1;i>=0;i--)
        {
            try{var s=Slots[i];s.Color.x=s.R;s.Color.y=s.G;s.Color.z=s.B;}catch{Interlocked.Increment(ref Faults);}
            Slots[i]=default;
        }
        Used=0;
    }
    // Called from the adapter's finally block: must never throw.
    static void AfterRender()
    {
        try
        {
            if(!FrameFixRules.LeaveRender(ref RenderDepth))return;
            if(Used>0)Restore();
            var state=State;
            // Every frame, for the whole session: the owner's per-draw handler increments its counters on every draw it
            // tints, and nothing else writes them during a render. If they moved during this render, that handler is
            // attached (never removed, or added again later) and both would tint: this module stops, the owner's stays.
            if(state==Mode.TakenOver&&OwnerCountSum()!=OwnerCountAfterTint)
            {
                Interlocked.Increment(ref Faults);
                bool restored=PutOwnerHandlerBack();
                try{Serilog.Log.Warning("[CoopFrameFix] Player colour takeover switched off: the owner's draw handler is active; restored={Restored}",restored);}catch{}
            }
            // Nothing left to do for this session: stop being called every frame.
            state=State;
            if(!RenderHookRemoved&&(state==Mode.OwnerAbsent||state==Mode.Failed))
            {
                RenderHookRemoved=true;
                try{RenderEvent.RemoveEventHandler(null,RenderHook);}catch{}
            }
        }
        catch{}
    }
}
