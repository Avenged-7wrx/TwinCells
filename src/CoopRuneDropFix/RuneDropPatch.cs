using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using dc.en;
using dc.en.inter;
using dc.pr;
using dc.tool;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using HaxeProxy.Runtime;
using File=System.IO.File;
using Path=System.IO.Path;

// One existing remote-progress callsite. The original native award executes
// exactly once. Only its successful LadderKey award can retire a drop captured
// before that call, still belonging to the identical living client world.
public static class RuneDropPatch
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string Owner="CoopRuneDropFix.RemoteLadderLifecycle.20261108";
    const BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly object InitializationGate=new();
    static readonly MethodInfo NativeAdd=typeof(ItemMetaManager).GetMethod("addPermanentItem",I,null,new[]{typeof(dc.String)},null);
    static volatile bool Published;
    static long Captured,Retired,Rejected,ReadErrors,DestroyErrors;
    public static bool Ready=>Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static long CapturedDrops=>Interlocked.Read(ref Captured);
    public static long RetiredDrops=>Interlocked.Read(ref Retired);
    public static long RejectedAfterAward=>Interlocked.Read(ref Rejected);
    public static long InspectionErrors=>Interlocked.Read(ref ReadErrors);
    public static long CleanupErrors=>Interlocked.Read(ref DestroyErrors);
    sealed class Receipt
    {
        public NetNode Net;public Game Game;public dc.Main Main;public Hero Hero;
        public Level Level;public dc.User User;public ItemMetaManager Meta;
        public ItemDrop Drop;public InventItem Item;
        public IntPtr GamePointer,MainPointer,HeroPointer,LevelPointer,MapPointer,UserPointer,MetaPointer,EntitiesPointer,DropPointer,ItemPointer;
        public int Thread;
    }
    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static IntPtr Pointer(HaxeProxyBase value)=>value?.HashlinkPointer??IntPtr.Zero;
    static Game CurrentGame()=>Game.Class.ME;
    static dc.Main CurrentMain()=>dc.Main.Class.ME;
    static bool IsLadder(InventItem item)=>item?.kind is InventItemKind.Meta meta&&meta.Param0?.ToString()=="LadderKey";
    static bool Idle(Game game)=>!game.destroyed&&!game.paused&&game.curCine==null&&!game.hasCinematic();
    public static void Install()
    {
        lock(InitializationGate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier rune-drop initialization failed: "+InitializationError);
            Harmony harmony=null;MethodInfo target=null;
            try
            {
                var original=typeof(ModEntry).Assembly;
                if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||
                    Hash(Path.Combine(Path.GetDirectoryName(typeof(ItemDrop).Assembly.Location),"hlboot.dat"))!=NativePin)
                    throw new InvalidOperationException("CoopRuneDropFix requires exact community0.5.1/native35.13.5");
                RuneDropProxyCompatibility.ValidateLoaded(typeof(ItemDrop).Assembly);
                var type=original.GetType("DeadCellsMultiplayerMod.AdvancedCoop.CoopAdvancedHardening",true);
                target=type.GetMethod("ApplyPendingPermanentProgress",S,null,Type.EmptyTypes,null);
                if(target?.ReturnType!=typeof(void)||NativeAdd?.ReturnType!=typeof(bool))throw new InvalidOperationException("Pinned progress signature changed");
                Disabled=Environment.GetEnvironmentVariable("COOP_RUNE_DROP_DISABLE")=="1";
                harmony=new Harmony(Owner);
                harmony.Patch(target,transpiler:new HarmonyMethod(typeof(RuneDropPatch).GetMethod(nameof(ReplaceAwardCall),S)));
                var info=Harmony.GetPatchInfo(target);
                if(info==null||info.Transpilers.Count(p=>p.owner==Owner)!=1)throw new InvalidOperationException("Rune-drop callsite was not installed exactly once");
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;
                if(harmony!=null&&target!=null)try{harmony.Unpatch(target,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    static IEnumerable<CodeInstruction> ReplaceAwardCall(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();var calls=code.Where(i=>i.Calls(NativeAdd)).ToArray();
        if(calls.Length!=1||calls[0].opcode!=OpCodes.Callvirt)throw new InvalidOperationException("Expected one original native permanent award call");
        // Same receiver+argument stack and bool result; labels/EH metadata stay
        // on the original instruction, including all original fallback paths.
        calls[0].opcode=OpCodes.Call;calls[0].operand=typeof(RuneDropPatch).GetMethod(nameof(AddAndRetireCapturedDrop),S);
        return code;
    }
    static bool AddAndRetireCapturedDrop(ItemMetaManager meta,dc.String id)
    {
        Receipt receipt=null;
        if(Published&&!Disabled)try{receipt=Capture(meta,id);}catch(Exception error){Interlocked.Increment(ref ReadErrors);Warn("capture",error);}
        // No catch here: preserve the original native return/exception and its
        // existing ApplyPendingPermanentProgress retry behavior.
        bool added=meta.addPermanentItem(id);
        if(added&&receipt!=null)
        {
            try
            {
                if(!SameContext(receipt)||!meta.hasPermanentItem(id)||!SameUniqueDrop(receipt))
                    Interlocked.Increment(ref Rejected);
                else
                {
                    // Native MajorItemFound#14703 uses Entity proto69, destroy.
                    // Entity#861 queues native GC; ItemDrop#10047 removes icons.
                    // No pickup, award, HP, inventory or native field write here.
                    receipt.Drop.destroy();
                    if(receipt.Drop.destroyed)Interlocked.Increment(ref Retired);
                    else{Interlocked.Increment(ref DestroyErrors);Warn("destroy did not mark the captured drop",null);}
                }
            }
            catch(Exception error){Interlocked.Increment(ref DestroyErrors);Warn("cleanup",error);}
        }
        return added;
    }
    static Receipt Capture(ItemMetaManager meta,dc.String id)
    {
        if(meta==null||id?.ToString()!="LadderKey")return null;
        var net=ModEntry._net;if(net==null||!net.IsAlive||net.IsHost)return null;
        var game=CurrentGame();var main=CurrentMain();var hero=ModEntry.me;var level=hero?._level;var user=game?.user;
        if(game==null||main==null||hero==null||level?.map==null||user==null||level.entities==null||level.entitiesGC==null)return null;
        var receipt=new Receipt{Net=net,Game=game,Main=main,Hero=hero,Level=level,User=user,Meta=meta,Thread=Environment.CurrentManagedThreadId,
            GamePointer=Pointer(game),MainPointer=Pointer(main),HeroPointer=Pointer(hero),LevelPointer=Pointer(level),MapPointer=Pointer(level.map),
            UserPointer=Pointer(user),MetaPointer=Pointer(meta),EntitiesPointer=Pointer(level.entities)};
        if(!SameContext(receipt)||meta.hasPermanentItem(id))return null;
        var drop=UniqueDrop(receipt);if(drop==null)return null;
        receipt.Drop=drop;receipt.Item=drop.item;receipt.DropPointer=Pointer(drop);receipt.ItemPointer=Pointer(receipt.Item);
        if(receipt.DropPointer==IntPtr.Zero||receipt.ItemPointer==IntPtr.Zero)return null;
        Interlocked.Increment(ref Captured);return receipt;
    }
    static bool SameContext(Receipt r)
    {
        if(Environment.CurrentManagedThreadId!=r.Thread||!ReferenceEquals(ModEntry._net,r.Net)||!r.Net.IsAlive||r.Net.IsHost)return false;
        return r.GamePointer!=IntPtr.Zero&&r.MainPointer!=IntPtr.Zero&&r.HeroPointer!=IntPtr.Zero&&r.LevelPointer!=IntPtr.Zero&&r.MapPointer!=IntPtr.Zero&&r.UserPointer!=IntPtr.Zero&&r.MetaPointer!=IntPtr.Zero&&r.EntitiesPointer!=IntPtr.Zero&&
            Pointer(CurrentGame())==r.GamePointer&&Pointer(CurrentMain())==r.MainPointer&&!r.Main.destroyed&&Idle(r.Game)&&
            Pointer(ModEntry.me)==r.HeroPointer&&Pointer(r.Game.hero)==r.HeroPointer&&!r.Hero.destroyed&&r.Hero.life>0&&
            !r.Level.destroyed&&Pointer(r.Hero._level)==r.LevelPointer&&Pointer(r.Game.curLevel)==r.LevelPointer&&Pointer(r.Level.game)==r.GamePointer&&
            Pointer(r.Level.map)==r.MapPointer&&Pointer(r.Level.entities)==r.EntitiesPointer&&r.Level.entitiesGC!=null&&
            Pointer(r.Game.user)==r.UserPointer&&Pointer(r.Main.user)==r.UserPointer&&Pointer(r.User.itemMeta)==r.MetaPointer;
    }
    static ItemDrop UniqueDrop(Receipt r)
    {
        var entities=r.Level.entities;int count=entities.length;
        if(count<0||count>32768)return null;
        ItemDrop found=null;
        for(int i=0;i<count;i++)
        {
            if(entities.getDyn(i) is not ItemDrop drop||drop.destroyed||Pointer(drop._level)!=r.LevelPointer||!IsLadder(drop.item))continue;
            // An ambiguous or locally active/locked LadderKey is untouched.
            if(found!=null||!drop.initDone||drop.life<=0||drop.pickLocked||!drop.visible||drop.icon==null)return null;
            found=drop;
        }
        return found;
    }
    static bool SameUniqueDrop(Receipt r)
    {
        var current=UniqueDrop(r);
        return current!=null&&Pointer(current)==r.DropPointer&&Pointer(current.item)==r.ItemPointer&&
            !r.Drop.destroyed&&Pointer(r.Drop._level)==r.LevelPointer&&Pointer(r.Drop.item)==r.ItemPointer&&IsLadder(r.Drop.item);
    }
    static void Warn(string phase,Exception error)
    {
        try{Serilog.Log.Warning("[CoopRuneDropFix] Preserved original progression; {Phase} failed: {Error}",phase,error?.Message??"native cleanup incomplete");}catch{ }
    }
}
