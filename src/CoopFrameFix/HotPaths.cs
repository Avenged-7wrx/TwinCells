using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using dc.en;
using DeadCellsMultiplayerMod;
using HarmonyLib;

// Two exact shortcuts in the community mod 0.5.1 (same results, less work and garbage). Host side mostly.
//
// 1. Boss-part verdict memo.
//    MobsSynchronization.ScanHostBossPartDespawns asks IsBossOwnedTransientPart(mob) for every tracked mob every
//    frame. For an ordinary mob the answer is decided by IsBossRelatedEntity(SafePartType(mob)), which rebuilds
//    the mob's type signature each time (mob.type.ToString() creates a new game string, plus two managed
//    strings, a lower-cased copy and up to eight Contains). The signature of a mob does not change during its
//    life (the mod itself and CoopPerfFix rely on that), so the type part is remembered per mob:
//        IsBossOwnedTransientPart(mob) = !combatant && level.boss != mob && typePart(mob)
//    typePart == false  -> the original returns false on every path: answered at once.
//    typePart == true   -> the original runs unchanged (it needs the live combatant / level.boss checks).
//    The memo is learned only while the mob's type id is readable, and every 64th call per mob the type part is
//    computed again and the memo corrected, so a wrong memory could not last longer than a fraction of a second.
//
// 2. Affect payload without the dynamic binder.
//    MobsSynchronization.BuildMobAffectPresencePayload was compiled with C# `dynamic`: for every affect slot
//    it runs three DLR call sites and boxes an int and a bool (measured: about 26 KB of garbage per call, and
//    up to 48 calls in one frame every 1.5 s). The replacement is the same loop with static types.
//    With COOP_FRAME_STATS=1 every 32nd payload is also built by a private copy of the original and compared.
//    gc-opt-20261112: the slots are read from HashLink memory (FrameAffectRaw) instead of through affects.getDyn(i),
//    which built a new wrapper (about 240 B) for every slot whose wrapper had died (measured 4.7 KB per call); a slot
//    the raw reading cannot decide exactly still runs the statements above. The text is built on the stack.
//
// The two patches are installed independently; a failure of one leaves the other in place.
public static class HotPaths
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string Owner="CoopFrameFix.HotPaths.20261110";
    const BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
    sealed class Verdict{public bool TypePart;public int Calls;}
    static readonly ConditionalWeakTable<Mob,Verdict> PartVerdicts=new();
    static Func<Mob,string> SafePartType,MobTypeId;
    static Func<string,bool> IsBossRelatedEntity;
    static Func<Mob,string> OriginalAffectPayload;
    static bool Stats;
    static long PartCalls,PartShort,PartOriginal,PartLearned,PartRecheck,PartCorrected,AffectCalls,AffectNonEmpty,AffectShadow,AffectMismatch,FaultCount;
    public static bool BossPartReady{get;private set;}
    public static bool AffectReady{get;private set;}
    public static bool BossPartDisabled{get;private set;}
    public static bool AffectDisabled{get;private set;}
    public static string BossPartError{get;private set;}
    public static string AffectError{get;private set;}
    public static string StatsLine=>string.Create(CultureInfo.InvariantCulture,
        $"partCalls={Interlocked.Read(ref PartCalls)} partShort={Interlocked.Read(ref PartShort)} partOriginal={Interlocked.Read(ref PartOriginal)} partLearned={Interlocked.Read(ref PartLearned)} partRecheck={Interlocked.Read(ref PartRecheck)} partCorrected={Interlocked.Read(ref PartCorrected)} affectCalls={Interlocked.Read(ref AffectCalls)} affectNonEmpty={Interlocked.Read(ref AffectNonEmpty)} affectShadow={Interlocked.Read(ref AffectShadow)} affectMismatch={Interlocked.Read(ref AffectMismatch)} hotErrors={Interlocked.Read(ref FaultCount)}");

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static string OriginalAffectStub(Mob mob)=>throw new NotImplementedException("reverse patch target");
    static Type Sync()
    {
        var assembly=typeof(ModEntry).Assembly;
        if(assembly.GetName().Version!=new Version(0,5,1,0)||Hash(assembly.Location)!=BasePin)throw new InvalidOperationException("CoopFrameFix hot paths require exact community 0.5.1");
        return assembly.GetType("DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization",true);
    }

    public static void Install()
    {
        Stats=Environment.GetEnvironmentVariable("COOP_FRAME_STATS")=="1";
        BossPartDisabled=Environment.GetEnvironmentVariable("COOP_FRAME_BOSSPART_DISABLE")=="1";
        AffectDisabled=Environment.GetEnvironmentVariable("COOP_FRAME_AFFECT_DISABLE")=="1";
        if(!BossPartDisabled&&!BossPartReady&&BossPartError==null)
        {
            MethodInfo part=null;var harmony=new Harmony(Owner+".BossPart");
            try
            {
                var sync=Sync();
                part=sync.GetMethod("IsBossOwnedTransientPart",S,null,new[]{typeof(Mob)},null);
                var safe=sync.GetMethod("SafePartType",S,null,new[]{typeof(Mob)},null);
                var related=sync.GetMethod("IsBossRelatedEntity",S,null,new[]{typeof(string)},null);
                var typeId=sync.GetMethod("GetMobTypeIdSafe",S,null,new[]{typeof(Mob)},null);
                if(part?.ReturnType!=typeof(bool)||safe?.ReturnType!=typeof(string)||related?.ReturnType!=typeof(bool)||typeId?.ReturnType!=typeof(string))
                    throw new InvalidOperationException("Pinned boss-part members changed");
                SafePartType=(Func<Mob,string>)Delegate.CreateDelegate(typeof(Func<Mob,string>),safe);
                IsBossRelatedEntity=(Func<string,bool>)Delegate.CreateDelegate(typeof(Func<string,bool>),related);
                MobTypeId=(Func<Mob,string>)Delegate.CreateDelegate(typeof(Func<Mob,string>),typeId);
                harmony.Patch(part,prefix:new HarmonyMethod(typeof(HotPaths).GetMethod(nameof(BeforeBossPart),BindingFlags.Static|BindingFlags.NonPublic)));
                BossPartReady=true;
            }
            catch(Exception error)
            {
                BossPartError=error.GetBaseException().Message;
                try{if(part!=null)harmony.Unpatch(part,HarmonyPatchType.All,harmony.Id);}catch{}
            }
        }
        if(!AffectDisabled&&!AffectReady&&AffectError==null)
        {
            MethodInfo affect=null;var harmony=new Harmony(Owner+".Affect");
            try
            {
                var sync=Sync();
                affect=sync.GetMethod("BuildMobAffectPresencePayload",S,null,new[]{typeof(Mob)},null);
                var dynLength=sync.GetMethod("TryGetDynLength",S,null,new[]{typeof(object)},null);
                if(affect?.ReturnType!=typeof(string)||dynLength?.ReturnType!=typeof(int))throw new InvalidOperationException("Pinned affect payload members changed");
                if(Stats)
                {
                    // A private copy of the original body, only for the sampled comparison in stats mode.
                    try
                    {
                        var copy=Harmony.ReversePatch(affect,new HarmonyMethod(typeof(HotPaths).GetMethod(nameof(OriginalAffectStub),BindingFlags.Static|BindingFlags.NonPublic)));
                        OriginalAffectPayload=(Func<Mob,string>)Delegate.CreateDelegate(typeof(Func<Mob,string>),copy);
                    }
                    catch{OriginalAffectPayload=null;Interlocked.Increment(ref FaultCount);}
                }
                harmony.Patch(affect,prefix:new HarmonyMethod(typeof(HotPaths).GetMethod(nameof(BeforeAffectPayload),BindingFlags.Static|BindingFlags.NonPublic)));
                AffectReady=true;
            }
            catch(Exception error)
            {
                AffectError=error.GetBaseException().Message;
                try{if(affect!=null)harmony.Unpatch(affect,HarmonyPatchType.All,harmony.Id);}catch{}
            }
        }
        if(BossPartError!=null||AffectError!=null)throw new InvalidOperationException("boss part: "+(BossPartError??"ok")+"; affect payload: "+(AffectError??"ok"));
    }

    // ---- 1. boss-part verdict memo ----
    static bool BeforeBossPart(Mob __0,ref bool __result)
    {
        if(__0==null)return true;
        try
        {
            if(Stats)Interlocked.Increment(ref PartCalls);
            if(!PartVerdicts.TryGetValue(__0,out var verdict))
            {
                // Learn only from a complete signature: the mob's own type id must be readable now. (The signature
                // builder falls back to the class name alone when it is not; that partial text is never remembered.)
                if(string.IsNullOrEmpty(MobTypeId(__0)))return true;
                string type=SafePartType(__0);
                if(string.IsNullOrEmpty(type))return true;
                verdict=PartVerdicts.GetValue(__0,_=>new Verdict{TypePart=IsBossRelatedEntity(type)});
                if(Stats)Interlocked.Increment(ref PartLearned);
            }
            else if((++verdict.Calls&63)==0)
            {
                // Every 64th call per mob: compute the type part again; a difference corrects the memo and lets the
                // original answer this call.
                if(Stats)Interlocked.Increment(ref PartRecheck);
                string type=SafePartType(__0);
                if(!string.IsNullOrEmpty(type)&&!string.IsNullOrEmpty(MobTypeId(__0)))
                {
                    bool fresh=IsBossRelatedEntity(type);
                    if(fresh!=verdict.TypePart){Interlocked.Increment(ref PartCorrected);verdict.TypePart=fresh;return true;}
                }
            }
            if(verdict.TypePart){if(Stats)Interlocked.Increment(ref PartOriginal);return true;}
            if(Stats)Interlocked.Increment(ref PartShort);
            __result=false;return false;
        }
        catch{Interlocked.Increment(ref FaultCount);return true;}
    }

    // ---- 2. affect payload, statically typed ----
    // Same statements as the release body (MonsterSynchronization.HostSend.cs BuildMobAffectPresencePayload):
    // slot i is listed when it holds an ArrayObj whose length is positive (TryGetDynLength), indices joined by '.'.
    // gc-opt-20261112: raw.Slot(i) answers 1 (listed) or 0 (not listed) from HashLink memory exactly as the two statements
    // below would, or -1 (not decided): then the two statements run as before. The indices are written into a stack
    // buffer; a StringBuilder (the original one) is made only when the buffer is full. Same text, same exception boundary.
    static string AffectPayload(Mob mob)
    {
        if(mob==null)return string.Empty;
        try
        {
            var affects=mob.getAllAffects();
            if(affects==null||affects.length<=0)return string.Empty;
            var raw=new FrameAffectRaw.Reader(affects);
            Span<char> text=stackalloc char[FrameAffectRaw.TextChars];
            int used=0;StringBuilder builder=null;
            for(int i=0;i<affects.length;i++)
            {
                int known=raw.Slot(i);
                if(known<0)
                {
                    object slot=affects.getDyn(i);
                    int length=DynLength(slot);
                    raw.Learn(i,slot,length);
                    if(length<=0)continue;
                }
                else if(known==0)continue;
                // The original made new StringBuilder(affects.length*3) here at the first listed slot; a negative
                // capacity (length above int.MaxValue/3) threw, and the catch below returned the empty string.
                if(used==0&&builder==null&&affects.length*3<0)return string.Empty;
                if(builder==null&&used+12<=text.Length)
                {
                    if(used>0)text[used++]='.';
                    i.TryFormat(text.Slice(used),out int written,default,CultureInfo.InvariantCulture);
                    used+=written;
                    continue;
                }
                builder??=new StringBuilder(affects.length*3).Append(text.Slice(0,used));
                if(builder.Length>0)builder.Append('.');
                builder.Append(i.ToString(CultureInfo.InvariantCulture));
            }
            raw.Finish();
            if(builder!=null)return builder.ToString();
            return used>0?new string(text.Slice(0,used)):string.Empty;
        }
        catch{return string.Empty;}
    }
    static int DynLength(object value)
    {
        if(!(value is dc.hl.types.ArrayObj array))return 0;
        try{return array.length;}catch{return 0;}
    }

    // gc-opt-20261112: the affect slots read from HashLink memory, without a wrapper (nested holder, own state).
    //
    // What the two statements of AffectPayload do for slot i (installed code: GameProxy, HashlinkSharp, HaxeProxy IL and
    // the HashLink bytecode of hlboot A37869A3):
    //   affects.getDyn(i)  GameProxy dc.hl.types.ArrayObj::getDyn calls HashLink f@71 hl.types.ArrayObj.getDyn:
    //                      if(pos>=this.length) return null; return this.array[pos];   (array: a varray of Dyn)
    //                      The pointer p it returns is marshalled: null gives null; otherwise the live wrapper of p or a
    //                      new HashlinkObject (kind HOBJ, not the String type), then GetProxy<object> gives the proxy of
    //                      class bindingTypes[type index of p]; type index 37 (hl.types.ArrayObj, the only HashLink type
    //                      in that class tree) is bound to dc.hl.types.ArrayObj and to nothing else.
    //   DynLength(slot)    slot is dc.hl.types.ArrayObj ? slot.length : 0, where length (ArrayBase::get_length,
    //                      GetValueFieldById<int>) is the int at the object's "length" field offset; it cannot throw for
    //                      such an object.
    // So a null slot gives 0, and a slot whose HashLink type is the ArrayObj type gives the int at the length offset. Slot()
    // decides exactly these two cases and answers -1 for everything else (another HashLink type, an affects object that is
    // not of the ArrayObj type, a null array): the two statements then run as before. Words read: affects + 0 (type),
    // + LengthOffset, + ArrayOffset, array + DataOffset + i * pointer size, p + 0 (type), p + LengthOffset. The original
    // statements read each of these words too (f@71, the marshaller's type test, get_length). Nothing is ever written.
    //
    // Trust: the ArrayObj type pointer and the offsets are used only after LearnCalls calls in which the two statements ran
    // for every slot (Slot answers -1 while learning) and every raw value agreed with the framework: affects.length, the
    // pointer and size of affects.array (HashlinkArray), and for every slot the raw element: zero exactly when the slot is
    // null, otherwise the HashlinkPointer of the slot's proxy (compared before the element is read; a call with a non-null
    // slot that is no proxy, such as a boxed value or a closure, cannot be checked and does not count), and for every slot of
    // the ArrayObj type its exact proxy class and its length. A call counts only when it compared at least one slot of the
    // ArrayObj type. A disagreement switches the raw reading off for the process (State 2), also when another call armed it
    // meanwhile; nothing arms it again. Once armed, the only runtime cross-check is BeforeAffectPayload's stats shadow
    // (stats mode only, every 32nd call, unchanged code): it returns the original text for the sampled call and counts
    // affectMismatch, but it does not switch the raw reading off.
    //
    // GC safety: HashLink's collector does not move objects and never writes into a live object (its mark bits are kept
    // outside the objects; the framework writes only the 8-byte trailer of an allocation, and only for a dead wrapper). A
    // collection may overlap these reads: managed code runs while its thread is in HashLink "blocking" mode (the framework's
    // callback stubs set hl_blocking(1) around the managed target), so another registered thread may collect meanwhile. That
    // is still safe because every object read is reachable: affects is the object of the live proxy 'affects' (the framework
    // reports the objects of live wrappers to the HashLink GC as roots), its varray is reachable from it, and the elements from
    // the varray. No raw object pointer is kept from one Slot call to the next (each call starts again from
    // affects.HashlinkPointer) except the ArrayObj type pointer, a type of the loaded module that is never freed.
    // Thread (verified statically on the release IL and the pack sources): every path into BuildMobAffectPresencePayload runs
    // on the game thread, the thread that changes the affect arrays. TryBuildClientAffectStateUpdate <- FlushClientDirtyMobQueue
    // <- OnFrameUpdateCore (frame event); BuildHostMobStatePayload <- ObserveHostMobForDirtyQueue <- Hook_Mob_postUpdate,
    // <- RememberHostDeathTombstoneLocked <- Hook_Mob_onDie, <- TryBuildHostMobDeltaSnapshot <- FlushHostPriorityResyncPass
    // <- FlushHostPriorityResync / TryBuildHostDirtySnapshotForQueue <- FlushHostDirtyMobQueue, both <- OnFrameUpdateCore;
    // in the pack also CoopSyncFixes BuildManifestSnapshot <- CaptureWorld <- ReadWorldLine, a NetNode.HandleLine prefix,
    // and HandleLine runs only in MainThreadPump jobs (ProcessMainThreadQueue, called from ModEntry.OnFrameUpdate,
    // hook_boot_update and a menu hook). The words read raw are a subset of those the original statements read, and
    // nothing is written.
    static class FrameAffectRaw
    {
        internal const int TextChars=512;          // stack buffer of AffectPayload (114 slots need at most 456 chars)
        const int LearnCalls=4;
        static readonly int LengthOffset=IntPtr.Size;      // hl.types.ArrayObj: hl_type* t; int length; varray* array
        static readonly int ArrayOffset=2*IntPtr.Size;     // (the int is padded to the pointer alignment)
        static readonly int SizeOffset=2*IntPtr.Size;      // varray (ModCore.Native HL_array): hl_type* t, at; int size, __pad
        static readonly int DataOffset=2*IntPtr.Size+8;    // the elements follow the varray header (hl_aptr)
        static int State;              // 0 learning, 1 armed, 2 off for this process
        static IntPtr ArrayType;       // HashLink type of hl.types.ArrayObj; valid once State is 1
        static int Learned;            // learning calls that agreed

        static IntPtr Element(IntPtr array,int i)=>System.Runtime.InteropServices.Marshal.ReadIntPtr(array+(DataOffset+(nint)i*IntPtr.Size));

        internal struct Reader
        {
            readonly dc.hl.types.ArrayObj affects;
            readonly IntPtr type;      // the ArrayObj type (armed: the learned one; learning: that of this affects object)
            readonly int mode;         // 1 armed, 0 learning this call, 2 statements only
            readonly int length;       // learning: affects.length at the start
            bool failed,unverified;int sameType;

            internal Reader(dc.hl.types.ArrayObj affects)
            {
                this.affects=affects;type=IntPtr.Zero;mode=2;length=0;failed=false;unverified=false;sameType=0;
                int state=Volatile.Read(ref State);
                if(state==1){type=Volatile.Read(ref ArrayType);mode=1;return;}
                if(state!=0)return;
                try
                {
                    // Learn only from an affects object of exactly the ArrayObj proxy class (so its HashLink object is a
                    // hl.types.ArrayObj, 24 bytes: the three header reads below stay inside it).
                    if(affects.GetType()!=typeof(dc.hl.types.ArrayObj))return;
                    int count=affects.length;
                    var array=affects.array;      // the framework's own reading of the varray (proxy calls first, raw reads after)
                    if(array==null)return;
                    IntPtr self=affects.HashlinkPointer;
                    if(self==IntPtr.Zero)return;
                    if(System.Runtime.InteropServices.Marshal.ReadInt32(self,LengthOffset)!=count){Off();return;}
                    IntPtr data=System.Runtime.InteropServices.Marshal.ReadIntPtr(self,ArrayOffset);
                    if(array.HashlinkPointer!=data||array.Count<count||System.Runtime.InteropServices.Marshal.ReadInt32(data,SizeOffset)!=array.Count){Off();return;}
                    type=System.Runtime.InteropServices.Marshal.ReadIntPtr(self);length=count;mode=0;
                }
                catch{Off();}
            }

            // 1: listed, 0: not listed, -1: not decided (the caller runs the two statements).
            internal int Slot(int i)
            {
                if(mode!=1)return -1;
                IntPtr self=affects.HashlinkPointer;
                if(self==IntPtr.Zero||System.Runtime.InteropServices.Marshal.ReadIntPtr(self)!=type)return -1;
                if(i>=System.Runtime.InteropServices.Marshal.ReadInt32(self,LengthOffset))return 0;     // getDyn: null
                IntPtr array=System.Runtime.InteropServices.Marshal.ReadIntPtr(self,ArrayOffset);
                if(array==IntPtr.Zero)return -1;                                                            // HashLink fails there
                IntPtr p=Element(array,i);
                if(p==IntPtr.Zero)return 0;                                                                 // null: not an ArrayObj
                if(System.Runtime.InteropServices.Marshal.ReadIntPtr(p)!=type)return -1;                    // another type
                return System.Runtime.InteropServices.Marshal.ReadInt32(p,LengthOffset)>0?1:0;
            }

            // Learning: compares the raw reading of slot i with what the two statements just gave (slot, DynLength).
            internal void Learn(int i,object slot,int dynLength)
            {
                if(mode!=0||failed)return;
                try
                {
                    IntPtr self=affects.HashlinkPointer;
                    if(self==IntPtr.Zero||i>=length||System.Runtime.InteropServices.Marshal.ReadIntPtr(self)!=type){failed=true;return;}
                    IntPtr array=System.Runtime.InteropServices.Marshal.ReadIntPtr(self,ArrayOffset);
                    if(array==IntPtr.Zero){failed=true;return;}
                    IntPtr p=Element(array,i);
                    if(p==IntPtr.Zero){if(slot!=null)failed=true;return;}
                    // p is never read unless the framework's object for this slot is a proxy of exactly that pointer. A
                    // non-null slot that is no proxy (a boxed value, a closure) cannot be checked: this call does not count.
                    if(!(slot is HaxeProxy.Runtime.HaxeProxyBase proxy)){unverified=true;return;}
                    if(proxy.HashlinkPointer!=p){failed=true;return;}               // p is checked before it is read
                    if(System.Runtime.InteropServices.Marshal.ReadIntPtr(p)!=type)return;     // another type: never decided raw
                    if(slot.GetType()!=typeof(dc.hl.types.ArrayObj)||System.Runtime.InteropServices.Marshal.ReadInt32(p,LengthOffset)!=dynLength){failed=true;return;}
                    sameType++;
                }
                catch{failed=true;}
            }

            internal void Finish()
            {
                if(mode!=0)return;
                if(failed){Off();return;}
                if(unverified)return;          // a non-null slot was no proxy: its element could not be checked
                if(sameType==0)return;         // no slot of the ArrayObj type was compared
                if(Interlocked.Increment(ref Learned)<LearnCalls)return;
                Interlocked.CompareExchange(ref ArrayType,type,IntPtr.Zero);
                if(Volatile.Read(ref ArrayType)==type)Interlocked.CompareExchange(ref State,1,0);
                else Off();
            }

            // Sticky: arming happens only from 0, so once off it stays off, and a learner that disagrees after another
            // call armed the raw reading switches it off too.
            static void Off()=>Interlocked.Exchange(ref State,2);
        }
    }
    static bool BeforeAffectPayload(Mob __0,ref string __result)
    {
        try
        {
            string mine=AffectPayload(__0);
            if(Stats)
            {
                long n=Interlocked.Increment(ref AffectCalls);
                if(mine.Length>0)Interlocked.Increment(ref AffectNonEmpty);
                var original=OriginalAffectPayload;
                if(original!=null&&(n&31)==0)
                {
                    Interlocked.Increment(ref AffectShadow);
                    string theirs=original(__0);
                    if(!string.Equals(mine,theirs,StringComparison.Ordinal)){Interlocked.Increment(ref AffectMismatch);__result=theirs;return false;}
                }
            }
            __result=mine;return false;
        }
        catch{Interlocked.Increment(ref FaultCount);return true;}
    }
}
