using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

// CoopPadFix game glue: the three DCCM native hooks, their boundaries, and the game reads/writes they need.
//   dc.Hook__Options.load              $Options.load (f@5796): before orig, whether the options file exists
//                                      (tool.$File.exists, the game's own test incl. Steam Cloud); after orig, the D-pad
//                                      decision on the RETURNED Options (Main.ME.options is still null here, f@5634).
//   dc.hxd.Hook_Pad.get_name           hxd.Pad.get_name (f@2461): a DualSense name gains "PS5 " (PadFixRules.MapPadName).
//   dc.tool.Hook_Controller.onPadEnabled  tool.Controller.onPadEnabled (f@2438, called when the first pad becomes active,
//                                      f@2436): one log line with the pad name, "Icons used" and the D-pad function.
// Every orig call is outside the part's try/catch (game exceptions reach the game unchanged); everything the part adds
// runs inside a try/catch behind a per-hook fault gate (PadFixGate). All hooks run on the game thread.
public static class PadFixStatus
{
    // Read by the unit probe (CoopPadFixProbe, reflection) and summarised in the logs.
    public static volatile bool Ready,Disabled,DpadHooked,GlyphsHooked,PadLogHooked;
    public static string ProxyCheck,StatePath,InitError;
    public static string DpadOutcome,DpadLine,DpadMarker,DpadRecordError;
    public static int DpadBefore=-1,DpadAfter=-1,DpadFileExisted=-1,DpadCodesBound=-1;
    public static bool DpadConfigured,DpadSaved,DpadRecorded;
    public static long LoadCalls,LoadDecisions,NameCalls,NameMapped,PadEnabledCalls;
    public static string LastRawName,LastShownName,LastPadLine;
    public static PadFixGate LoadGate,NameGate,PadGate;
    public static long Faults=>(LoadGate?.Faults??0)+(NameGate?.Faults??0)+(PadGate?.Faults??0);
    internal static void Record(PadFixRules.DpadResult r,bool existed)
    {
        DpadOutcome=r.Decision.Outcome;DpadMarker=r.Marker.ToString();DpadBefore=r.Before;DpadAfter=r.After;DpadFileExisted=existed?1:0;
        DpadCodesBound=r.CodesBound==null?-1:(r.CodesBound.Value?1:0);DpadConfigured=r.Configured;DpadSaved=r.Saved;DpadRecorded=r.Recorded;DpadRecordError=r.RecordError;
        DpadLine=PadFixRules.DpadLine(r);Interlocked.Increment(ref LoadDecisions);
    }
}

public static class PadFixLog
{
    // Literal rendering: pad names may contain template braces.
    public static void Info(string text){try{Serilog.Log.Information("{Text:l}","[CoopPadFix] "+text);}catch{}}
    public static void Warn(string text){try{Serilog.Log.Warning("{Text:l}","[CoopPadFix] "+text);}catch{}}
    public static void Error(string text){try{Serilog.Log.Error("{Text:l}","[CoopPadFix] "+text);}catch{}}
    public static void Fault(PadFixGate gate,string where,Exception e)
    {
        bool tripped=gate.Fail();
        string message;try{message=e.GetBaseException().GetType().Name+": "+e.GetBaseException().Message;}catch{message="?";}
        if(gate.Faults==1)Warn(where+" fault: "+message);
        if(tripped)Warn(gate.Name+" hook switched off for this session after "+gate.Faults+" faults (the game's own behaviour continues)");
    }
}

public sealed class PadFixLoadBoundary
{
    readonly Func<object,dc.Options> _orig;readonly string _statePath;readonly PadFixGate _gate;readonly PadFixLogBudget _log=new(8);
    public PadFixLoadBoundary(Func<object,dc.Options> orig,string statePath,PadFixGate gate){_orig=orig;_statePath=statePath;_gate=gate;}
    public dc.Options Invoke(object orig)
    {
        Interlocked.Increment(ref PadFixStatus.LoadCalls);
        bool? existed=null;
        if(!_gate.Tripped){try{existed=PadFixGame.OptionsFileExists();}catch(Exception e){PadFixLog.Fault(_gate,"options file check",e);}}
        var result=_orig(orig);
        if(existed.HasValue&&!_gate.Tripped&&result is not null)
        {
            try
            {
                var r=PadFixRules.RunDpad(new PadFixOptionsPort(result,_statePath),existed.Value,DateTime.UtcNow);
                PadFixStatus.Record(r,existed.Value);
                if(_log.Take()){if(PadFixRules.IsPostponed(r.Decision)||r.RecordError!=null)PadFixLog.Warn(PadFixStatus.DpadLine);else PadFixLog.Info(PadFixStatus.DpadLine);}
                _gate.Ok();
            }
            catch(Exception e){PadFixLog.Fault(_gate,"D-pad decision",e);}
        }
        return result;
    }
}

