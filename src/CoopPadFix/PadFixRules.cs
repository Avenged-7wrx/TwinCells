using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

// CoopPadFix pure rules (Alpha2 unit U5, PLAN-ALPHA2.md issue 7, owner decisions OD5/Q6). No game type is used here: this
// file is compiled into the part and, unchanged, into the managed checks (checks\PadFixRulesChecks.cs) and the mutants.
//
// 7a (D-pad does not move the hero): Dead Cells v35 defaults Options.dpadFunction to 2 = "Bound input (to rebind)"
// ($Options.__constructor__ f@5802, load fallback f@5796 op542-546). Options.configController (f@5789, Options.hx:1006)
// puts the D-pad codes [12,14,13,15] into the movement actions 10-13 (up, left, down, right) only when it is 0
// ("Movement"); with 2 nothing is bound to the D-pad. The part changes the game's own option, once:
//   - a new copy (no options file before $Options.load ran): the default becomes Movement;
//   - an existing copy: one-time migration from 2 to Movement when no gamepad action uses a D-pad code (in mode 2 the
//     player can bind actions to the D-pad himself, ui.OptionsBase.get_padKeys f@34430; such a choice is kept).
//   The decision is recorded in the part's own state file (never in dc_options.json) and is never taken again.
// 7b (Xbox glyphs on a DualSense): with "Icons used" = Default, Assets.getControlId (f@5852, Assets.hx:910-932) shows
// PlayStation glyphs only when hxd.Pad.get_name contains "PS4" or "PS5" (case-sensitive indexOf). DCCM's SDL3 3.4.14
// sdl.hdll names a DualSense "DualSense Wireless Controller"; the part shows it to the game as "PS5 <name>". The manual
// override stays the game's own Settings > Video > Icons > "Icons used" (OD Q6).
public static class PadFixRules
{
    // Switches (scheme COOP_<PART>_DISABLE; the pack row switch COOP_PACK_PAD_DISABLE is added by integration).
    public const string PartSwitch="COOP_PAD_DISABLE";
    public const string DpadSwitch="COOP_PAD_DPAD_DISABLE";
    public const string GlyphSwitch="COOP_PAD_GLYPHS_DISABLE";
    // QA only (honoured with COOP_PROBE=1): name of the state folder under <game>\qa\CoopPadFix\.
    public const string QaStateVariable="COOP_QA_PAD_STATE";
    public const string QaStateDefault="default";
    public const string StateFileName="CoopPadFix.state";
    public const string StateHeader="CoopPadFix-state-1";

    // Options.dpadFunction (ui/Options.hx:270-292: U29_DPAD_MOVEMENT=0, U29_DPAD_CAMERA=1, U29_DPAD_BINDABLE=2).
    public const int DpadMovement=0,DpadCamera=1,DpadBindable=2;
    // GamePad codes of the D-pad (Options.hx:1006: [12,14,13,15] for the actions up, left, down, right).
    public const int DpadUp=12,DpadDown=13,DpadLeft=14,DpadRight=15;

    public static bool SwitchSet(string value)=>value=="1";

    // ---------------- 7b: the pad name the game sees ----------------
    // Returns the name to hand to the game, or null to keep the native name unchanged.
    public static string MapPadName(string name)
    {
        if(string.IsNullOrEmpty(name))return null;
        // Already detected by the game (Assets.hx:921-922 test exactly these two substrings).
        if(name.IndexOf("PS5",StringComparison.Ordinal)>=0||name.IndexOf("PS4",StringComparison.Ordinal)>=0)return null;
        if(name.IndexOf("DualSense",StringComparison.OrdinalIgnoreCase)<0)return null;
        return "PS5 "+name;
    }
    // The log text of a name the game showed: "'<raw>' shown as '<shown>'" when the rule renamed it, else "'<shown>'".
    public static string DescribeShownName(string shown)
    {
        if(shown==null)return "(none)";
        if(shown.StartsWith("PS5 ",StringComparison.Ordinal))
        {
            var raw=shown.Substring(4);
            if(MapPadName(raw)==shown)return "'"+raw+"' shown as '"+shown+"'";
        }
        return "'"+shown+"'";
    }
    // Options.overrideControlIconsController (ui/Options.hx:758; Assets.hx:928-932).
    public static string IconsUsedName(int value)=>value switch
    {
        0=>"default (auto)",1=>"xbox",2=>"ps4",3=>"ps5",4=>"joycons",5=>"switch-pro",_=>"unknown("+value.ToString(CultureInfo.InvariantCulture)+")"
    };

