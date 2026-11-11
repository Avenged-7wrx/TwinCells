using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;

// No NetNode instance or static/game method is called by this class.
// Only the concrete private pending queue is patched. Both generic arguments are
// value types; the exact closed method and its field shapes are verified first.
public static class MobMoveQueuePatch
{
    public const string BaseSha256 = "378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string Owner = "CoopNetworkFixes.MobMoveQueue.20261108";
    const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly object InstallationGate = new object();
    static Func<object, Dictionary<(int Generation, int Id), int>> ReadSlots;
    static Func<object, List<NetNode.MobMoveSnapshot>> ReadItems;
    static long RejectedCount;
    public static bool Ready { get; private set; }
    public static bool Disabled { get; private set; }
    public static long ReorderedUpdatesRejected => Interlocked.Read(ref RejectedCount);
    public static string InitializationError { get; private set; }

    public static void Install()
    {
        lock (InstallationGate)
        {
            if (Ready) return;
            if (InitializationError != null)
                throw new InvalidOperationException("CoopNetworkFixes previous initialization failed: " + InitializationError);

            Harmony harmony = null;
            MethodInfo target = null;
            try
            {
                var original = typeof(NetNode).Assembly;
                using (var bytes = File.OpenRead(original.Location))
                {
                    if (original.GetName().Version != new Version(0, 5, 1, 0) ||
                        Convert.ToHexString(SHA256.HashData(bytes)) != BaseSha256)
                        throw new InvalidOperationException("CoopNetworkFixes requires the exact community 0.5.1 DLL (SHA256 " + BaseSha256 + ").");
                }

                var generic = original.GetType("NetNode+LatestValueQueue`2", true);
                var concrete = generic.MakeGenericType(typeof(ValueTuple<int, int>), typeof(NetNode.MobMoveSnapshot));
                var slots = concrete.GetField("_slots", InstanceFields);
                var items = concrete.GetField("_items", InstanceFields);
                target = concrete.GetMethod("Upsert", InstanceFields, null,
                    new[] { typeof(ValueTuple<int, int>), typeof(NetNode.MobMoveSnapshot) }, null);
                if (target == null || target.IsStatic || target.ReturnType != typeof(void) || target.ContainsGenericParameters ||
                    slots == null || slots.FieldType != typeof(Dictionary<ValueTuple<int, int>, int>) ||
                    items == null || items.FieldType != typeof(List<NetNode.MobMoveSnapshot>))
                    throw new InvalidOperationException("Pinned pending-movement queue shape changed.");

                // Compile field reads once. No reflection, per-packet allocation, additional
                // queue lock, clock conversion, or persistent timestamp ledger in the prefix.
                ReadSlots = MakeGetter<Dictionary<(int, int), int>>(concrete, slots);
                ReadItems = MakeGetter<List<NetNode.MobMoveSnapshot>>(concrete, items);
                Disabled = Environment.GetEnvironmentVariable("COOP_NETWORK_REORDER_DISABLE") == "1";
                harmony = new Harmony(Owner);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(MobMoveQueuePatch).GetMethod(nameof(BeforeUpsert), BindingFlags.Static | BindingFlags.NonPublic)));
                var info = Harmony.GetPatchInfo(target);
                if (info == null || !info.Owners.Contains(Owner))
                    throw new InvalidOperationException("Pending-movement patch registration was not observed.");
                Ready = true;
            }
            catch (Exception ex)
            {
                Ready = false;
                InitializationError = ex.Message;
                if (harmony != null && target != null)
                {
                    try { harmony.Unpatch(target, HarmonyPatchType.All, Owner); }
                    catch (Exception rollback) { InitializationError += "; rollback: " + rollback.Message; }
                }
                throw;
            }
        }
    }

    static Func<object, T> MakeGetter<T>(Type declaringType, FieldInfo field)
    {
        var method = new DynamicMethod("CoopNetworkFixes_Read" + field.Name, typeof(T), new[] { typeof(object) }, typeof(MobMoveQueuePatch), true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, declaringType);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (Func<object, T>)method.CreateDelegate(typeof(Func<object, T>));
    }

    static bool BeforeUpsert(object __instance, ValueTuple<int, int> __0, NetNode.MobMoveSnapshot __1)
    {
        if (Disabled) return true;
        var slots = ReadSlots(__instance);
        if (!slots.TryGetValue(__0, out var index)) return true;
        var previous = ReadItems(__instance)[index];
        // The caller supplies (generation, id). No comparison is made across keys,
        // and Clear/TryConsume naturally end the ordering window with the slots.
        if (!double.IsFinite(previous.Time) || previous.Time <= 0 ||
            !double.IsFinite(__1.Time) || __1.Time <= 0 || __1.Time >= previous.Time)
            return true;
        Interlocked.Increment(ref RejectedCount);
        return false;
    }
}
