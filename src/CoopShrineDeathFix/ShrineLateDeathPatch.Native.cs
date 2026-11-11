using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using dc.en;
using dc.en.inter;

public static partial class ShrineLateDeathPatch
{
    static readonly List<(EventInfo Event,Delegate Hook)> NativeHooks=new();
    static Action<object,Mob> CallFFF,CallNativeOriginal;
    static Type NativeOriginalType;
    static DynamicMethod RetireAdapter;
    sealed class RetireScope {public Receipt Receipt;public object Original;public bool Used;}
    static IEnumerable<CodeInstruction> ReplaceNativeCall(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();var calls=Enumerable.Range(0,code.Count).Where(i=>code[i].Calls(FFFNativeBridge)).ToArray();
        if(calls.Length!=1)throw new InvalidOperationException("Exactly one composed FFF native death call required");
        int at=calls[0];
        if(at<2||code[at-2].opcode!=OpCodes.Ldarg_0||code[at-1].opcode!=OpCodes.Ldarg_1)
            throw new InvalidOperationException("Pinned native death stack changed");
        // Preserve original labels/EH markers and both untouched tail paths.
        code[at].operand=typeof(ShrineLateDeathPatch).GetMethod(nameof(DispatchNativeOriginal),S);
        return code;
    }
    static void BuildNativeBridges()
    {
        NativeOriginalType=DeathHook.GetParameters()[0].ParameterType;
        var invoke=NativeOriginalType.GetMethod("Invoke");
        if(invoke?.ReturnType!=typeof(void)||!invoke.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(Mob)}))
            throw new InvalidOperationException("Native original death ABI changed");
        Action<object,Mob> Bridge(string name,MethodInfo target,bool cast)
        {
            var dm=new DynamicMethod(name,typeof(void),new[]{typeof(object),typeof(Mob)},typeof(ShrineLateDeathPatch),true);var il=dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);if(cast)il.Emit(OpCodes.Castclass,NativeOriginalType);il.Emit(OpCodes.Ldarg_1);
            il.Emit(cast?OpCodes.Callvirt:OpCodes.Call,target);il.Emit(OpCodes.Ret);return (Action<object,Mob>)dm.CreateDelegate(typeof(Action<object,Mob>));
        }
        CallFFF=Bridge("LateShrine_OriginalFFFGuard",FFFNativeBridge,false);
        CallNativeOriginal=Bridge("LateShrine_UnchangedNativeOriginal",invoke,true);
        RetireAdapter=new DynamicMethod("LateShrine_ExactPlainDestroy",typeof(void),new[]{typeof(RetireScope),typeof(Mob)},typeof(ShrineLateDeathPatch),true);
        var body=RetireAdapter.GetILGenerator();body.Emit(OpCodes.Ldarg_0);body.Emit(OpCodes.Ldarg_1);
        body.Emit(OpCodes.Call,typeof(ShrineLateDeathPatch).GetMethod(nameof(RetireExact),S));body.Emit(OpCodes.Ret);
    }
    static bool RetirementReady(Receipt r,Mob self)
    {
        if(r==null||r.Proof.Terminal||r.Proof.RetirementCalls!=0||r.Proof.OriginalPlainCalls!=0||!Current(r)||!SourceCurrent(r)||
            Pointer(self)!=r.ReplicaPointer||self.destroyed||self.onDieDone||
            self.life>0||!self.initDone||self.elite||self.type?.ToString()!="Archer"||Pointer(self._level)!=r.LevelPointer||
            (int)Field("authoritativeClientMobDieDepth")<=0||
            !(bool)AcceptedIntent.Invoke(null,new object[]{r.Generation,r.Id})||
            (bool)OriginalIsDead.Invoke(LedgerField.GetValue(null),new object[]{r.Generation,r.Id}))return false;
        // The original Hook tail removes by managed reference. A distinct proxy
        // of the same pointer is not enough to guarantee canonical map cleanup.
        return Forward.TryGetValue(r.Id,out var owner)&&ReferenceEquals(owner,self)&&Pointer(owner)==r.ReplicaPointer&&Reverse.TryGetValue(owner,out var id)&&id==r.Id&&
            Forward.Count(p=>Pointer(p.Value)==r.ReplicaPointer)==1&&Reverse.Count(p=>Pointer(p.Key)==r.ReplicaPointer)==1&&
            Tracked.Cast<Mob>().Count(m=>Pointer(m)==r.ReplicaPointer)==1;
    }
    static void PlainOriginal(Receipt r)
    {r.Proof.OriginalPlainCalls++;r.Proof.Terminal=true;Interlocked.Increment(ref OriginalPlainBodies);Interlocked.Increment(ref Rejected);}
    static void RetireExact(RetireScope scope,Mob self)
    {
        bool retire;
        lock(Sync)
        {
            if(scope.Used)throw new InvalidOperationException("Original native death delegate invoked twice");
            scope.Used=true;retire=RetirementReady(scope.Receipt,self);
            if(!retire)PlainOriginal(scope.Receipt);
            else
            {
                var r=scope.Receipt;r.Proof.RetirementCalls++;
                try
                {
                    // Native1139→861: ordinary destruction only, without native
                    // Mob.onDie, loot, kill stats, combos or onMobDeath rewards.
                    // Capture the already-proven source's current phase only for
                    // diagnostics. Activation may precede this accepted death.
                    bool? phaseUsed=null,phasePaired=null;int phaseReturns=r.Proof.PresentationReturns;
                    try{phaseUsed=r.Shrine.used;phasePaired=Read<Mob>(r.Pair,"Native")!=null;}catch{}
                    DestroyPlain(self);
                    r.Proof.RetirementReturns++;
                    if(!self.destroyed)throw new InvalidOperationException("Exact plain retirement did not complete");
                    r.Proof.RetirementCompleted++;Interlocked.Increment(ref PlainCompletions);
                    try{Serilog.Log.Information("[CoopShrineDeathFix] Exact plain retirement id={Id} generation={Generation} shrineUsed={ShrineUsed} nativePaired={NativePaired} presentationReturns={PresentationReturns}",r.Id,r.Generation,phaseUsed,phasePaired,phaseReturns);}catch{}
                }
                catch
                {
                    r.Proof.RetirementFaults++;r.Proof.Terminal=true;Interlocked.Increment(ref RetirementFaults);throw;
                }
            }
        }
        // A context replacement before the original call preserves the original
        // delegate. It also permanently disqualifies this receipt from replay.
        if(!retire)CallNativeOriginal(scope.Original,self);
    }
    static void DestroyPlain(Mob self)=>self.destroy();
    static void DispatchNativeOriginal(object orig,Mob self)
    {
        if(!Published||Disabled||self==null){CallFFF(orig,self);return;}
        Receipt selected=null;bool retire=false,late=false;
        try
        {
            lock(Sync)
            {
                var matches=Receipts.Where(r=>Pointer(self)==r.ReplicaPointer||r.Proof.Enrolled&&Pointer(self)==r.NativePointer).ToArray();
                if(matches.Length==1)
                {
                    selected=matches[0];
                    if(Pointer(self)==selected.ReplicaPointer)
                    {
                        retire=RetirementReady(selected,self);
                        if(!retire)PlainOriginal(selected);
                    }
                    else if(Current(selected)&&OriginalDead(selected)&&Pending.Contains(selected.Native)&&NoMap(selected.NativePointer)&&
                        (int)Field("authoritativeClientMobDieDepth")>0)
                    {late=true;selected.LateNativeCalls++;Interlocked.Increment(ref LateCalls);}
                }
            }
        }
        catch(Exception error){if(selected!=null)selected.Proof.Terminal=true;Report(error,false);}
        if(retire)
        {
            var scope=new RetireScope {Receipt=selected,Original=orig};
            // Keep FFF's actual original-delegate call and exception observer.
            // It invokes a compatible typed replacement, while the unchanged
            // original Hook tail performs its own MarkDead and map cleanup.
            CallFFF(RetireAdapter.CreateDelegate(NativeOriginalType,scope),self);return;
        }
        try{CallFFF(orig,self);if(late){selected.LateNativeReturns++;Interlocked.Increment(ref LateReturns);}}
        catch{if(late){selected.LateNativeFaults++;Interlocked.Increment(ref LateFaults);}throw;}
    }
    static Receipt BeforePresentation(EliteShrine shrine)
    {
        if(!Published||Disabled||Receipts.Count==0||shrine==null)return null;
        try
        {
            lock(Sync)
            {
                var matches=Receipts.Where(r=>r.ShrinePointer==Pointer(shrine)&&shrine.used&&Current(r)&&SourceCurrent(r)&&Read<Mob>(r.Pair,"Native")==null).ToArray();
                return matches.Length==1?matches[0]:null;
            }
        }
        catch(Exception error){Report(error,false);return null;}
    }
    static void AfterPresentation(Receipt r,bool returned)
    {
        if(r==null)return;
        try
        {
            lock(Sync)
            {
                var native=Read<Mob>(r.Pair,"Native");var pointer=Read<IntPtr>(r.Pair,"NativePointer");
                if(native==null)return;r.Proof.PresentationCalls++;
                if(!returned){r.Proof.PresentationFaults++;return;}
                if(!Current(r)||!SourceCurrent(r)||!r.Shrine.used||pointer==IntPtr.Zero||Pointer(native)!=pointer||pointer==r.ReplicaPointer||Pointer(native._level)!=r.LevelPointer)
                {r.Proof.PresentationFaults++;return;}
                r.Native=native;r.NativePointer=pointer;r.Proof.PresentationReturns++;Interlocked.Increment(ref CompletedPresentation);
            }
        }
        catch(Exception error){r.Proof.ObserverErrors++;Report(error,false);}
    }
    static Delegate PresentationAdapter(Type handler)
    {
        var invoke=handler.GetMethod("Invoke");var types=invoke?.GetParameters().Select(p=>p.ParameterType).ToArray();
        var expected=new[]{typeof(EliteShrine),typeof(dc.String),typeof(int)};
        if(invoke?.ReturnType!=typeof(void)||types==null||types.Length!=4||!types.Skip(1).SequenceEqual(expected)||
            types[0].GetMethod("Invoke")?.ReturnType!=typeof(void)||!types[0].GetMethod("Invoke").GetParameters().Select(p=>p.ParameterType).SequenceEqual(expected))
            throw new InvalidOperationException("Native presentation ABI changed");
        var dm=new DynamicMethod("LateShrine_PresentationComplete",typeof(void),types,typeof(ShrineLateDeathPatch),true);
        var il=dm.GetILGenerator();var receipt=il.DeclareLocal(typeof(Receipt));var returned=il.DeclareLocal(typeof(bool));
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,typeof(ShrineLateDeathPatch).GetMethod(nameof(BeforePresentation),S));il.Emit(OpCodes.Stloc,receipt);
        il.BeginExceptionBlock();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Ldarg_3);il.Emit(OpCodes.Callvirt,types[0].GetMethod("Invoke"));
        il.Emit(OpCodes.Ldc_I4_1);il.Emit(OpCodes.Stloc,returned);
        il.BeginFinallyBlock();il.Emit(OpCodes.Ldloc,receipt);il.Emit(OpCodes.Ldloc,returned);il.Emit(OpCodes.Call,typeof(ShrineLateDeathPatch).GetMethod(nameof(AfterPresentation),S));
        il.EndExceptionBlock();il.Emit(OpCodes.Ret);return dm.CreateDelegate(handler);
    }
    static void SubscribeHooks()
    {
        var ev=typeof(Mob).Assembly.GetType("dc.en.inter.Hook_EliteShrine",true).GetEvent("onCooldownEnd")??throw new InvalidOperationException("Native presentation event missing");
        var hook=PresentationAdapter(ev.EventHandlerType);NativeHooks.Add((ev,hook));ev.AddEventHandler(null,hook);
    }
    static void UnsubscribeHooks()
    {
        Exception failure=null;foreach(var hook in NativeHooks)try{hook.Event.RemoveEventHandler(null,hook.Hook);}catch(Exception error){failure??=error;}
        NativeHooks.Clear();if(failure!=null)throw failure;
    }
}