public sealed class PadFixNameBoundary
{
    readonly Func<object,dc.hxd.Pad,dc.String> _orig;readonly PadFixGate _gate;readonly PadFixNameBook _names=new(8);
    public PadFixNameBoundary(Func<object,dc.hxd.Pad,dc.String> orig,PadFixGate gate){_orig=orig;_gate=gate;}
    public dc.String Invoke(object orig,dc.hxd.Pad self)
    {
        var result=_orig(orig,self);
        if(_gate.Tripped||result is null)return result;
        try
        {
            Interlocked.Increment(ref PadFixStatus.NameCalls);
            var raw=result.ToString();
            var shown=PadFixRules.MapPadName(raw);
            PadFixStatus.LastRawName=raw;PadFixStatus.LastShownName=shown??raw;
            if(_names.FirstSight(raw))PadFixLog.Info(shown==null?"Pad name '"+raw+"' kept":"Pad name '"+raw+"' shown as '"+shown+"' (PlayStation glyphs in Icons used = Default)");
            _gate.Ok();
            if(shown==null)return result;
            Interlocked.Increment(ref PadFixStatus.NameMapped);
            return ModCore.Utilities.StringUtils.AsHaxeString(shown);
        }
        catch(Exception e){PadFixLog.Fault(_gate,"pad name",e);return result;}
    }
}

public sealed class PadFixPadBoundary
{
    readonly Action<object,dc.tool.Controller> _orig;readonly PadFixGate _gate;readonly PadFixLogBudget _log=new(16);
    public PadFixPadBoundary(Action<object,dc.tool.Controller> orig,PadFixGate gate){_orig=orig;_gate=gate;}
    public void Invoke(object orig,dc.tool.Controller self)
    {
        _orig(orig,self);
        if(_gate.Tripped)return;
        try
        {
            Interlocked.Increment(ref PadFixStatus.PadEnabledCalls);
            if(_log.Take())
            {
                var device=PadFixGame.ActiveDevice(self);
                var shown=device is null?null:device.get_name()?.ToString();     // through the get_name hook: what the game sees
                var options=dc.Main.Class.ME?.options;
                var line="Pad connected: "+PadFixRules.DescribeShownName(shown)+"; Icons used="+(options is null?"(options not loaded)":PadFixRules.IconsUsedName(options.overrideControlIconsController))
                    +"; D-pad function="+(options is null?"?":options.dpadFunction.ToString(System.Globalization.CultureInfo.InvariantCulture));
                PadFixStatus.LastPadLine=line;PadFixLog.Info(line);
            }
            _gate.Ok();
        }
        catch(Exception e){PadFixLog.Fault(_gate,"pad connected",e);}
    }
}

// The Options instance $Options.load returned, as PadFixRules.IOptionsPort.
public sealed class PadFixOptionsPort:PadFixRules.IOptionsPort
{
    readonly dc.Options _o;readonly string _state;
    public PadFixOptionsPort(dc.Options options,string statePath){_o=options;_state=statePath;}
    public int Dpad=>_o.dpadFunction;
    public bool ControllerReady{get{var boot=dc.Boot.Class.ME;return boot is not null&&boot.controller is not null;}}
    public PadFixRules.Marker ReadMarker()
    {
        try{return File.Exists(_state)?PadFixRules.ParseState(File.ReadAllText(_state),out _):PadFixRules.Marker.Absent;}
        catch{return PadFixRules.Marker.Unreadable;}
    }
    public bool? DpadCodesBound()=>PadFixGame.DpadCodesBound(_o);
    public void SetDpadMovement()=>_o.dpadFunction=PadFixRules.DpadMovement;
    public void ConfigController()=>PadFixGame.ConfigController(_o);
    public void Save()=>_o.save();
    public void WriteMarker(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_state));
        var temp=_state+".tmp";
        File.WriteAllText(temp,text,new UTF8Encoding(false));
        File.Move(temp,_state,true);
    }
}

