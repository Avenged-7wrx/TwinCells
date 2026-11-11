using System;
using System.Reflection;
using ModCore.Mods;

// DCCM entry point of the optimization parts (host per-enemy path, client per-enemy path, minimap loop). Each part
// installs independently and rolls itself back on failure; parts are found by type name so a partial build
// (Build.ps1 -Parts) compiles without the missing ones. Same pattern as CoopLobby; these parts will join CoopPack.
public sealed class CoopOpt : ModBase
{
    static readonly (string Type,string Switch)[] Parts={("OptHost","HOST"),("OptClient","CLIENT"),("OptMinimap","MINIMAP")};
    public CoopOpt(ModInfo info):base(info){}
    public override void Initialize()
    {
        try{OptCore.CheckPins();}
        catch(Exception e){OptCore.Warn("Not installed: {Error}",e.Message);return;}
        foreach(var (typeName,sw) in Parts)
        {
            var type=typeof(CoopOpt).Assembly.GetType(typeName,false);
            if(type==null)continue;
            if(OptCore.Off(sw)){OptCore.Info("{Part} switched off by COOP_OPT_{Switch}_DISABLE",typeName,sw);continue;}
            try
            {
                var install=type.GetMethod("Install",BindingFlags.Public|BindingFlags.Static,null,Type.EmptyTypes,null)??throw new MissingMethodException(typeName,"Install");
                install.Invoke(null,null);
                OptCore.Info("{Part} installed",typeName);
            }
            catch(TargetInvocationException e){OptCore.Warn("{Part} not installed: {Error}",typeName,e.InnerException?.Message??e.Message);}
            catch(Exception e){OptCore.Warn("{Part} not installed: {Error}",typeName,e.Message);}
        }
    }
}
