using System;
using System.Collections.Generic;
using System.Reflection;
using DeadCellsMultiplayerMod;

public sealed class LobbyPlayer
{
    public int Id;            // net id: host 1, clients 2..4
    public string Name="";
    public bool Ready;
    public string Skin="";
    public string Head="";
    public bool IsHost;       // Id == 1
    public bool IsYou;
}

// Who is in the lobby, read from the mod's own public/internal state. Main thread only (the UI part polls it
// from its ModEntry.OnFrameUpdate postfix). Ready and names are always read live, never cached across polls
// (after a client leaves the host's ready resets without a re-send). Players are ordered host first, then id.
public static class LobbyModel
{
    public static readonly List<LobbyPlayer> Players=new();
    public static int LocalId;
    public static bool IsHost,NetAlive;
    public static string Signature="";    // changes whenever anything the UI shows changes
    public static string Error;

    static bool _bound;
    static MethodInfo _snapshot;static PropertyInfo _localReady,_username;
    static Func<string> _localSkin,_localHead;
    static Func<int,int,string> _remoteSkin,_remoteHead;
    static Func<int,int,string,string> _playerName;

    static void Bind()
    {
        if(_bound)return;
        var lobby=LobbyCore.ModType("DeadCellsMultiplayerMod.LobbySession");
        _snapshot=lobby.GetMethod("ReadSessionSnapshot",LobbyCore.S,null,Type.EmptyTypes,null)??throw new MissingMethodException("LobbySession","ReadSessionSnapshot");
        var snap=_snapshot.ReturnType;
        _localReady=snap.GetProperty("LocalReady")??throw new MissingMemberException("LobbySessionSnapshot","LocalReady");
        _username=snap.GetProperty("Username")??throw new MissingMemberException("LobbySessionSnapshot","Username");
        var ui=LobbyCore.ModType("DeadCellsMultiplayerMod.MultiplayerModUI.Connection._ConnectionUI");
        _localSkin=LobbyCore.Delegate<Func<string>>(LobbyCore.Method(ui,"ResolveLocalHeroSkin",true,typeof(string)));
        _localHead=LobbyCore.Delegate<Func<string>>(LobbyCore.Method(ui,"ResolveLocalHeroHeadSkin",true,typeof(string)));
        _remoteSkin=LobbyCore.Delegate<Func<int,int,string>>(LobbyCore.Method(ui,"ResolveRemoteSkin",true,typeof(string),typeof(int),typeof(int)));
        _remoteHead=LobbyCore.Delegate<Func<int,int,string>>(LobbyCore.Method(ui,"ResolveRemoteHeadSkin",true,typeof(string),typeof(int),typeof(int)));
        _playerName=LobbyCore.Delegate<Func<int,int,string,string>>(LobbyCore.Method(ui,"GetPlayerName",true,typeof(string),typeof(int),typeof(int),typeof(string)));
        _bound=true;
    }
    // Throws on a binding mismatch (call it once from a part's Install to fail closed at install time).
    public static void Prepare()=>Bind();

    public static void Poll()
    {
        Bind();
        var list=new List<LobbyPlayer>(2);
        var net=ModEntry._net;
        NetAlive=net!=null&&net.IsAlive;
        IsHost=NetAlive&&net.IsHost;
        LocalId=!NetAlive?1:(IsHost?1:net.id);
        object snap=_snapshot.Invoke(null,null);
        var me=new LobbyPlayer{Id=LocalId,IsYou=true,IsHost=LocalId==1,Ready=(bool)_localReady.GetValue(snap),Name=Clean((string)_username.GetValue(snap)),Skin=_localSkin()??"",Head=_localHead()??""};
        list.Add(me);
        if(NetAlive&&LocalId>0)
        {
            if(net.TryGetRemoteUserSnapshots(out var remotes))
            {
                try
                {
                    foreach(var r in remotes)
                    {
                        if(r.Id<=0||r.Id==LocalId)continue;
                        bool ready=net.TryGetRemoteReady(r.Id,out var rr)&&rr;
                        list.Add(new LobbyPlayer{Id=r.Id,IsHost=r.Id==1,Ready=ready,Name=Clean(_playerName(LocalId,r.Id,r.Username)),Skin=_remoteSkin(LocalId,r.Id)??"",Head=_remoteHead(LocalId,r.Id)??""});
                    }
                }
                finally{NetNode.ReleaseConsumedList(remotes);}
            }
        }
        list.Sort((a,b)=>a.IsHost!=b.IsHost?(a.IsHost?-1:1):a.Id.CompareTo(b.Id));
        var sig=new System.Text.StringBuilder();
        foreach(var p in list)sig.Append(p.Id).Append('|').Append(p.Name).Append('|').Append(p.Ready?'R':'-').Append('|').Append(p.Skin).Append('|').Append(p.Head).Append(';');
        Players.Clear();Players.AddRange(list);Signature=sig.ToString();
    }
    // Nicknames come from the network: drop control characters and the game's markup braces so a nick
    // cannot turn into a colour tag or an image (ui.Text.set_text REG_TAG), and keep it short.
    public static string Clean(string s)
    {
        if(string.IsNullOrWhiteSpace(s))return "Guest";
        var b=new System.Text.StringBuilder(s.Length);
        foreach(char c in s){if(char.IsControl(c)||c=='{'||c=='}'||char.IsSurrogate(c))continue;b.Append(c);}
        var t=b.ToString().Trim();
        if(t.Length==0)return "Guest";
        return t.Length>32?t.Substring(0,32):t;
    }
}