    // ---------------- 7a: the D-pad decision ----------------
    public enum Marker{Absent,Present,Unreadable}
    public enum DpadAction{None,Record,SetMovement}
    public readonly struct DpadDecision
    {
        public readonly DpadAction Action;public readonly string Outcome;
        public DpadDecision(DpadAction action,string outcome){Action=action;Outcome=outcome;}
        public bool Records=>Action!=DpadAction.None;
        public override string ToString()=>Action+"/"+Outcome;
    }
    // Outcomes written to the state file. Any other dpad= value makes the file unreadable (never guessed).
    public static readonly string[] RecordedOutcomes={"new-copy-default","new-copy-movement","migrated","kept-bound","kept-movement","kept-camera","kept-other"};
    public static bool IsRecordedOutcome(string value)=>Array.IndexOf(RecordedOutcomes,value)>=0;

    // fileExisted: the game's options file (or its legacy file) existed before $Options.load ran.
    // marker: the part's own record of an earlier decision (only consulted for an existing file).
    // dpadCodesBound: some gamepad action uses a D-pad code; null = could not be read (only needed for mode 2).
    public static DpadDecision DecideDpad(bool fileExisted,int dpad,Marker marker,bool? dpadCodesBound)
    {
        if(!fileExisted)return dpad==DpadMovement?new DpadDecision(DpadAction.Record,"new-copy-movement"):new DpadDecision(DpadAction.SetMovement,"new-copy-default");
        if(marker==Marker.Present)return new DpadDecision(DpadAction.None,"kept-marker");
        if(marker==Marker.Unreadable)return new DpadDecision(DpadAction.None,"postponed-marker-unreadable");
        switch(dpad)
        {
            case DpadBindable:
                if(dpadCodesBound==null)return new DpadDecision(DpadAction.None,"postponed-bindings-unreadable");
                return dpadCodesBound.Value?new DpadDecision(DpadAction.Record,"kept-bound"):new DpadDecision(DpadAction.SetMovement,"migrated");
            case DpadMovement:return new DpadDecision(DpadAction.Record,"kept-movement");
            case DpadCamera:return new DpadDecision(DpadAction.Record,"kept-camera");
            default:return new DpadDecision(DpadAction.Record,"kept-other");
        }
    }
    // A D-pad code (12-15) in any binding slot.
    public static bool AnyDpadCode(IEnumerable<int?> codes)
    {
        foreach(var c in codes)if(c is int v&&v>=DpadUp&&v<=DpadRight)return true;
        return false;
    }
    // One slot of a gamepad binding array read as a dynamic value: null = empty slot; an integral number = the code; any
    // other value = an unknown shape (false: the caller treats the whole table as unreadable).
    public static bool TryCode(object value,out int? code)
    {
        code=null;
        switch(value)
        {
            case null:return true;
            case int i:code=i;return true;
            case short s:code=s;return true;
            case sbyte sb:code=sb;return true;
            case byte b:code=b;return true;
            case ushort us:code=us;return true;
            case long l when l>=int.MinValue&&l<=int.MaxValue:code=(int)l;return true;
            case uint ui when ui<=int.MaxValue:code=(int)ui;return true;
            case double d when d==Math.Floor(d)&&d>=int.MinValue&&d<=int.MaxValue:code=(int)d;return true;
            case float f when f==Math.Floor(f)&&f>=int.MinValue&&f<=int.MaxValue:code=(int)f;return true;
            default:return false;
        }
    }

