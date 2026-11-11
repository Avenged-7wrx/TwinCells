using System;
using System.Reflection;
using ModCore.Mods;

// DCCM entry point. Installs each part independently (UI layer, two-player cap, player colour): a part that
// fails to install is rolled back by itself and the other parts still run. Parts are found by type name so a
// partial build (Build.ps1 -Parts) compiles without the missing parts.
public sealed class CoopLobby : ModBase
{
    static readonly (string Type,string Switch)[] Parts={("LobbyUi","UI"),("LobbyCap","CAP"),("LobbyColor","COLOR")};
    public CoopLobby(ModInfo info):base(info){}
    public override void Initialize()
    {
        try{LobbyCore.CheckPins();}
        catch(Exception e){LobbyCore.Warn("Not installed: {Error}",e.Message);return;}
        foreach(var (typeName,sw) in Parts)
        {
            var type=typeof(CoopLobby).Assembly.GetType(typeName,false);
            if(type==null)continue;
            if(LobbyCore.Off(sw)){LobbyCore.Info("{Part} switched off by COOP_LOBBY_{Switch}_DISABLE",typeName,sw);continue;}
            try
            {
                var install=type.GetMethod("Install",BindingFlags.Public|BindingFlags.Static,null,Type.EmptyTypes,null)??throw new MissingMethodException(typeName,"Install");
                install.Invoke(null,null);
                LobbyCore.Info("{Part} installed",typeName);
            }
            catch(TargetInvocationException e){LobbyCore.Warn("{Part} not installed: {Error}",typeName,e.InnerException?.Message??e.Message);}
            catch(Exception e){LobbyCore.Warn("{Part} not installed: {Error}",typeName,e.Message);}
        }
    }
}
