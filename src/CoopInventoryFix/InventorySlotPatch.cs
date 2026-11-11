using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;

// Only the already-committed receive buffer and its existing consumer Add are
// extended. Original sender selection, parsing, locks, gates and list pool stay.
public static class InventorySlotPatch
{
    public const string BaseSha256="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string Owner="CoopInventoryFix.PendingSlots.20261108";
    const BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    static readonly object InstallGate=new();
    static readonly ConditionalWeakTable<object,Pending> PendingByState=new();
    static Func<object,NetNode.RemoteWeaponSnapshot> ReadSnapshot;
    static Func<NetNode,object> ReadSync;
    static FieldInfo HasWeaponUpdate,HasPosition;
    static volatile bool IsReady;
    static long Commits,Expanded,Barriers,MarkerResets;
    public static bool Ready=>IsReady;
    public static bool Disabled {get;private set;}
    public static string InitializationError {get;private set;}
    public static long CommittedUpdates=>Interlocked.Read(ref Commits);
    public static long AdditionalConsumedSlots=>Interlocked.Read(ref Expanded);
    public static long LegacyBarriers=>Interlocked.Read(ref Barriers);
    public static long MarkerResetWindows=>Interlocked.Read(ref MarkerResets);

    // Two bounded values, no arrival history, node reference or strong key link.
    sealed class Pending
    {
        internal bool Has0,Has1;
        internal int LastSlot;
        internal NetNode.RemoteWeaponSnapshot Slot0,Slot1;
        internal void Put(NetNode.RemoteWeaponSnapshot value)
        {if(value.Slot==0){Slot0=value;Has0=true;}else{Slot1=value;Has1=true;}LastSlot=value.Slot;}
        internal void Append(List<NetNode.RemoteWeaponSnapshot> output)
        {
            // Last-arrival order matters when a permanent item changes slots.
            if(Has0 && Has1){output.Add(LastSlot==0?Slot1:Slot0);output.Add(LastSlot==0?Slot0:Slot1);Interlocked.Increment(ref Expanded);}
            else if(Has0)output.Add(Slot0);else if(Has1)output.Add(Slot1);
            else throw new InvalidOperationException("Empty inventory pending entry");
        }
    }

