using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using dc.en;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using HaxeProxy.Runtime;
using File=System.IO.File;
using Path=System.IO.Path;

// Community mod 0.5.1 ModEntry.ResolveLocalDownedAnchor picks where a downed player's body
// goes. It takes a position from its own 12-second history and never checks the level, and
// DeadBase then pins the corpse there (ForceAnchoredGroundedPose after 0.70 s). When the
// position has no floor any more (lift left, ladder, ledge) the body hangs in the air.
// This postfix re-checks the chosen point against the current collision grid, once per
// death, and replaces it only when the grid positively shows nothing under it.
// Nothing runs per frame. No native function is called: the grid is read from the two
// arrays the game already keeps (LevelMap.collisions / groundYr).
public static class DownedAnchorPatch
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string Owner="CoopDownedAnchorFix.SupportedAnchor.20261109";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static readonly object Gate=new();
    static FieldInfo History,HistoryCount,HistoryWrite,UsesRecovery,EnvFlag,EnvTicks,SampleX,SampleY,SampleTicks,SampleLevel;
    static MethodInfo LevelId,Teammate;
    static volatile bool Published;
    static long Calls,Unsupported,Replaced,Unknown,Faults;
    static volatile string Last="";
    public static bool Ready=>Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static long Resolves=>Interlocked.Read(ref Calls);
    public static long UnsupportedAnchors=>Interlocked.Read(ref Unsupported);
    public static long Replacements=>Interlocked.Read(ref Replaced);
    public static long UnknownGrids=>Interlocked.Read(ref Unknown);
    public static long Errors=>Interlocked.Read(ref Faults);
    // One line describing the latest decision, for logs and tests.
    public static string LastDecision=>Last;

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static FieldInfo Field(Type type,string name,Type fieldType)
    {
        var field=type.GetField(name,I|BindingFlags.DeclaredOnly);
        if(field==null||(fieldType!=null&&field.FieldType!=fieldType))throw new InvalidOperationException("Pinned field mismatch: "+type.FullName+"."+name);
        return field;
    }
    public static void Install()
    {
        lock(Gate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier downed anchor initialization failed: "+InitializationError);
            Harmony harmony=null;MethodInfo target=null;
            try
            {
                var original=typeof(ModEntry).Assembly;
                if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||
                    Hash(Path.Combine(Path.GetDirectoryName(typeof(Hero).Assembly.Location),"hlboot.dat"))!=NativePin)
                    throw new InvalidOperationException("CoopDownedAnchorFix requires exact community0.5.1/native35.13.5");
                var mod=typeof(ModEntry);
                History=Field(mod,"_safeReviveAnchors",null);
                var sample=History.FieldType.GetElementType();
                if(!History.FieldType.IsArray||sample==null||!sample.IsValueType)throw new InvalidOperationException("Pinned anchor history shape changed");
                SampleX=Field(sample,"X",typeof(double));SampleY=Field(sample,"Y",typeof(double));
                SampleTicks=Field(sample,"Ticks",typeof(long));SampleLevel=Field(sample,"LevelId",typeof(string));
                HistoryCount=Field(mod,"_safeReviveAnchorCount",typeof(int));
                HistoryWrite=Field(mod,"_safeReviveAnchorWriteIndex",typeof(int));
                UsesRecovery=Field(mod,"_localDownedUsesRecoveryAnchor",typeof(bool));
                EnvFlag=Field(mod,"_lastLocalDamageWasEnvironmental",typeof(bool));
                EnvTicks=Field(mod,"_lastLocalDamageContextTicks",typeof(long));
                LevelId=mod.GetMethod("GetCurrentLevelId",I|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null);
                if(LevelId?.ReturnType!=typeof(string))throw new InvalidOperationException("Pinned level id signature changed");
                var byRef=typeof(double).MakeByRefType();
                Teammate=mod.GetMethod("TryGetLivingTeammateSafeAnchor",I|BindingFlags.DeclaredOnly,null,new[]{typeof(Hero),byRef,byRef},null);
                if(Teammate?.ReturnType!=typeof(bool))throw new InvalidOperationException("Pinned teammate anchor signature changed");
                target=mod.GetMethod("ResolveLocalDownedAnchor",I|BindingFlags.DeclaredOnly,null,new[]{typeof(Hero),typeof(double),typeof(double),byRef,byRef},null);
                if(target?.ReturnType!=typeof(void)||target.IsStatic)throw new InvalidOperationException("Pinned anchor resolver signature changed");
                Disabled=Environment.GetEnvironmentVariable("COOP_DOWNED_ANCHOR_DISABLE")=="1";
                harmony=new Harmony(Owner);
                harmony.Patch(target,
                    prefix:new HarmonyMethod(typeof(DownedAnchorPatch).GetMethod(nameof(BeforeResolve),S)),
                    postfix:new HarmonyMethod(typeof(DownedAnchorPatch).GetMethod(nameof(AfterResolve),S)));
                var installed=Harmony.GetPatchInfo(target);
                if(installed==null||installed.Postfixes.Count(p=>p.owner==Owner)!=1||installed.Prefixes.Count(p=>p.owner==Owner)!=1)
                    throw new InvalidOperationException("Downed anchor patch was not installed exactly once");
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

    // The original clears its damage context before returning, so read it first.
    // Never skips the original.
    static void BeforeResolve(ModEntry __instance,out bool __state)
    {
        __state=false;
        try
        {
            if(!Published||__instance==null)return;
            bool environmental=(bool)EnvFlag.GetValue(__instance);long ticks=(long)EnvTicks.GetValue(__instance);
            __state=environmental&&ticks>0&&Stopwatch.GetTimestamp()-ticks<=(long)(Stopwatch.Frequency*2.5);
        }
        catch{__state=false;}
    }

    sealed class Grid
    {
        public IntPtr Flags,Ground;public int FlagsLength,GroundLength,Width,Height;
        public bool Read(int cx,int cy,out int flags,out double groundYr)
        {
            flags=0;groundYr=double.NaN;
            if(cx<0||cy<0||cx>=Width||cy>=Height)return false;
            int index=cx+cy*Width;
            if(index<0||index>=FlagsLength||index>=GroundLength)return false;
            flags=Marshal.ReadInt32(Flags,index*4);
            groundYr=BitConverter.Int64BitsToDouble(Marshal.ReadInt64(Ground,index*8));
            return true;
        }
    }
    static Grid Capture(Hero hero)
    {
        if(hero==null||hero.HashlinkPointer==IntPtr.Zero||hero.destroyed)return null;
        var level=hero._level;if(level==null||level.HashlinkPointer==IntPtr.Zero||level.destroyed)return null;
        var map=level.map;if(map==null||map.HashlinkPointer==IntPtr.Zero)return null;
        var flags=map.collisions;var ground=map.groundYr;if(flags==null||ground==null)return null;
        int width=map.wid,height=map.hei;long cells=(long)width*height;
        if(width<=0||height<=0||cells>int.MaxValue/8||flags.length<cells||ground.length<cells||
            flags.bytes==IntPtr.Zero||ground.bytes==IntPtr.Zero)return null;
        return new Grid{Flags=flags.bytes,Ground=ground.bytes,FlagsLength=flags.length,GroundLength=ground.length,Width=width,Height=height};
    }
    static List<DownedAnchorRules.Sample> Samples(ModEntry mod,string level,long now)
    {
        var result=new List<DownedAnchorRules.Sample>();
        if(!(History.GetValue(mod) is Array history)||history.Length<=0)return result;
        int count=(int)HistoryCount.GetValue(mod),write=(int)HistoryWrite.GetValue(mod),size=history.Length;
        if(count<0||count>size||write<0||write>=size)return result;
        for(int i=0;i<count;i++)
        {
            object row=history.GetValue(((write-1-i)%size+size)%size);
            long ticks=(long)SampleTicks.GetValue(row);if(ticks<=0)continue;
            result.Add(new DownedAnchorRules.Sample((double)SampleX.GetValue(row),(double)SampleY.GetValue(row),
                (now-ticks)/(double)Stopwatch.Frequency,
                !string.IsNullOrWhiteSpace(level)&&string.Equals(SampleLevel.GetValue(row) as string,level,StringComparison.Ordinal)));
        }
        return result;
    }
    static string F(double v)=>v.ToString("0.0",CultureInfo.InvariantCulture);

    static void AfterResolve(ModEntry __instance,Hero hero,double deathX,double deathY,ref double downedX,ref double downedY,bool __state)
    {
        if(!Published||__instance==null)return;
        Interlocked.Increment(ref Calls);
        try
        {
            if(!ReferenceEquals(__instance,ModEntry.Instance)||!ReferenceEquals(hero,ModEntry.me))return;
            if(!double.IsFinite(downedX)||!double.IsFinite(downedY))return;
            var grid=Capture(hero);
            if(grid==null)
            {
                Interlocked.Increment(ref Unknown);Last="grid=unavailable action=Keep";
                try{Serilog.Log.Warning("[CoopDownedAnchorFix] Downed anchor kept: level grid unavailable");}catch{}
                return;
            }
            long now=Stopwatch.GetTimestamp();
            string level=LevelId.Invoke(__instance,null) as string;
            var samples=Samples(__instance,level,now);
            // Cheap first look: only ask for a teammate when the original point is bad.
            var first=DownedAnchorRules.Classify(downedX,downedY,grid.Width,grid.Height,grid.Read);
            bool hasMate=false;double mateX=0,mateY=0;
            if(first==DownedAnchorRules.Support.Unsupported)
            {
                var args=new object[]{hero,0.0,0.0};
                try{hasMate=(bool)Teammate.Invoke(__instance,args);mateX=(double)args[1];mateY=(double)args[2];}catch{hasMate=false;}
                if(!double.IsFinite(mateX)||!double.IsFinite(mateY))hasMate=false;
            }
            var decision=DownedAnchorRules.Decide(downedX,downedY,deathX,deathY,__state,samples,hasMate,mateX,mateY,grid.Width,grid.Height,grid.Read);
            if(decision.Original==DownedAnchorRules.Support.Unknown)Interlocked.Increment(ref Unknown);
            if(decision.Original==DownedAnchorRules.Support.Unsupported)Interlocked.Increment(ref Unsupported);
            string line="original="+F(downedX)+","+F(downedY)+" support="+decision.Original+" death="+F(deathX)+","+F(deathY)+
                " environmental="+__state+" samples="+samples.Count+" action="+decision.Action+" final="+F(decision.X)+","+F(decision.Y)+" disabled="+Disabled;
            Last=line;
            if(decision.Original!=DownedAnchorRules.Support.Unsupported)
            {
                // One line per death, so a player's log shows what was decided and why.
                try{Serilog.Log.Information("[CoopDownedAnchorFix] Downed anchor kept: {Decision}",line);}catch{}
                return;
            }
            if(Disabled||decision.Action==DownedAnchorRules.Action.Keep)
            {
                try{Serilog.Log.Warning("[CoopDownedAnchorFix] Unsupported downed anchor left unchanged: {Decision}",line);}catch{}
                return;
            }
            downedX=decision.X;downedY=decision.Y;
            // The body is still relocated by the mod (not left at the raw death point) for
            // every action except DeathPosition.
            UsesRecovery.SetValue(__instance,decision.Action!=DownedAnchorRules.Action.DeathPosition);
            Interlocked.Increment(ref Replaced);
            try{Serilog.Log.Information("[CoopDownedAnchorFix] Replaced unsupported downed anchor: {Decision}",line);}catch{}
        }
        catch(Exception error)
        {
            if(Interlocked.Increment(ref Faults)==1)
                try{Serilog.Log.Warning("[CoopDownedAnchorFix] Preserved original anchor; check failed: {Error}",error.GetBaseException().Message);}catch{}
        }
    }
}
