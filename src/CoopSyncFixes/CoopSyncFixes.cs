using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ModCore.Mods;
using dc.en;
using DeadCellsMultiplayerMod;
using Serilog;
using System.IO;
using System.Security.Cryptography;

// A separate, version-pinned repair package. Original DLL preserved; both peers need the extension.
public sealed partial class CoopSyncFixes : ModBase
{
    internal static readonly Type Mobs = typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization", true);
    internal static readonly Type Lobby = typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.LobbySession", true);
    internal static readonly Type Coordinator = typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.RunLaunchCoordinator", true);
    internal static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<int, NetNode.MobDie> PendingDeaths = new();
    private static readonly HashSet<int> SeenStates = new();
    public CoopSyncFixes(ModInfo info) : base(info) { }
    public static bool RepairInitializationComplete {get;private set;}

    // These five keep their names, signatures and results. What they need between calls is kept in the nested Csf* holders
    // at the end of this file (gc-opt PATTERN.md): no member lookup, environment read or new box per call.
    internal static object Field(string name) => CsfByName.Mobs.Field(name);
    internal static object Invoke(string name, params object[] arguments) => CsfByName.Mobs.Invoke(name, arguments);
    internal static object Gate => CsfGate.Sync;
    internal static bool Current(int generation) => CsfMobs.PacketCurrent(generation);
    private static bool Disabled(string name) => CsfSwitches.Off(name);
    private static void Patch(Harmony harmony, Type type, string name, string prefix = null, string postfix = null)
    {
        var method = type.GetMethod(name, Flags) ?? throw new MissingMethodException(type.FullName, name);
        harmony.Patch(method,
            prefix == null ? null : new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(prefix, Flags)),
            postfix == null ? null : new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(postfix, Flags)));
    }
    public override void Initialize()
    {
        RepairInitializationComplete=false;
        var original = typeof(ModEntry).Assembly;
        var hash = global::CoopPackRuntime.PackPins.Sha256(original.Location);
        if (original.GetName().Version != new Version(0, 5, 1, 0) ||
            hash != "378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28")
            throw new InvalidOperationException("CoopSyncFixes requires the pinned 0.5.1 multiplayer assembly.");
        var harmony = new Harmony("local.deadcells.syncfixes.1");
        if (!Disabled("IDENTITY")) Patch(harmony, Mobs, "AreLikelySameNativeMobProxy", nameof(NativeIdentity));
        if (!Disabled("HEALTH")) Patch(harmony, Mobs, "ApplyIncomingHostMobStates", null, nameof(MergeReliableHealth));
        if (!Disabled("DEATH"))
        {
            Patch(harmony, Mobs, "ApplyIncomingMobDies", nameof(RetainUnresolvedDeaths));
            Patch(harmony, Mobs, "FlushPendingClientAuthoritativeDeaths", nameof(RetryUnresolvedDeaths));
            Patch(harmony, Mobs, "ResetMobTrackingLocked", nameof(ResetDeaths));
        }
        if (!Disabled("READY")) Patch(harmony, Lobby, "ReceiveRunLevelReadyPayload", nameof(ValidateReadyIdentity));
        InitializeEnemyExtensions(harmony);
        InitializeRegistryBatches(harmony);
        InitializeCatchup(harmony);
        InitializeLifecycleFence(harmony);
        InitializeSleepingTransforms(harmony);
        InitializeSleepingMotion(harmony);
        InitializeJoinProtocol(harmony);
        InitializeWorldJoin(harmony);
        InitializeRoutes();
        InitializeGraphLinks(harmony);
        InitializeBranches(harmony);
        InitializeManifests(harmony);
        InitializeWireEpoch(harmony);
        InitializePlayerColors();
        InitializePlayerSafety(harmony);
        InitializeExitMembership(harmony);
        InitializeDoorAndDeathBoundaries(harmony);
        InitializeEliteDeathCompletion(harmony);
        InitializeRemoteWeaponIdentity(harmony);
        InitializeRemoteWeaponEquip(harmony);
        InitializeCinematicPresentation(harmony);
        InitializeExperimentalElevators(harmony);
        Log.Information("[CoopSyncFixes] v1.3.0 integration candidate loaded. identity={Identity} health={Health} death={Death} ready={Ready}",
            !Disabled("IDENTITY"), !Disabled("HEALTH"), !Disabled("DEATH"), !Disabled("READY"));
        RepairInitializationComplete=true;
    }

    // Proximity is useful for matching a host spawn to an unbound client replica, but is never
    // evidence that two managed wrappers refer to the same native object on one machine.
    private static bool NativeIdentity(Mob left, Mob right, ref bool __result)
    {
        __result = false;
        if (left == null || right == null) return false;
        if (ReferenceEquals(left, right)) { __result = true; return false; }
        try
        {
            if (left.destroyed || right.destroyed ||
                !CsfMobs.LevelMatches(left._level) ||
                !CsfMobs.LevelMatches(right._level)) return false;
            var pointer = left.HashlinkPointer;
            __result = pointer != IntPtr.Zero && pointer == right.HashlinkPointer;
        }
        catch { __result = false; } // Preserve the original dying-wrapper failure boundary.
        return false;
    }

    // A newer unreliable movement frame must not erase an older (but ordered/reliable) HP update.
    // Preserve every movement/animation field and merge only the already accepted HP/max HP.
    private static void MergeReliableHealth(IReadOnlyList<NetNode.MobStateSnapshot> states)
    {
        if (states == null || ModEntry._net == null || ModEntry._net.IsHost) return;
        lock (Gate)
        {
            var forward = CsfMobs.IdToMob;
            var reverse = CsfMobs.MobToId;
            var reported = CsfMobs.ClientLastReportedMobLife;
            SeenStates.Clear();
            for (var i = states.Count - 1; i >= 0; --i)
            {
                var state = states[i];
                if (!Current(state.Generation) || !SeenStates.Add(state.Index) || !forward.TryGetValue(state.Index, out var mob)) continue;
                // The accepted target (clientMobTargets) is a private struct of the base mod: it is read and rebuilt by the
                // two emitted accessors, through the same constructor and with the same field values as before.
                if (mob == null || !Bound(reverse, mob, state.Index) ||
                    !CsfMobs.TargetState(mob, out var life, out var maxLife, out _, out _) ||
                    !reported.TryGetValue(mob, out var last) || last != state.Life) continue;
                if (life == state.Life && maxLife == state.MaxLife) continue;
                CsfMobs.SetTargetLife(mob, state.Life, state.MaxLife);
            }
            SeenStates.Clear();
        }
        // Stays a local function: it takes the metadata place of the removed reflective one, so the number of methods of
        // the outer class, and with it the method tokens inside the pinned SaltBranchIdentity, does not move.
        static bool Bound(Dictionary<Mob, int> registry, Mob mob, int id) => registry.TryGetValue(mob, out var bound) && bound == id;
    }

    private static void RetainUnresolvedDeaths(IReadOnlyList<NetNode.MobDie> dies)
    {
        if (dies == null || ModEntry._net == null || !ModEntry._net.IsAlive || ModEntry._net.IsHost) return;
        lock (Gate)
        {
            var forward = CsfMobs.IdToMob;
            var reverse = CsfMobs.MobToId;
            foreach (var die in dies)
            {
                if (die.MobIndex <= 0 || !Current(die.Generation)) continue;
                if (ConfirmedDead(die.Generation, die.MobIndex)) { PendingDeaths.Remove(die.MobIndex); continue; }
                forward.TryGetValue(die.MobIndex, out var mob);
                if (mob != null && !mob.destroyed && reverse.TryGetValue(mob, out var bound) && bound == die.MobIndex) continue;
                // Keep an authoritative identity until registration or level reset. No spatial guessing.
                // The cap is fail-visible; avoid silently turning an overflow into a false "fixed" claim.
                if (PendingDeaths.Count >= 8192 && !PendingDeaths.ContainsKey(die.MobIndex))
                    throw new InvalidOperationException("CoopSyncFixes pending death capacity exceeded.");
                PendingDeaths[die.MobIndex] = die;
            }
        }
    }
    private static bool ConfirmedDead(int generation, int id) => CsfMobs.LedgerDead(generation, id);
    private static void ResetDeaths() { lock (Gate) PendingDeaths.Clear(); }
    private static void RetryUnresolvedDeaths()
    {
        if (ModEntry._net == null || !ModEntry._net.IsAlive || ModEntry._net.IsHost ||
            CsfMobs.SyncQuiesced()) return;
        List<NetNode.MobDie> retry = null;
        lock (Gate)
        {
            if (PendingDeaths.Count == 0) return;
            var forward = CsfMobs.IdToMob;
            var reverse = CsfMobs.MobToId;
            // The loop removes entries, so it walks a copy taken first, in the same order as before. The copy goes into a
            // list that is kept between calls; it is taken out of its slot while in use, so a nested call would get its own.
            var pending = CsfScratch.Deaths ?? new List<KeyValuePair<int, NetNode.MobDie>>();
            CsfScratch.Deaths = null;
            pending.AddRange(PendingDeaths);
            for (var i = 0; i < pending.Count; i++)
            {
                var pair = pending[i];
                var die = pair.Value;
                if (!Current(die.Generation) || ConfirmedDead(die.Generation, pair.Key)) { PendingDeaths.Remove(pair.Key); continue; }
                forward.TryGetValue(pair.Key, out var mob);
                if (mob == null || mob.destroyed || !reverse.TryGetValue(mob, out var bound) || bound != pair.Key ||
                    !CsfMobs.LevelMatches(mob._level)) continue;
                (retry ??= new List<NetNode.MobDie>()).Add(die);
                PendingDeaths.Remove(pair.Key);
            }
            pending.Clear();
            CsfScratch.Deaths = pending;
        }
        // Use the mod's original authority/deferred-death/idempotency path, outside the registry lock.
        if (retry != null) Invoke("ApplyIncomingMobDies", retry);
    }

    private static object Property(object value, string name) => value.GetType().GetProperty(name).GetValue(value);
    private static bool ValidateReadyIdentity(string payload)
    {
        var codec = typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.RunLaunchWireCodec", true);
        var args = new object[] { payload, null, null };
        if (!(bool)codec.GetMethod("TryDecodeReady", Flags).Invoke(null, args) || args[1] == null) return true;
        var ready = args[1];
        var role = Coordinator.GetProperty("CurrentRole", Flags).GetValue(null).ToString();
        var getter = role == "Host" ? "GetCurrentHostDescriptor" : role == "Client" ? "GetCurrentRemoteDescriptor" : null;
        var descriptor = getter == null ? null : Coordinator.GetMethod(getter, Flags).Invoke(null, null);
        var valid = descriptor != null && Equals(Property(ready, "SessionId"), Property(descriptor, "SessionId")) &&
            Equals(Property(ready, "RunId"), Property(descriptor, "RunId")) &&
            Equals(Property(ready, "Sequence"), Property(descriptor, "Sequence"));
        if (!valid) return false;
        if (Disabled("JOIN")) return true;
        // Readiness is a completion acknowledgement, not evidence of late joining.
        Coordinator.GetMethod("ReceiveRemoteReady", Flags).Invoke(null, new object[] { ready });
        ArmExplicitJoinContext();
        return false;
    }

    // ---- What Field / Invoke / Gate / Current / Disabled / ConfirmedDead keep between calls (gc-opt PATTERN.md) ----
    // Every holder is a nested class: its initializer runs at its own first use, whatever the order of the static fields
    // of the 38 partial files. No holder uses a member of the outer class (field, property or method) or of any other type
    // of this assembly that is not a holder, and no static field initializer of the outer class uses a holder or a helper.

    // COOP_SYNC_DISABLE_<name>, read once per name. No code in a game process sets or changes a COOP_* variable (runners
    // and the launcher set them before the process starts), so the first answer is the answer of every later read.
    private static class CsfSwitches
    {
        private static readonly ConcurrentDictionary<string, bool> Known = new ConcurrentDictionary<string, bool>();
        internal static bool Off(string name)
        {
            if (name == null) return Environment.GetEnvironmentVariable("COOP_SYNC_DISABLE_" + name) == "1";
            if (Known.TryGetValue(name, out var off)) return off;
            return Known.GetOrAdd(name, Environment.GetEnvironmentVariable("COOP_SYNC_DISABLE_" + name) == "1");
        }
    }

    // The two boxed bools every helper hands out, and the answer of a by-name stub that leaves a call to reflection.
    // No caller keeps, locks, mutates or compares by reference a boxed helper result: each is unboxed where it is returned.
    private static class CsfBoxes
    {
        internal static readonly object True = true, False = false, Mismatch = new object();
    }

    // The static members of one type by name: Field(name) and Invoke(name, arguments) with the lookup done once per name.
    // A name that does not resolve is never cached and takes the original expression, so it fails exactly as before.
    private sealed class CsfStatics
    {
        private const BindingFlags Members = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Type owner;
        private readonly ConcurrentDictionary<string, CsfFieldSlot> fields = new ConcurrentDictionary<string, CsfFieldSlot>();
        private readonly ConcurrentDictionary<string, CsfCallSlot> calls = new ConcurrentDictionary<string, CsfCallSlot>();
        internal CsfStatics(Type owner) { this.owner = owner; }

        internal object Field(string name)
        {
            if (name != null && fields.TryGetValue(name, out var slot)) return slot.Read();
            var field = name == null ? null : owner.GetField(name, Members);
            if (field == null) return owner.GetField(name, Members).GetValue(null);
            return fields.GetOrAdd(name, new CsfFieldSlot(field)).Read();
        }

        internal object Invoke(string name, object[] arguments)
        {
            if (name != null && calls.TryGetValue(name, out var slot)) return slot.Call(arguments);
            var method = name == null ? null : owner.GetMethod(name, Members);
            if (method == null) return owner.GetMethod(name, Members).Invoke(null, arguments);
            return calls.GetOrAdd(name, new CsfCallSlot(method)).Call(arguments);
        }
    }

    // One static field. Every read returns the value the field has at that moment; nothing but the box is reused.
    private sealed class CsfFieldSlot
    {
        private readonly Func<object> any;     // a reference as it is; a value type other than bool and int in a new box, as before
        private readonly Func<bool> flag;      // bool: one of the two shared boxes
        private readonly Func<int> number;     // int: the box handed out last, for as long as the field keeps that value
        private readonly FieldInfo reflected;  // pointer-like field types and constant strings stay with FieldInfo.GetValue
        private object last;                   // int: that box; constant number: its box (a constant has no storage and cannot change)

        internal CsfFieldSlot(FieldInfo field)
        {
            var type = field.FieldType;
            // A constant string comes back from FieldInfo.GetValue as a new object at every call: left as it is.
            if (field.IsLiteral) { if (type.IsValueType) last = field.GetValue(null); else reflected = field; }
            else if (type.IsPointer || type.IsFunctionPointer || type.IsByRef || type.IsByRefLike) reflected = field;
            else if (type == typeof(bool)) flag = CsfStubs.StaticGetter<bool>(field);
            else if (type == typeof(int)) number = CsfStubs.StaticGetter<int>(field);
            else any = CsfStubs.StaticGetter<object>(field);
        }

        internal object Read()
        {
            try
            {
                if (any != null) return any();
                if (flag != null) return flag() ? CsfBoxes.True : CsfBoxes.False;
                if (number != null)
                {
                    // Threads may race here without a lock: a box is immutable, each reader compares the box it loaded with
                    // the value it has just read, and whichever box is stored last is as good as any other.
                    var value = number();
                    var box = System.Threading.Volatile.Read(ref last);
                    if (box != null && (int)box == value) return box;
                    box = value;
                    System.Threading.Volatile.Write(ref last, box);
                    return box;
                }
            }
            // The only thing a getter can throw is the failed type initializer of the field's owner. FieldInfo.GetValue hands
            // that on inside a TargetInvocationException, at every call.
            catch (TypeInitializationException e) { throw new TargetInvocationException(e); }
            return reflected != null ? reflected.GetValue(null) : last;
        }
    }

    // One static method found by name. The stub takes the calls MethodInfo.Invoke would pass through unchanged and gives
    // the same result or the same TargetInvocationException; every other argument list goes to MethodInfo.Invoke itself.
    private sealed class CsfCallSlot
    {
        private readonly MethodInfo method;
        private readonly Func<object[], object> stub;   // null: by-ref, pointer or nullable in the signature
        private readonly int count;

        internal CsfCallSlot(MethodInfo method)
        {
            this.method = method;
            count = method.GetParameters().Length;
            stub = CsfStubs.ByName(method);
        }

        internal object Call(object[] arguments)
        {
            if (stub != null && arguments != null && arguments.Length == count)
            {
                var result = stub(arguments);
                if (!ReferenceEquals(result, CsfBoxes.Mismatch)) return result;
            }
            return method.Invoke(null, arguments);
        }
    }

    // Emitted accessors. A method is entered through its entry-point address, loaded as a plain number: the JIT cannot
    // inline such a call, so a Harmony detour on the target applies as it does to MethodInfo.Invoke. Whatever the target
    // throws leaves the stub inside a TargetInvocationException, which is MethodInfo.Invoke's boundary.
    private static class CsfStubs
    {
        private const BindingFlags Members = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly ConstructorInfo Wrap = typeof(TargetInvocationException).GetConstructor(new[] { typeof(Exception) });
        private static readonly FieldInfo TrueBox = typeof(CsfBoxes).GetField(nameof(CsfBoxes.True), Members);
        private static readonly FieldInfo FalseBox = typeof(CsfBoxes).GetField(nameof(CsfBoxes.False), Members);
        private static readonly FieldInfo MismatchBox = typeof(CsfBoxes).GetField(nameof(CsfBoxes.Mismatch), Members);
        private static readonly FieldInfo MissingArgument = typeof(Type).GetField(nameof(Type.Missing));

        internal static Type Mobs() => typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization", true);

        // A static the base mod assigns in its type initializer and can never assign again (initonly): read once and kept.
        internal static T Fixed<T>(Type owner, string name) where T : class
        {
            var field = owner.GetField(name, Members) ?? throw new MissingFieldException(owner.FullName, name);
            if (!field.IsInitOnly || field.IsLiteral) throw new InvalidOperationException("Static field is not read-only: " + owner.FullName + "." + name);
            var value = field.GetValue(null) ?? throw new InvalidOperationException("Static field is empty: " + owner.FullName + "." + name);
            return value as T ?? throw new InvalidOperationException("Static field has another type: " + owner.FullName + "." + name);
        }

        // ldsfld, ret: the field's value at the moment of the call.
        internal static Func<T> StaticGetter<T>(FieldInfo field)
        {
            var type = field.FieldType;
            var boxed = type.IsValueType && typeof(T) != type;
            if (!field.IsStatic || field.IsLiteral || (boxed ? typeof(T) != typeof(object) : !typeof(T).IsAssignableFrom(type)))
                throw new InvalidOperationException("Static field has another type: " + field.DeclaringType.FullName + "." + field.Name);
            var stub = new DynamicMethod("Csf_Read_" + field.Name, typeof(T), Type.EmptyTypes, typeof(CoopSyncFixes), true);
            var il = stub.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, field);
            if (boxed) il.Emit(OpCodes.Box, type);
            il.Emit(OpCodes.Ret);
            return (Func<T>)stub.CreateDelegate(typeof(Func<T>));
        }

        internal static Func<T> StaticGetter<T>(Type owner, string name) =>
            StaticGetter<T>(owner.GetField(name, Members) ?? throw new MissingFieldException(owner.FullName, name));

        private static bool Odd(Type type) => type.IsByRef || type.IsPointer || type.IsFunctionPointer || type.IsByRefLike;

        private static void Target(ILGenerator il, MethodInfo method, Type[] parameters)
        {
            il.Emit(OpCodes.Ldc_I8, method.MethodHandle.GetFunctionPointer().ToInt64());
            il.Emit(OpCodes.Conv_I);
            il.EmitCalli(OpCodes.Calli, method.IsStatic ? CallingConventions.Standard : CallingConventions.HasThis, method.ReturnType, parameters, null);
        }

        private static void WrapAndEnd(ILGenerator il)
        {
            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Newobj, Wrap);
            il.Emit(OpCodes.Throw);
            il.EndExceptionBlock();
        }

        // object[] in, object out, for Invoke(name, arguments). An argument passes when it is what MethodInfo.Invoke hands
        // on untouched: a reference of the parameter type (or null), or a boxed value of exactly the parameter type.
        // Anything else (Type.Missing, null for a value type, a convertible value) returns the Mismatch marker before the
        // target is entered. Returns null for a signature that is left to reflection altogether.
        internal static Func<object[], object> ByName(MethodInfo method)
        {
            var parameters = method.GetParameters();
            if (!method.IsStatic || method.ContainsGenericParameters || (method.CallingConvention & CallingConventions.VarArgs) != 0 ||
                Odd(method.ReturnType)) return null;
            var types = new Type[parameters.Length];
            for (var i = 0; i < types.Length; i++)
            {
                types[i] = parameters[i].ParameterType;
                if (Odd(types[i]) || Nullable.GetUnderlyingType(types[i]) != null) return null;
            }
            var stub = new DynamicMethod("Csf_Invoke_" + method.Name, typeof(object), new[] { typeof(object[]) }, typeof(CoopSyncFixes), true);
            var il = stub.GetILGenerator();
            var mismatch = il.DefineLabel();
            var raw = il.DeclareLocal(typeof(object));
            var result = il.DeclareLocal(typeof(object));
            var typed = new LocalBuilder[types.Length];
            for (var i = 0; i < types.Length; i++)
            {
                typed[i] = il.DeclareLocal(types[i]);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem_Ref); il.Emit(OpCodes.Stloc, raw);
                if (types[i].IsValueType)
                {
                    il.Emit(OpCodes.Ldloc, raw); il.Emit(OpCodes.Isinst, types[i]); il.Emit(OpCodes.Brfalse, mismatch);
                    il.Emit(OpCodes.Ldloc, raw); il.Emit(OpCodes.Unbox_Any, types[i]); il.Emit(OpCodes.Stloc, typed[i]);
                }
                else
                {
                    var accepted = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, raw); il.Emit(OpCodes.Ldsfld, MissingArgument); il.Emit(OpCodes.Beq, mismatch);
                    il.Emit(OpCodes.Ldloc, raw); il.Emit(OpCodes.Isinst, types[i]); il.Emit(OpCodes.Stloc, typed[i]);
                    il.Emit(OpCodes.Ldloc, typed[i]); il.Emit(OpCodes.Brtrue, accepted);
                    il.Emit(OpCodes.Ldloc, raw); il.Emit(OpCodes.Brtrue, mismatch);
                    il.MarkLabel(accepted);
                }
            }
            il.BeginExceptionBlock();
            for (var i = 0; i < types.Length; i++) il.Emit(OpCodes.Ldloc, typed[i]);
            Target(il, method, types);
            if (method.ReturnType == typeof(void)) il.Emit(OpCodes.Ldnull);
            else if (method.ReturnType == typeof(bool))
            {
                var yes = il.DefineLabel(); var done = il.DefineLabel();
                il.Emit(OpCodes.Brtrue, yes); il.Emit(OpCodes.Ldsfld, FalseBox); il.Emit(OpCodes.Br, done);
                il.MarkLabel(yes); il.Emit(OpCodes.Ldsfld, TrueBox);
                il.MarkLabel(done);
            }
            else if (method.ReturnType.IsValueType) il.Emit(OpCodes.Box, method.ReturnType);
            il.Emit(OpCodes.Stloc, result);
            WrapAndEnd(il);
            il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
            il.MarkLabel(mismatch); il.Emit(OpCodes.Ldsfld, MismatchBox); il.Emit(OpCodes.Ret);
            return (Func<object[], object>)stub.CreateDelegate(typeof(Func<object[], object>));
        }

        // A typed delegate for a static method, or for an instance method of the reference type of target (bound to it).
        // No argument or result is converted or boxed; the signature must fit or the binding throws.
        internal static T Typed<T>(MethodInfo method, object target = null) where T : Delegate
        {
            var shape = typeof(T).GetMethod("Invoke");
            var given = shape.GetParameters();
            var wanted = method.GetParameters();
            var types = new Type[wanted.Length];
            var fits = !method.ContainsGenericParameters && (method.CallingConvention & CallingConventions.VarArgs) == 0 &&
                given.Length == wanted.Length && Fits(method.ReturnType, shape.ReturnType) &&
                (method.IsStatic ? target == null : target != null && !method.DeclaringType.IsValueType &&
                    method.DeclaringType.IsInstanceOfType(target) && (!method.IsVirtual || method.IsFinal || method.DeclaringType.IsSealed));
            for (var i = 0; fits && i < types.Length; i++)
            {
                types[i] = wanted[i].ParameterType;
                fits = Fits(given[i].ParameterType, types[i]);
            }
            if (!fits) throw new InvalidOperationException("Method has another signature: " + method.DeclaringType.FullName + "." + method.Name);
            var bound = method.IsStatic ? 0 : 1;
            var signature = new Type[bound + given.Length];
            if (bound == 1) signature[0] = method.DeclaringType;
            for (var i = 0; i < given.Length; i++) signature[bound + i] = given[i].ParameterType;
            var stub = new DynamicMethod("Csf_Call_" + method.Name, shape.ReturnType, signature, typeof(CoopSyncFixes), true);
            var il = stub.GetILGenerator();
            var result = shape.ReturnType == typeof(void) ? null : il.DeclareLocal(shape.ReturnType);
            il.BeginExceptionBlock();
            for (var i = 0; i < signature.Length; i++) il.Emit(OpCodes.Ldarg, (short)i);
            Target(il, method, types);
            if (result != null) il.Emit(OpCodes.Stloc, result);
            WrapAndEnd(il);
            if (result != null) il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Ret);
            return (T)(bound == 1 ? stub.CreateDelegate(typeof(T), target) : stub.CreateDelegate(typeof(T)));
        }

        internal static T Typed<T>(Type owner, string name) where T : Delegate =>
            Typed<T>(owner.GetMethod(name, Members) ?? throw new MissingMethodException(owner.FullName, name));

        // The same type, or a reference that needs no conversion.
        private static bool Fits(Type from, Type to) => from == to || !from.IsValueType && !to.IsValueType && !Odd(from) && !Odd(to) && to.IsAssignableFrom(from);

        private const BindingFlags Own = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // (instance) -> the value an instance field or property of owner has at the call; nothing is boxed. owner may be a
        // type that cannot be named here; for a field it may be a struct (the instance is then its box). A null instance
        // gives NullReferenceException and an instance of another type InvalidCastException before the member is touched.
        // What a property getter throws leaves inside a TargetInvocationException, as it does from PropertyInfo.GetValue.
        internal static Func<object, T> Reader<T>(Type owner, string name)
        {
            var field = owner.GetField(name, Own);
            var getter = field == null ? owner.GetProperty(name, Own)?.GetGetMethod(true) : null;
            if (field == null && getter == null) throw new MissingMemberException(owner.FullName, name);
            var type = field != null ? field.FieldType : getter.ReturnType;
            if (owner.ContainsGenericParameters || Odd(type) || !Fits(type, typeof(T)) || getter != null &&
                (owner.IsValueType || getter.GetParameters().Length != 0 || getter.IsVirtual && !getter.IsFinal && !owner.IsSealed))
                throw new InvalidOperationException("Member has another shape: " + owner.FullName + "." + name);
            var stub = new DynamicMethod("Csf_Get_" + name, typeof(T), new[] { typeof(object) }, typeof(CoopSyncFixes), true);
            var il = stub.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(owner.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, owner);
            if (field != null) { il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret); }
            else
            {
                var self = il.DeclareLocal(owner);
                var result = il.DeclareLocal(typeof(T));
                il.Emit(OpCodes.Stloc, self);
                // The entry-point call below does not test the instance: this does, as value.GetType() did at the call site.
                il.Emit(OpCodes.Ldloc, self); il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetType")); il.Emit(OpCodes.Pop);
                il.BeginExceptionBlock();
                il.Emit(OpCodes.Ldloc, self);
                Target(il, getter, Type.EmptyTypes);
                il.Emit(OpCodes.Stloc, result);
                WrapAndEnd(il);
                il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
            }
            return (Func<object, T>)stub.CreateDelegate(typeof(Func<object, T>));
        }

        // (instance, value): stores into an instance field of the class owner. The instance is tested as in Reader.
        internal static Action<object, T> Writer<T>(Type owner, string name)
        {
            var field = owner.GetField(name, Own) ?? throw new MissingFieldException(owner.FullName, name);
            if (owner.IsValueType || owner.ContainsGenericParameters || field.IsInitOnly || Odd(field.FieldType) || !Fits(typeof(T), field.FieldType))
                throw new InvalidOperationException("Member has another shape: " + owner.FullName + "." + name);
            var stub = new DynamicMethod("Csf_Set_" + name, typeof(void), new[] { typeof(object), typeof(T) }, typeof(CoopSyncFixes), true);
            var il = stub.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, owner); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            return (Action<object, T>)stub.CreateDelegate(typeof(Action<object, T>));
        }

        // clientMobTargets is a Dictionary<Mob, ClientMobState>. ClientMobState is a private struct of the base mod, so a
        // read through IDictionary boxes it and every field read boxes again. The two accessors below work on the typed
        // dictionary. Constructor parameters in order; each has a public field of the same name and type:
        private static readonly string[] TargetFields = { "X", "Y", "Dir", "Life", "MaxLife", "AnimPayload", "StatePayload", "Time", "Dx", "Dy",
            "ReceivedFrame", "ForcePositionSnap", "ForceVerticalPositionSnap" };
        private static readonly Type[] TargetTypes = { typeof(double), typeof(double), typeof(int), typeof(int), typeof(int), typeof(string), typeof(string),
            typeof(double), typeof(double), typeof(double), typeof(double), typeof(bool), typeof(bool) };
        private const int TargetX = 0, TargetY = 1, TargetLife = 3, TargetMaxLife = 4;   // positions in TargetFields

        private static ConstructorInfo TargetShape(object targets, out FieldInfo[] fields)
        {
            var map = targets.GetType();
            var arguments = map.IsGenericType && map.GetGenericTypeDefinition() == typeof(Dictionary<,>) ? map.GetGenericArguments() : null;
            var state = arguments != null && arguments[0] == typeof(Mob) && arguments[1].IsValueType ? arguments[1] : null;
            var constructor = state?.GetConstructor(TargetTypes);
            var parameters = constructor?.GetParameters();
            fields = new FieldInfo[TargetFields.Length];
            for (var i = 0; parameters != null && i < fields.Length; i++)
            {
                fields[i] = state.GetField(TargetFields[i]);
                if (fields[i] == null || fields[i].FieldType != TargetTypes[i] ||
                    !string.Equals(parameters[i].Name, TargetFields[i], StringComparison.OrdinalIgnoreCase)) parameters = null;
            }
            return parameters != null ? constructor : throw new InvalidOperationException("The accepted mob target has another shape: " + map);
        }

        // bool (mob, out life, out maxLife, out x, out y): one typed lookup; false, and zeros, when the mob has no accepted target.
        internal static CsfTargetRead TargetRead(object targets)
        {
            TargetShape(targets, out var fields);
            var map = targets.GetType();
            var stub = new DynamicMethod("Csf_TargetRead", typeof(bool), new[] { map, typeof(Mob), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(),
                typeof(double).MakeByRefType(), typeof(double).MakeByRefType() }, typeof(CoopSyncFixes), true);
            var il = stub.GetILGenerator();
            var value = il.DeclareLocal(fields[0].DeclaringType);
            var found = il.DeclareLocal(typeof(bool));
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloca, value);
            il.Emit(OpCodes.Callvirt, map.GetMethod("TryGetValue"));
            il.Emit(OpCodes.Stloc, found);
            // For a missing key TryGetValue leaves the default value in the local, so the stores below then write zeros.
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldloc, value); il.Emit(OpCodes.Ldfld, fields[TargetLife]); il.Emit(OpCodes.Stind_I4);
            il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldloc, value); il.Emit(OpCodes.Ldfld, fields[TargetMaxLife]); il.Emit(OpCodes.Stind_I4);
            il.Emit(OpCodes.Ldarg, (short)4); il.Emit(OpCodes.Ldloc, value); il.Emit(OpCodes.Ldfld, fields[TargetX]); il.Emit(OpCodes.Stind_R8);
            il.Emit(OpCodes.Ldarg, (short)5); il.Emit(OpCodes.Ldloc, value); il.Emit(OpCodes.Ldfld, fields[TargetY]); il.Emit(OpCodes.Stind_R8);
            il.Emit(OpCodes.Ldloc, found); il.Emit(OpCodes.Ret);
            return (CsfTargetRead)stub.CreateDelegate(typeof(CsfTargetRead), targets);
        }

        // bool (mob, life, maxLife): replaces the accepted target by a copy that differs in these two values only. The copy
        // is made by the struct's own constructor from the other eleven fields, which is what Activator.CreateInstance did
        // with the thirteen boxed values. false, and nothing written, when the mob has no accepted target.
        internal static CsfTargetWrite TargetWrite(object targets)
        {
            var constructor = TargetShape(targets, out var fields);
            var map = targets.GetType();
            var stub = new DynamicMethod("Csf_TargetWrite", typeof(bool), new[] { map, typeof(Mob), typeof(int), typeof(int) }, typeof(CoopSyncFixes), true);
            var il = stub.GetILGenerator();
            var value = il.DeclareLocal(fields[0].DeclaringType);
            var missing = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloca, value);
            il.Emit(OpCodes.Callvirt, map.GetMethod("TryGetValue"));
            il.Emit(OpCodes.Brfalse, missing);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
            for (var i = 0; i < fields.Length; i++)
            {
                if (i == TargetLife) il.Emit(OpCodes.Ldarg_2);
                else if (i == TargetMaxLife) il.Emit(OpCodes.Ldarg_3);
                else { il.Emit(OpCodes.Ldloc, value); il.Emit(OpCodes.Ldfld, fields[i]); }
            }
            il.Emit(OpCodes.Newobj, constructor);
            il.Emit(OpCodes.Callvirt, map.GetMethod("set_Item"));
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
            il.MarkLabel(missing);
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            return (CsfTargetWrite)stub.CreateDelegate(typeof(CsfTargetWrite), targets);
        }
    }

    private delegate bool CsfTargetRead(Mob mob, out int life, out int maxLife, out double x, out double y);
    private delegate bool CsfTargetWrite(Mob mob, int life, int maxLife);

    // Field(name) and Invoke(name, ...) of the base mod's MobsSynchronization.
    private static class CsfByName
    {
        internal static readonly CsfStatics Mobs = new CsfStatics(CsfStubs.Mobs());
    }

    // MobsSynchronization.Sync, the object behind every lock (Gate). The field is initonly and its only store is in the base
    // mod's type initializer (release IL line 145740), so the reference is read once.
    private static class CsfGate
    {
        internal static readonly object Sync = CsfStubs.Fixed<object>(CsfStubs.Mobs(), "Sync");
    }

    // What Current, ConfirmedDead and the four patch bodies of this file need from MobsSynchronization, and nothing else. All of
    // it is bound together at the first use of any member, and a binding that fails disables every member for good (the type
    // initializer has failed), so an entry that only other code uses does not belong here: see the three holders below.
    private static class CsfMobs
    {
        private static readonly Type Owner = CsfStubs.Mobs();

        // Collections the base mod creates once in its type initializer (initonly, one store each): the reference is kept.
        // Use them under lock (Gate), as the code that reached them through Field did.
        internal static readonly Dictionary<int, Mob> IdToMob = CsfStubs.Fixed<Dictionary<int, Mob>>(Owner, "IdToMob");
        internal static readonly Dictionary<Mob, int> MobToId = CsfStubs.Fixed<Dictionary<Mob, int>>(Owner, "MobToId");
        internal static readonly Dictionary<Mob, int> ClientLastReportedMobLife = CsfStubs.Fixed<Dictionary<Mob, int>>(Owner, "clientLastReportedMobLife");
        // Its value type is private to the base mod: only the non-generic view can be named here.
        internal static readonly IDictionary ClientMobTargets = CsfStubs.Fixed<IDictionary>(Owner, "clientMobTargets");
        internal static readonly CsfTargetRead TargetState = CsfStubs.TargetRead(ClientMobTargets);
        internal static readonly CsfTargetWrite SetTargetLife = CsfStubs.TargetWrite(ClientMobTargets);

        // Methods. Each call enters the base method (and any Harmony patch on it) and has MethodInfo.Invoke's exception boundary.
        internal static readonly Func<int, bool> PacketCurrent = CsfStubs.Typed<Func<int, bool>>(Owner, "IsPacketGenerationCurrentLocked");
        internal static readonly Func<dc.pr.Level, bool> LevelMatches = CsfStubs.Typed<Func<dc.pr.Level, bool>>(Owner, "DoesLevelMatchCurrentIdentityLocked");
        internal static readonly Func<bool> SyncQuiesced = CsfStubs.Typed<Func<bool>>(Owner, "IsSyncQuiescedForTransition");   // has a side effect: call it every time

        // s_clientMobLifecycle is initonly with one store (release IL lines 145904-145905): the delegate is bound to that ledger.
        internal static readonly Func<int, int, bool> LedgerDead = Ledger("IsDead");
        private static Func<int, int, bool> Ledger(string name)
        {
            var ledger = CsfStubs.Fixed<object>(Owner, "s_clientMobLifecycle");
            return CsfStubs.Typed<Func<int, int, bool>>(ledger.GetType().GetMethod(name) ?? throw new MissingMethodException(ledger.GetType().FullName, name), ledger);
        }
    }

    // The three holders below are for call sites that are not converted yet: nothing in this file uses them. Each is bound at
    // its own first use, so a binding that fails in one of them leaves CsfMobs and the six helpers as they are.

    // Statics of MobsSynchronization that the base mod assigns while it runs: read on every call, never kept.
    private static class CsfMobLive
    {
        private static readonly Type Owner = CsfStubs.Mobs();
        internal static readonly Func<dc.pr.Level> CurrentLevel = CsfStubs.StaticGetter<dc.pr.Level>(Owner, "currentLevel");
        internal static readonly Func<bool> LevelIdentityReady = CsfStubs.StaticGetter<bool>(Owner, "s_levelIdentityReady");
        internal static readonly Func<int> LevelIdentityToken = CsfStubs.StaticGetter<int>(Owner, "s_levelIdentityToken");
        internal static readonly Func<int> LevelIdentityGeneration = CsfStubs.StaticGetter<int>(Owner, "s_levelIdentityGeneration");
        internal static readonly Func<int> NextRuntimeSyncId = CsfStubs.StaticGetter<int>(Owner, "nextRuntimeSyncId");
        internal static readonly Func<int> AuthoritativeClientMobDieDepth = CsfStubs.StaticGetter<int>(Owner, "authoritativeClientMobDieDepth");
    }

    // More methods of the base mod. Each call enters the base method (and any Harmony patch on it), as in CsfMobs.
    private static class CsfMobCalls
    {
        private static readonly Type Owner = CsfStubs.Mobs();
        internal static readonly Func<dc.pr.Level, int> LevelToken = CsfStubs.Typed<Func<dc.pr.Level, int>>(Owner, "ComputeLevelIdentityToken");
        internal static readonly Func<Mob, bool> MobCulledLocally = CsfStubs.Typed<Func<Mob, bool>>(Owner, "IsMobCulledLocally");
        internal static readonly Func<dc.Entity, double> WorldX = CsfStubs.Typed<Func<dc.Entity, double>>(Owner, "GetWorldX");
        internal static readonly Func<dc.Entity, double> WorldY = CsfStubs.Typed<Func<dc.Entity, double>>(Owner, "GetWorldY");
        internal static readonly Func<Mob, bool> IsBossMob = CsfStubs.Typed<Func<Mob, bool>>(
            typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.Mobs.Bosses.BossSyncHelpers", true), "IsBossMob");
    }

    // More collections of the mob registry (initonly, one store each: the reference is kept; use them under lock (Gate)).
    private static class CsfMobExtras
    {
        private static readonly Type Owner = CsfStubs.Mobs();
        internal static readonly List<Mob> TrackedMobs = CsfStubs.Fixed<List<Mob>>(Owner, "trackedMobs");
        internal static readonly HashSet<Mob> PendingCulledMobDeaths = CsfStubs.Fixed<HashSet<Mob>>(Owner, "s_pendingCulledMobDeaths");
        // Their element types are private to the base mod: only the non-generic view can be named here.
        internal static readonly IDictionary HostDeathTombstones = CsfStubs.Fixed<IDictionary>(Owner, "s_hostDeathTombstonesBySyncId");
        internal static readonly IList ClientPendingBossAttacks = CsfStubs.Fixed<IList>(Owner, "clientPendingBossAttacks");
        private static readonly Type Tombstone = Owner.GetNestedType("HostDeathTombstone", BindingFlags.NonPublic) ??
            throw new TypeLoadException("MobsSynchronization.HostDeathTombstone");
        // The resend budget of one value of HostDeathTombstones (a private class of the base mod).
        internal static readonly Func<object, int> TombstoneSends = CsfStubs.Reader<int>(Tombstone, "SendsRemaining");
        internal static readonly Action<object, int> SetTombstoneSends = CsfStubs.Writer<int>(Tombstone, "SendsRemaining");
    }

    // The run: the launch descriptor that Property(descriptor, ...) reads, the two calls that return it and the lobby's run
    // test. Nothing in this file uses it. A descriptor is an immutable record: what was read from one object stays true for it.
    private static class CsfRun
    {
        private static readonly Type Descriptor = typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.PortableCore.RunLaunchDescriptor", true);
        private static readonly Type Launch = typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.RunLaunchCoordinator", true);
        internal static readonly Func<object, Guid> SessionId = CsfStubs.Reader<Guid>(Descriptor, "SessionId");
        internal static readonly Func<object, long> RunId = CsfStubs.Reader<long>(Descriptor, "RunId");
        internal static readonly Func<object, int> Sequence = CsfStubs.Reader<int>(Descriptor, "Sequence");
        internal static readonly Func<object> HostDescriptor = CsfStubs.Typed<Func<object>>(Launch, "GetCurrentHostDescriptor");
        internal static readonly Func<object> RemoteDescriptor = CsfStubs.Typed<Func<object>>(Launch, "GetCurrentRemoteDescriptor");
        internal static readonly Func<bool> InActualRun = CsfStubs.Typed<Func<bool>>(
            typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.LobbySession", true), "IsInActualRun");
    }

    private static class CsfScratch
    {
        internal static List<KeyValuePair<int, NetNode.MobDie>> Deaths;   // RetryUnresolvedDeaths only, under lock (Gate)
    }
}