// A gamepad table of Options read through the game's own Reflect (PadFixRules.ITablePort). Every dynamic value is taken as
// object: GameProxy declares getDyn / Reflect results as dynamic, and no call here may be bound at run time (no
// Microsoft.CSharp binder).
//   Reflect.fields is typed (HlFunc<ArrayObj,object>): DCCM converts its result to the ArrayObj proxy itself.
//   Reflect.field is typed Dynamic (HlFunc<object,object,String>): DCCM returns its result unconverted (a raw
//   Hashlink.Proxy.HashlinkObj; native run a2-U5-pad-1), so PadFixRules passes it through Dynamic, which is
//   HaxeProxyHelper.GetProxy<object>: the same conversion every GameProxy member with a Dynamic result applies
//   (e.g. ArrayAccess.getDyn: calli, then GetProxy<object>). A raw class instance becomes its dc.* proxy.
//   ArrayAccess.getDyn already converts (proxy or boxed number), so the slot values need no second conversion.
public sealed class PadFixTablePort:PadFixRules.ITablePort
{
    public static readonly PadFixTablePort Game=new PadFixTablePort();
    PadFixTablePort(){}
    public IList<object> FieldNames(object table)
    {
        object list=dc.Reflect.Class.fields.Invoke(table);
        if(list is not dc.hl.types.ArrayObj names)return null;
        var keys=new List<object>();
        for(int i=0;i<names.length;i++)
        {
            object name=names.getDyn(i);
            dc.String key=name is dc.String s?s:name is string t?ModCore.Utilities.StringUtils.AsHaxeString(t):null;
            if(key is null)return null;
            keys.Add(key);
        }
        return keys;
    }
    public object Field(object table,object name)=>dc.Reflect.Class.field.Invoke(table,(dc.String)name);
    public object Dynamic(object value)=>HaxeProxy.Runtime.Internals.HaxeProxyHelper.GetProxy<object>(value);
    public IList<object> Slots(object value)
    {
        int length;dc.hl.types.ArrayAccess access;
        if(value is dc.hl.types.ArrayBase array){length=array.length;access=array;}
        else if(value is dc.hl.types.ArrayDyn dyn){length=dyn.get_length();access=dyn;}
        else return null;
        var slots=new List<object>(length<0?0:length);
        for(int j=0;j<length;j++){object slot=access.getDyn(j);slots.Add(slot);}   // as object first: no dynamic call site
        return slots;
    }
}

public static class PadFixGame
{
    // Options.configController(force:Null<Bool>) (f@5789). Its Nullable<bool> parameter cannot be named against the
    // reference set (CS0012, as AnimManager.play in LobbyUiNative), so it is bound once by reflection with an exact
    // signature check (PadFixHooks.Install) and called with force=null, as Options.apply does (Options.hx:704).
    static MethodInfo _configController;
    public static void BindConfigController()
    {
        var m=typeof(dc.Options).GetMethod("configController",BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly);
        var p=m?.GetParameters();
        if(m==null||m.ReturnType!=typeof(void)||p.Length!=1||Nullable.GetUnderlyingType(p[0].ParameterType)!=typeof(bool))
            throw new InvalidOperationException("Pinned method mismatch: Options.configController");
        _configController=m;
    }
    public static void ConfigController(dc.Options o)
    {
        if(_configController==null)throw new InvalidOperationException("Options.configController not bound");
        try{_configController.Invoke(o,new object[]{null});}
        catch(TargetInvocationException e)when(e.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();}
    }
    // The game's own existence test of its options file ($Options.load f@5796 op21-39: FILE, then LEGACY_FILE).
    public static bool OptionsFileExists()
    {
        var options=dc.Options.Class;var exists=dc.tool.File.Class.exists;
        var file=options.FILE;
        if(file is null||exists is null)throw new InvalidOperationException("Options.FILE / tool.File.exists unavailable");
        if(exists.Invoke(file))return true;
        var legacy=options.LEGACY_FILE;
        return legacy is not null&&exists.Invoke(legacy);
    }
    // Any action of gamepad_normal or gamepad_custom bound to a D-pad code; null when a table has an unknown shape
    // (PadFixRules.DpadCodesBound over the game's Reflect, PadFixTablePort).
    public static bool? DpadCodesBound(dc.Options o)
    {
        // gamepad_normal / gamepad_custom are declared dynamic by GameProxy: taken as object (no run-time binder).
        object normalTable=o.gamepad_normal;object customTable=o.gamepad_custom;
        return PadFixRules.DpadCodesBound(PadFixTablePort.Game,normalTable,customTable);
    }
    // The pad the controller reads (Assets.hx:921: Boot.ME.controller.gc.device), else the last active one.
    public static dc.hxd.Pad ActiveDevice(dc.tool.Controller c)
    {
        if(c is null)return null;
        var gc=c.gc;
        if(gc is not null&&gc.device is not null)return gc.device;
        var active=c.activeGamepads;
        if(active is null||active.length<=0)return null;
        object last=active.getDyn(active.length-1);
        return (last as dc.libs.heaps.GamePad)?.device;
    }
}

