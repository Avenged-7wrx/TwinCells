using System;

// The six player colours (owner-approved mockup, checked on the real hero sprite with the lobby shaders).
// Rgb is both the swatch colour and the body-tint multiplier (r/255, g/255, b/255), as CoopSyncFixes tints.
// Defaults keep today's two-player look: host Ember (orange), client Ice (cyan).
public static class LobbyPalette
{
    public static readonly (string Key,int Rgb)[] Colors=
    {
        ("color.ember",0xFFAD6B),("color.ice",0x61E0FF),("color.violet",0xD67AFF),
        ("color.moss",0x7AFF7A),("color.rose",0xFF80C7),("color.azure",0x739EFF),
    };
    public const int HostDefault=0,ClientDefault=1;
    public static int Count=>Colors.Length;
    public static bool Valid(int index)=>index>=0&&index<Colors.Length;
    public static int Rgb(int index)=>Valid(index)?Colors[index].Rgb:0xFFFFFF;
    public static (double R,double G,double B) Multipliers(int index){int c=Rgb(index);return(((c>>16)&255)/255.0,((c>>8)&255)/255.0,(c&255)/255.0);}
    public static int DefaultFor(int netId)=>netId<=1?HostDefault:ClientDefault;
}

// Colour state shared between the colour part (writer, main thread) and the UI part (reader, main thread).
// ColorOf is what the game really draws: the chosen colour while the colour part runs and the partner supports
// colours (or the host is alone); otherwise the defaults above, which match CoopSyncFixes' two-player tint; and -1
// (white, no tint) when that tint is switched off (COOP_SYNC_DISABLE_COLORS=1) but the colour part is not running,
// because then nothing tints the heroes in the run.
public static class LobbyColorState
{
    public static volatile bool PartRunning;          // colour part installed and not tripped
    public static volatile bool PeerSupports;         // the connected partner announced colour support
    // Published by the colour part's host tick from its own session state (a host session with no partner seat);
    // never derived from the UI's lobby poll, which goes stale outside the lobby screens.
    public static volatile bool HostAlone;
    static readonly int[] _byId={-1,-1,-1,-1,-1};      // index = net id (1 = host, 2..4 clients)
    public static int Pending=-1;                     // local request waiting for the host's answer
    public static string LastNotice;                  // one-shot text for the UI hint line ("taken" or ready text)
    static long _noticeAt;
    public static Action<int> RequestHandler;         // set by the colour part
    public static int Revision;                       // bumped on every change the UI should redraw
    static readonly bool CsfTintOff=Environment.GetEnvironmentVariable("COOP_SYNC_DISABLE_COLORS")=="1";

    public static bool CanPick=>PartRunning&&(PeerSupports||HostAlone);
    public static int ColorOf(int netId)
    {
        if(!PartRunning&&CsfTintOff)return -1;
        if(netId>=1&&netId<_byId.Length&&LobbyPalette.Valid(_byId[netId])&&PartRunning&&(PeerSupports||HostAlone))return _byId[netId];
        return LobbyPalette.DefaultFor(netId);
    }
    public static void Set(int netId,int index){if(netId>=1&&netId<_byId.Length&&_byId[netId]!=index){_byId[netId]=index;Revision++;}}
    public static void Clear(){for(int i=0;i<_byId.Length;i++)_byId[i]=-1;Pending=-1;PeerSupports=false;HostAlone=false;LastNotice=null;Revision++;}
    // One-shot hint text with a time stamp: the UI shows it only when it is fresh, so a notice posted while no
    // lobby layer was up (e.g. an answer arriving in a run) never pops up on a later screen.
    public static void Post(string text){LastNotice=text;_noticeAt=System.Diagnostics.Stopwatch.GetTimestamp();Revision++;}
    public static string Take(double maxAgeSeconds=1.5)
    {
        var n=LastNotice;if(n==null)return null;
        LastNotice=null;
        double age=(System.Diagnostics.Stopwatch.GetTimestamp()-_noticeAt)/(double)System.Diagnostics.Stopwatch.Frequency;
        return age<=maxAgeSeconds?n:null;
    }
    public static bool TakenByOther(int index,int localId)
    {
        for(int id=1;id<_byId.Length;id++)if(id!=localId&&ColorOf(id)==index&&IsKnown(id))return true;
        return false;
    }
    static bool IsKnown(int id){foreach(var p in LobbyModel.Players)if(p.Id==id)return true;return false;}
    public static void Request(int index){if(CanPick&&LobbyPalette.Valid(index))try{RequestHandler?.Invoke(index);}catch(Exception e){LobbyCore.Warn("Colour request failed: {Error}",e.Message);}}
}

// Cross-part flags that are not colour state.
public static class LobbyShared
{
    // Set by the CAP part while it owns the client's "lobby full" popup (between the refused WELCOME and the popup):
    // the UI must not re-show the stale original waiting panel when its layer goes down in that window.
    public static volatile bool PopupPending;
}