    public static void Install()
    {
        lock(InstallGate)
        {
            if(IsReady)return;
            if(InitializationError!=null)throw new InvalidOperationException("Prior inventory initialization failed: "+InitializationError);
            Harmony h=null;MethodInfo incoming=null,consume=null,reset=null;
            try
            {
                var assembly=typeof(NetNode).Assembly;
                if(assembly.GetName().Version!=new Version(0,5,1,0) || global::CoopPackRuntime.PackPins.Sha256(assembly.Location)!=BaseSha256)
                    throw new InvalidOperationException("CoopInventoryFix requires the exact community 0.5.1 DLL");
                var state=assembly.GetType("NetNode+RemotePlayerState",true);
                if(!state.IsClass || !state.IsSealed)throw new InvalidOperationException("Remote state identity shape changed");
                HasWeaponUpdate=RequiredField(state,"HasWeaponUpdate",typeof(bool));
                HasPosition=RequiredField(state,"HasPosition",typeof(bool));
                ReadSnapshot=MakeSnapshotReader(state);
                var sync=RequiredField(typeof(NetNode),"_sync",typeof(object));
                var getter=new DynamicMethod("CoopInventoryFix_ReadSync",typeof(object),new[]{typeof(NetNode)},typeof(InventorySlotPatch),true);
                var il=getter.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,sync);il.Emit(OpCodes.Ret);
                ReadSync=(Func<NetNode,object>)getter.CreateDelegate(typeof(Func<NetNode,object>));
                incoming=typeof(NetNode).GetMethod("HandleLine",Fields,null,new[]{typeof(string),typeof(int?),typeof(string).MakeByRefType()},null);
                consume=typeof(NetNode).GetMethod("TryConsumeRemoteWeaponSnapshots",Fields,null,new[]{typeof(List<NetNode.RemoteWeaponSnapshot>).MakeByRefType()},null);
                reset=typeof(NetNode).GetMethod("ClearRemoteRoomMarkers",Fields,null,Type.EmptyTypes,null);
                if(incoming==null || consume==null || reset==null || incoming.ReturnType!=typeof(bool) || consume.ReturnType!=typeof(bool) || reset.ReturnType!=typeof(void))throw new InvalidOperationException("Pinned inventory methods changed");
                Disabled=Environment.GetEnvironmentVariable("COOP_INVENTORY_SLOTS_DISABLE")=="1";
                h=new Harmony(Owner);
                h.Patch(incoming,transpiler:new HarmonyMethod(typeof(InventorySlotPatch).GetMethod(nameof(CommitTranspiler),Static)));
                h.Patch(consume,transpiler:new HarmonyMethod(typeof(InventorySlotPatch).GetMethod(nameof(ConsumeTranspiler),Static)));
                h.Patch(reset,transpiler:new HarmonyMethod(typeof(InventorySlotPatch).GetMethod(nameof(ResetTranspiler),Static)));
                if(new[]{incoming,consume,reset}.Any(m=>!Harmony.GetPatchInfo(m).Owners.Contains(Owner)))throw new InvalidOperationException("Inventory hooks not registered");
                IsReady=true; // publish all hooks together; pre-ready callbacks preserve original behavior
            }
            catch(Exception error)
            {
                IsReady=false;InitializationError=error.Message;
                if(h!=null)foreach(var target in new[]{incoming,consume,reset})if(target!=null)
                    try{h.Unpatch(target,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }

    static FieldInfo RequiredField(Type type,string name,Type fieldType)
    {var f=type.GetField(name,Fields);if(f==null || f.FieldType!=fieldType || f.IsStatic)throw new InvalidOperationException("Pinned field changed: "+name);return f;}
    static Func<object,NetNode.RemoteWeaponSnapshot> MakeSnapshotReader(Type state)
    {
        var fields=new[]{RequiredField(state,"<Id>k__BackingField",typeof(int)),RequiredField(state,"WeaponKind",typeof(string)),
            RequiredField(state,"WeaponSlot",typeof(int)),RequiredField(state,"WeaponPermanentId",typeof(int)),RequiredField(state,"WeaponAmmo",typeof(int))};
        var reader=new DynamicMethod("CoopInventoryFix_ReadCommitted",typeof(NetNode.RemoteWeaponSnapshot),new[]{typeof(object)},typeof(InventorySlotPatch),true);
        var il=reader.GetILGenerator();foreach(var f in fields){il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,state);il.Emit(OpCodes.Ldfld,f);}
        il.Emit(OpCodes.Call,typeof(InventorySlotPatch).GetMethod(nameof(Snapshot),Static));il.Emit(OpCodes.Ret);
        return (Func<object,NetNode.RemoteWeaponSnapshot>)reader.CreateDelegate(typeof(Func<object,NetNode.RemoteWeaponSnapshot>));
    }
    static NetNode.RemoteWeaponSnapshot Snapshot(int id,string kind,int slot,int permanent,int ammo)
        =>new(id,kind,slot,permanent,ammo==int.MinValue?(int?)null:ammo);
    static bool LocalLoad(CodeInstruction c)=>c.opcode==OpCodes.Ldloc || c.opcode==OpCodes.Ldloc_S || c.opcode==OpCodes.Ldloc_0 || c.opcode==OpCodes.Ldloc_1 || c.opcode==OpCodes.Ldloc_2 || c.opcode==OpCodes.Ldloc_3;
    static CodeInstruction CloneLoad(CodeInstruction c)=>new(c.opcode,c.operand);
    static IEnumerable<CodeInstruction> CommitTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();var found=Enumerable.Range(0,code.Count).Where(i=>code[i].opcode==OpCodes.Stfld && Equals(code[i].operand,HasWeaponUpdate)).ToArray();
        if(found.Length!=1)throw new InvalidOperationException("Expected one committed inventory flag write");
        int p=found[0];if(p<2 || code[p-1].opcode!=OpCodes.Ldc_I4_1 || !LocalLoad(code[p-2]))throw new InvalidOperationException("Inventory commit anchor changed");
        code.InsertRange(p+1,new[]{CloneLoad(code[p-2]),new CodeInstruction(OpCodes.Ldarg_0),new CodeInstruction(OpCodes.Call,typeof(InventorySlotPatch).GetMethod(nameof(Committed),Static))});
        return code;
    }
    static IEnumerable<CodeInstruction> ConsumeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();var add=typeof(List<NetNode.RemoteWeaponSnapshot>).GetMethod("Add");
        var found=Enumerable.Range(0,code.Count).Where(i=>code[i].opcode==OpCodes.Callvirt && Equals(code[i].operand,add)).ToArray();
        if(found.Length!=1)throw new InvalidOperationException("Expected one original inventory list Add");
        int p=found[0];
        if(p+3>=code.Count || !LocalLoad(code[p+1]) || code[p+2].opcode!=OpCodes.Ldc_I4_0 || code[p+3].opcode!=OpCodes.Stfld || !Equals(code[p+3].operand,HasWeaponUpdate) || code[p].labels.Count!=0 || code[p].blocks.Count!=0)
            throw new InvalidOperationException("Inventory consumption/reset anchor changed");
        var state=CloneLoad(code[p+1]);code[p]=new CodeInstruction(OpCodes.Call,typeof(InventorySlotPatch).GetMethod(nameof(AppendCommitted),Static));
        code.InsertRange(p,new[]{state,new CodeInstruction(OpCodes.Ldarg_0)});return code;
    }
    static IEnumerable<CodeInstruction> ResetTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();var found=Enumerable.Range(0,code.Count).Where(i=>code[i].opcode==OpCodes.Stfld && Equals(code[i].operand,HasPosition)).ToArray();
        if(found.Length!=1)throw new InvalidOperationException("Expected one marker-reset position gate write");
        int p=found[0];if(p<2 || code[p-1].opcode!=OpCodes.Ldc_I4_0 || !LocalLoad(code[p-2]))throw new InvalidOperationException("Marker reset anchor changed");
        code.InsertRange(p+1,new[]{CloneLoad(code[p-2]),new CodeInstruction(OpCodes.Ldarg_0),new CodeInstruction(OpCodes.Call,typeof(InventorySlotPatch).GetMethod(nameof(ResetCommitted),Static))});
        return code;
    }
    static void Committed(object state,NetNode node)
    {
        if(!IsReady || Disabled)return;
        if(!Monitor.IsEntered(ReadSync(node)))throw new InvalidOperationException("Inventory commit left original lock");
        var snapshot=ReadSnapshot(state);Interlocked.Increment(ref Commits);
        // An unknown/legacy slot cannot be assigned to either equipped slot.
        // Preserve the original single-latest behavior across that barrier.
        if(snapshot.Slot!=0 && snapshot.Slot!=1){PendingByState.Remove(state);Interlocked.Increment(ref Barriers);return;}
        PendingByState.GetValue(state,static _=>new Pending()).Put(snapshot);
    }
    static void AppendCommitted(List<NetNode.RemoteWeaponSnapshot> output,NetNode.RemoteWeaponSnapshot original,object state,NetNode node)
    {
        if(!IsReady || Disabled){output.Add(original);return;}
        if(!Monitor.IsEntered(ReadSync(node)))throw new InvalidOperationException("Inventory consume left original lock");
        if(!PendingByState.TryGetValue(state,out var pending)){output.Add(original);return;}
        pending.Append(output);PendingByState.Remove(state);
        // The original method next clears HasWeaponUpdate while still locked.
    }
    static void ResetCommitted(object state,NetNode node)
    {
        if(!IsReady || Disabled)return;
        if(!Monitor.IsEntered(ReadSync(node)))throw new InvalidOperationException("Inventory marker reset left original lock");
        // The same remote object survives this world boundary. Forget only the
        // extra slot window; original last snapshot/flag remain its fallback.
        if(PendingByState.Remove(state))Interlocked.Increment(ref MarkerResets);
    }
}