public static class PadFixHooks
{
    const BindingFlags Static=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    static readonly List<(EventInfo Event,Delegate Handler)> Added=new();
    // The installed boundaries (the unit probe drives the name boundary with a model orig; reflection only).
    public static PadFixLoadBoundary LoadBoundary;public static PadFixNameBoundary NameBoundary;public static PadFixPadBoundary PadBoundary;

    public static void Install(string statePath,bool dpad,bool glyphs)
    {
        var proxy=typeof(dc.Options).Assembly;
        try
        {
            if(dpad)
            {
                var s=PadFixNativeAbi.Validate(proxy.GetType("dc.Hook__Options",true).GetEvent("load",Static),typeof(dc.Options));
                PadFixGame.BindConfigController();
                PadFixStatus.LoadGate=new PadFixGate("load");
                LoadBoundary=new PadFixLoadBoundary(PadFixNativeAbi.Forwarder<Func<object,dc.Options>>(s),statePath,PadFixStatus.LoadGate);
                Add(s.Event,PadFixNativeAbi.Handler(s,LoadBoundary,typeof(PadFixLoadBoundary).GetMethod(nameof(PadFixLoadBoundary.Invoke))));
                PadFixStatus.DpadHooked=true;
            }
            if(glyphs)
            {
                var s=PadFixNativeAbi.Validate(proxy.GetType("dc.hxd.Hook_Pad",true).GetEvent("get_name",Static),typeof(dc.String),typeof(dc.hxd.Pad));
                PadFixStatus.NameGate=new PadFixGate("name");
                NameBoundary=new PadFixNameBoundary(PadFixNativeAbi.Forwarder<Func<object,dc.hxd.Pad,dc.String>>(s),PadFixStatus.NameGate);
                Add(s.Event,PadFixNativeAbi.Handler(s,NameBoundary,typeof(PadFixNameBoundary).GetMethod(nameof(PadFixNameBoundary.Invoke))));
                PadFixStatus.GlyphsHooked=true;
            }
            {
                var s=PadFixNativeAbi.Validate(proxy.GetType("dc.tool.Hook_Controller",true).GetEvent("onPadEnabled",Static),typeof(void),typeof(dc.tool.Controller));
                PadFixStatus.PadGate=new PadFixGate("pad");
                PadBoundary=new PadFixPadBoundary(PadFixNativeAbi.Forwarder<Action<object,dc.tool.Controller>>(s),PadFixStatus.PadGate);
                Add(s.Event,PadFixNativeAbi.Handler(s,PadBoundary,typeof(PadFixPadBoundary).GetMethod(nameof(PadFixPadBoundary.Invoke))));
                PadFixStatus.PadLogHooked=true;
            }
        }
        catch{Uninstall();throw;}
    }
    static void Add(EventInfo e,Delegate handler){Added.Add((e,handler));e.AddEventHandler(null,handler);}
    // Rollback of a failed install (removing a handler that was never added is harmless).
    public static void Uninstall()
    {
        for(int i=Added.Count-1;i>=0;i--){try{Added[i].Event.RemoveEventHandler(null,Added[i].Handler);}catch{}}
        Added.Clear();
        PadFixStatus.DpadHooked=PadFixStatus.GlyphsHooked=PadFixStatus.PadLogHooked=false;
        LoadBoundary=null;NameBoundary=null;PadBoundary=null;
    }
}
