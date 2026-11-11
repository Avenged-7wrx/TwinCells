using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using dc.libs.heaps.slib;
using DeadCellsMultiplayerMod;

public sealed partial class CoopSyncFixes
{
    private static Dictionary<IntPtr,int> BodyTints=new();
    private static readonly long[] TintDraws=new long[5];
    private static Delegate BodyDrawHook;
    private static readonly bool TwoPlayerColors=Environment.GetEnvironmentVariable("COOP_TWO_PLAYER_COLORS")=="1";
    private struct TintState {public dc.h3d.Vector Color;public double R,G,B;}
    private static int PlayerPaletteId(int id,bool pairProfile)
        =>id>=1 && id<=4?(pairProfile && id!=1?2:id):0;
    private static void InitializePlayerColors()
    {
        if(Disabled("COLORS"))return;
        var hookType=typeof(HSprite).Assembly.GetType("dc.libs.heaps.slib.Hook_HSprite",true);
        var ev=hookType.GetEvent("draw");var invoke=ev.EventHandlerType.GetMethod("Invoke");
        var parameters=invoke.GetParameters();var types=new Type[parameters.Length];
        for(int i=0;i<types.Length;i++)types[i]=parameters[i].ParameterType;
        if(types.Length!=3 || invoke.ReturnType!=typeof(void))throw new InvalidOperationException("Unexpected native sprite draw signature");
        var adapter=new DynamicMethod("CoopSyncPlayerTint",typeof(void),types,typeof(CoopSyncFixes),true);
        var il=adapter.GetILGenerator();var state=il.DeclareLocal(typeof(TintState));
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(BeforePlayerDraw),Flags));il.Emit(OpCodes.Stloc,state);
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Callvirt,types[0].GetMethod("Invoke"));
        il.BeginFinallyBlock();il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldloc,state);
        il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(AfterPlayerDraw),Flags));il.EndExceptionBlock();il.Emit(OpCodes.Ret);
        BodyDrawHook=adapter.CreateDelegate(ev.EventHandlerType);RuntimeHelpers.PrepareDelegate(BodyDrawHook);ev.AddEventHandler(null,BodyDrawHook);
    }
    private static void RefreshPlayerColors()
    {
        if(Disabled("COLORS"))return;
        var next=new Dictionary<IntPtr,int>();var net=ModEntry._net;
        if(net!=null && net.IsAlive)
        {
            try {var body=dc.pr.Game.Class.ME?.hero?.spr;if(body!=null && net.id>=1 && net.id<=4)next[body.HashlinkPointer]=net.id;}catch { }
            for(int i=0;i<ModEntry.clients.Length;i++)
                try {var body=ModEntry.clients[i]?.spr;var id=ModEntry.clientIds[i];if(body!=null && id>=1 && id<=4)next[body.HashlinkPointer]=id;}catch { }
        }
        BodyTints=next;
    }
    private static TintState BeforePlayerDraw(HSprite sprite)
    {
        if(!BodyTints.TryGetValue(sprite.HashlinkPointer,out var id) || sprite.color==null)return default;
        var palette=PlayerPaletteId(id,TwoPlayerColors);if(palette==0)return default;
        var color=sprite.color;var saved=new TintState {Color=color,R=color.x,G=color.y,B=color.z};
        double r=1,g=1,b=1;
        switch(palette)
        {
            case 1:r=1;g=.68;b=.42;break;
            case 2:r=.38;g=.88;b=1;break;
            case 3:r=.84;g=.48;b=1;break;
            case 4:r=.48;g=1;b=.48;break;
        }
        color.x=saved.R*r;color.y=saved.G*g;color.z=saved.B*b;TintDraws[id]++;
        return saved;
    }
    private static void AfterPlayerDraw(HSprite sprite,TintState saved)
    {
        if(saved.Color==null)return;
        saved.Color.x=saved.R;saved.Color.y=saved.G;saved.Color.z=saved.B;
    }
}