    // ---------------- the gamepad tables ----------------
    // Options.gamepad_normal / gamepad_custom: an object whose fields (the action names) hold binding arrays of GamePad
    // codes, read through the game's own Reflect.fields / Reflect.field (ITablePort: PadFixTablePort in the part, models
    // in the checks). Reflect.field is typed Dynamic in GameProxy (HlFunc<object,object,String>), and DCCM hands a
    // closure's Dynamic result over unconverted: a raw Hashlink.Proxy.HashlinkObj, never the dc.hl.types.* proxy (native
    // run a2-U5-pad-1: every movement value read as Hashlink.Proxy.Objects.HashlinkObject, so the decision always waited).
    // Each value Field returns therefore passes through Dynamic (the conversion GameProxy applies to its own Dynamic
    // results, HaxeProxyHelper.GetProxy<object>) before its shape is tested.
    public interface ITablePort
    {
        IList<object> FieldNames(object table);   // the table's field names as Field keys; null = not readable
        object Field(object table,object name);    // the field's value as the Reflect.field closure returns it
        object Dynamic(object value);              // GameProxy's conversion of a Dynamic value (raw object -> proxy)
        IList<object> Slots(object value);         // the slot values of a binding array proxy; null = not an array
    }
    // true / false: some slot of the table holds a D-pad code; a missing table has none; null = a table of unknown shape.
    public static bool? TableHasDpadCode(ITablePort port,object table)
    {
        if(table==null)return false;
        var names=port.FieldNames(table);
        if(names==null)return null;
        var codes=new List<int?>();
        foreach(var name in names)
        {
            if(name==null)return null;
            var value=port.Dynamic(port.Field(table,name));
            if(value==null)continue;
            var slots=port.Slots(value);
            if(slots==null)return null;
            foreach(var slot in slots){if(!TryCode(slot,out int? code))return null;codes.Add(code);}
        }
        return AnyDpadCode(codes);
    }
    // Any action of gamepad_normal or gamepad_custom bound to a D-pad code; null when either table cannot be read.
    public static bool? DpadCodesBound(ITablePort port,object normal,object custom)
    {
        bool? n=TableHasDpadCode(port,normal);if(n==null)return null;
        bool? c=TableHasDpadCode(port,custom);if(c==null)return null;
        return n.Value||c.Value;
    }

    // ---------------- the state file ----------------
    public static string FormatState(string outcome,int before,int after,DateTime utc)
    {
        if(!IsRecordedOutcome(outcome))throw new ArgumentException("not a recorded outcome: "+outcome);
        return StateHeader+"\n"+"dpad="+outcome+"\n"+"before="+before.ToString(CultureInfo.InvariantCulture)+"\n"+"after="+after.ToString(CultureInfo.InvariantCulture)+"\n"
            +"utc="+utc.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture)+"\n";
    }
    // null text = no file. A file with another header, without a dpad= line, or with an unknown outcome is Unreadable.
    public static Marker ParseState(string text,out string outcome)
    {
        outcome=null;
        if(text==null)return Marker.Absent;
        var lines=text.Replace("\r\n","\n").Split('\n');
        if(lines[0]!=StateHeader)return Marker.Unreadable;
        for(int i=1;i<lines.Length;i++)
        {
            if(!lines[i].StartsWith("dpad=",StringComparison.Ordinal))continue;
            var value=lines[i].Substring(5);
            if(!IsRecordedOutcome(value))return Marker.Unreadable;
            outcome=value;return Marker.Present;
        }
        return Marker.Unreadable;
    }
    // Where the decision is recorded. Normal play: <the module's DCCM data folder>\CoopPadFix.state (coremod\data\CoopPack
    // in the pack, coremod\data\CoopPadFix standalone). QA runs (COOP_PROBE=1): <game>\qa\CoopPadFix\<COOP_QA_PAD_STATE or
    // "default">\CoopPadFix.state, so an audit copy keeps no state outside its run-log folders.
    public static string StatePath(string dataRoot,string gameRoot,string probe,string qaState)
    {
        if(probe=="1")
        {
            var name=string.IsNullOrEmpty(qaState)?QaStateDefault:qaState;
            if(!QaNameValid(name))throw new ArgumentException("invalid "+QaStateVariable+" value");
            if(string.IsNullOrEmpty(gameRoot))throw new InvalidOperationException("no game folder for the QA state");
            return Path.Combine(gameRoot,"qa","CoopPadFix",name,StateFileName);
        }
        if(string.IsNullOrEmpty(dataRoot))throw new InvalidOperationException("no module data folder");
        return Path.Combine(dataRoot,StateFileName);
    }
    public static bool QaNameValid(string name)
    {
        if(string.IsNullOrEmpty(name)||name.Length>80||name.Contains(".."))return false;
        if(!char.IsAsciiLetterOrDigit(name[0]))return false;
        foreach(var c in name)if(!(char.IsAsciiLetterOrDigit(c)||c=='_'||c=='.'||c=='-'))return false;
        return true;
    }

    // ---------------- the sequence run after $Options.load ----------------
    public sealed class DpadResult
    {
        public DpadDecision Decision;public Marker Marker;public bool? CodesBound;public int Before,After;
        public bool Configured,Saved,Recorded;public string RecordError;
    }
    // The game operations on the Options instance $Options.load returned (PadFixOptionsPort in the part; a model in the
    // checks). Order (the checks pin it): read the marker (existing file only), read the bindings (existing file, no
    // marker, mode 2 only), decide; for SetMovement: dpadFunction=0, configController(null) when Boot.ME.controller exists
    // (Options.ctrlBind binds into it, Options.hx:1109), save(); then the record. A failed record is reported, not thrown:
    // the option is already right, and the next start records it ("kept-movement").
    public interface IOptionsPort
    {
        int Dpad{get;}
        bool ControllerReady{get;}
        Marker ReadMarker();
        bool? DpadCodesBound();
        void SetDpadMovement();
        void ConfigController();
        void Save();
        void WriteMarker(string text);
    }
    public static DpadResult RunDpad(IOptionsPort port,bool fileExisted,DateTime utc)
    {
        var r=new DpadResult{Marker=Marker.Absent};
        r.Before=port.Dpad;r.After=r.Before;
        if(fileExisted)r.Marker=port.ReadMarker();
        if(fileExisted&&r.Marker==Marker.Absent&&r.Before==DpadBindable)r.CodesBound=port.DpadCodesBound();
        r.Decision=DecideDpad(fileExisted,r.Before,r.Marker,r.CodesBound);
        if(r.Decision.Action==DpadAction.SetMovement)
        {
            port.SetDpadMovement();r.After=DpadMovement;
            if(port.ControllerReady){port.ConfigController();r.Configured=true;}
            port.Save();r.Saved=true;
        }
        if(r.Decision.Records)
        {
            try{port.WriteMarker(FormatState(r.Decision.Outcome,r.Before,r.After,utc));r.Recorded=true;}
            catch(Exception e){r.RecordError=e.GetBaseException().Message;}
        }
        return r;
    }
    // The one log line per decision (no failure words of the QA critical-line regex: postponed decisions are WARN).
    public static string DpadLine(DpadResult r)
    {
        var b=new StringBuilder("D-pad: ").Append(r.Decision.Outcome).Append(" (dpadFunction ").Append(r.Before.ToString(CultureInfo.InvariantCulture));
        if(r.After!=r.Before)b.Append(" -> ").Append(r.After.ToString(CultureInfo.InvariantCulture)).Append(", saved").Append(r.Configured?", movement bound":"");
        else b.Append(" unchanged");
        if(r.Decision.Records)b.Append(r.Recorded?", recorded":", record not written: "+r.RecordError);
        return b.Append(')').ToString();
    }
    public static bool IsPostponed(DpadDecision d)=>d.Outcome.StartsWith("postponed",StringComparison.Ordinal);
}

// Per-hook fault gate (same thresholds as CoopPack PartGate and LobbyCore.FaultGate): trips at 8 consecutive or 64 total
// faults; a tripped hook only forwards to the game.
public sealed class PadFixGate
{
    public const int ConsecutiveLimit=8,TotalLimit=64;
    readonly string _name;int _consecutive;long _total;
    public volatile bool Tripped;
    public PadFixGate(string name){_name=name;}
    public string Name=>_name;
    public long Faults=>Interlocked.Read(ref _total);
    public void Ok(){_consecutive=0;}
    // Records a fault; returns true exactly once, when this fault trips the gate.
    public bool Fail()
    {
        long n=Interlocked.Increment(ref _total);
        bool before=Tripped;
        if(++_consecutive>=ConsecutiveLimit||n>=TotalLimit)Tripped=true;
        return Tripped&&!before;
    }
}

// Rate limit of the part's log lines: at most Max lines per session for one kind.
public sealed class PadFixLogBudget
{
    readonly int _max;int _used;
    public PadFixLogBudget(int max){_max=max;}
    public int Used=>_used;
    public bool Take(){if(_used>=_max)return false;_used++;return true;}
}

// Distinct pad names seen (log once per name, at most Max names per session).
public sealed class PadFixNameBook
{
    readonly HashSet<string> _seen=new(StringComparer.Ordinal);readonly int _max;
    public PadFixNameBook(int max){_max=max;}
    public bool FirstSight(string name)
    {
        name??="";
        if(_seen.Contains(name)||_seen.Count>=_max)return false;
        _seen.Add(name);return true;
    }
}
