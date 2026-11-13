using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using ModCore.Mods;
using ModCore.Events.Interfaces.Game;

namespace CoopPackRuntime
{
    // The pack host (PACK-DESIGN.md sections 2.2-2.6). It constructs each original ModBase entry type unchanged, in the
    // part-table order, removes it from the DCCM EventSystem, calls its Initialize, and forwards the three DCCM events
    // the parts use, each inside its own try/catch and fault gate. Parts are found by type name only: this file never
    // names a part type, so a bisect build without some parts still compiles and a type-load failure stays in its part.
    public static class PackHost
    {
        public const string PackVersion="0.2.0-alpha";
        public const string PackSwitch="COOP_PACK_DISABLE";
        public const string FaultInjection="COOP_QA_PACK_FAIL_AFTER";
        const BindingFlags AnyInstance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;

        sealed class Live
        {
            public PartRow Row;public ModBase Part;public PartGate Gate;
        }

        static readonly object Gate=new();
        static readonly List<Live> Parts=new();
        static Live[] _frame=Array.Empty<Live>();
        static Live[] _gameInit=Array.Empty<Live>();
        static bool _ran,_gameInitDone;
        static MethodInfo _removeReceiver;

        public static void Run(global::CoopPack host)
        {
            lock(Gate)
            {
                if(_ran)return;
                _ran=true;
                try{RunCore(host);}
                catch(Exception error){PackLog.Error("host failure: "+Innermost(error));try{Publish();}catch{}}
                finally{try{PackProxyImage.ReleaseShared();}catch{}}
            }
        }

        static void RunCore(global::CoopPack host)
        {
            var total=Stopwatch.StartNew();
            var rows=PartTable.Rows;
            var location=typeof(global::CoopPack).Assembly.Location;
            string sha;
            try{sha=PackPins.Sha256(location);}catch(Exception error){sha="unavailable ("+error.Message+")";}
            PackLog.Info("CoopPack "+PackVersion+" starting: sha256="+sha+" parts="+rows.Length);

            // 1. Legacy guard: a separate Coop module loaded next to the pack would double-patch under the same Harmony
            //    IDs and make the type-name lookups ambiguous. Detection is by global type name (CSF's legacy assembly
            //    has a random simple name).
            var legacy=FindLegacy(location);
            if(legacy!=null){PackLog.Error("Not installed: "+legacy);return;}

            // 2. Whole-pack switch.
            if(Environment.GetEnvironmentVariable(PackSwitch)=="1"){PackLog.Info("Switched off by "+PackSwitch);return;}

            _removeReceiver=ResolveRemoveReceiver()??throw new InvalidOperationException("DCCM EventSystem.RemoveReceiver(IEventReceiver) not found");

            // 3a. Pre-pass, before any part runs: every pack-switched-off row is SwitchedOff and every row whose entry type
            //     is absent is Missing (or Failed when its type cannot even be loaded), so that an EARLIER row's companion
            //     lookup (PackParts.FindType, R1-R5) already sees a LATER part as absent. Design 2.5: dependents of a
            //     pack-switched-off part behave as if that module were not installed. Only rows still to run stay Pending.
            //     The log lines stay in table order (3b).
            var plan=new Planned[rows.Length];
            for(int i=0;i<rows.Length;i++)
            {
                var row=rows[i];
                try
                {
                    if(Environment.GetEnvironmentVariable(row.Switch)=="1"){plan[i].Off=true;PackParts.Set(row.Name,PartState.SwitchedOff);continue;}
                    plan[i].Type=PackParts.PackAssembly.GetType(row.EntryType,false);   // throws on a broken dependency even with false
                    if(plan[i].Type==null)PackParts.Set(row.Name,PartState.Missing);
                }
                catch(Exception error)
                {
                    plan[i].LoadError=Innermost(error);
                    PackParts.Set(row.Name,PartState.Failed,plan[i].LoadError);
                }
            }

            // 3b. Parts, in table order. Nothing escapes a row; the forwarding lists are published even if it did.
            int installed=0,deferred=0,off=0,failed=0,missing=0;bool rollbackIncomplete=false;
            try
            {
                for(int i=0;i<rows.Length;i++)
                {
                    var row=rows[i];var name=row.Name;
                    if(plan[i].Off){off++;PackLog.Info(name+" switched off by "+row.Switch);continue;}
                    if(plan[i].LoadError!=null){failed++;PackLog.Warn(name+" "+row.Version+" not installed: "+plan[i].LoadError);continue;}
                    if(plan[i].Type==null){missing++;PackLog.Warn(name+" missing from this build");continue;}
                    var watch=Stopwatch.StartNew();
                    ModBase part=null;
                    try
                    {
                        part=(ModBase)Activator.CreateInstance(plan[i].Type,PartInfo(host,row));   // the Module ctor registers it with DCCM
                        RemoveReceiver(part);                                                      // the host forwards its events (D4)
                        part.Initialize();
                        if(Environment.GetEnvironmentVariable(FaultInjection)==name)
                        {
                            // QA only (N9c). Honoured only where the host can undo a successful Initialize; any other part
                            // would be marked Failed while its patches and hooks stayed live.
                            if(row.Rollback!=null)throw new InvalidOperationException("QA fault injection");
                            PackLog.Warn(name+" QA fault injection ignored: no host rollback hook");
                        }
                        var live=new Live{Row=row,Part=part,Gate=new PartGate(name)};
                        lock(Parts)Parts.Add(live);
                        if((row.Events&PartEvents.GameInit)!=0)
                        {
                            PackParts.Set(name,PartState.Deferred);deferred++;
                            PackLog.Info(name+" "+row.Version+" deferred to game init");
                        }
                        else
                        {
                            PackParts.Set(name,PartState.Installed);installed++;
                            PackLog.Info(name+" "+row.Version+" installed in "+watch.ElapsedMilliseconds+" ms");
                        }
                    }
                    catch(Exception error)
                    {
                        var message=Innermost(error);
                        PackParts.Set(name,PartState.Failed,message);failed++;
                        if(row.Rollback!=null&&!RunRollback(row.Rollback))rollbackIncomplete=true;
                        if(part!=null)try{RemoveReceiver(part);}catch{}             // idempotent; a failed part gets no events
                        PackLog.Warn(name+" "+row.Version+" not installed: "+message);
                    }
                }
            }
            finally{Publish();}

            // 4. Release the shared GameProxy image (bounds its memory to the loader loop).
            PackProxyImage.ReleaseShared();

            // 5. Summary.
            PackLog.Info("Summary: installed="+installed+" deferred="+deferred+" off="+off+" failed="+failed+" missing="+missing+" totalMs="+total.ElapsedMilliseconds
                +(rollbackIncomplete?"; rollback incomplete; restart recommended":""));
        }

        struct Planned{public bool Off;public Type Type;public string LoadError;}

        // Publishes the forwarding lists from the parts installed so far (table order).
        static void Publish()
        {
            var frame=new List<Live>();var gameInit=new List<Live>();
            lock(Parts)
                foreach(var live in Parts)
                {
                    if((live.Row.Events&PartEvents.FrameUpdate)!=0)frame.Add(live);
                    if((live.Row.Events&PartEvents.GameInit)!=0)gameInit.Add(live);
                }
            _frame=frame.ToArray();_gameInit=gameInit.ToArray();
        }

        // DCCM IOnFrameUpdate, forwarded in table order to the installed parts that use it. Never throws.
        public static void Frame(double dt)
        {
            Live[] parts=_frame;
            for(int i=0;i<parts.Length;i++)
            {
                var live=parts[i];
                if(live.Gate.Tripped)continue;
                try
                {
                    if(PackParts.State(live.Row.Name)!=PartState.Installed)continue;
                    ((IOnFrameUpdate)live.Part).OnFrameUpdate(dt);
                    live.Gate.Ok();
                }
                catch(Exception error){try{live.Gate.Fail("frame",error);}catch{}}
            }
        }

        // DCCM IOnGameInit: installs the deferred parts. A failure stays in its part (intended change: today it is
        // rethrown into Boot.init). Releases the GameProxy image a deferred verifier acquired. Never throws.
        public static void GameInit()
        {
            lock(Gate)
            {
                if(_gameInitDone)return;
                _gameInitDone=true;
                try
                {
                    foreach(var live in _gameInit)
                    {
                        var name=live.Row.Name;
                        if(PackParts.State(name)!=PartState.Deferred)continue;
                        var watch=Stopwatch.StartNew();
                        try
                        {
                            ((IOnGameInit)live.Part).OnGameInit();
                            PackParts.Set(name,PartState.Installed);
                            PackLog.Info(name+" installed at game init in "+watch.ElapsedMilliseconds+" ms");
                        }
                        catch(Exception error)
                        {
                            var message=Innermost(error);
                            PackParts.Set(name,PartState.Failed,message);
                            PackLog.Warn(name+" not installed at game init: "+message);
                        }
                    }
                }
                catch(Exception error){PackLog.Error("host failure: "+Innermost(error));}
                finally{try{PackProxyImage.ReleaseShared();}catch{}}
            }
        }

        // Unwraps TargetInvocationException / TypeInitializationException to the innermost message.
        public static string Innermost(Exception error)
        {
            if(error==null)return "";
            while((error is TargetInvocationException||error is TypeInitializationException)&&error.InnerException!=null)error=error.InnerException;
            return error.Message;
        }

        // PartInfo (section 2.3). Built with Activator because ModInfo has required members. ModRoot/ModDataRoot are the
        // host's FolderInfo objects (a second FolderInfo with the same name throws); they are copied by reflection so
        // this assembly needs no reference to ModCore.Common (gate G6).
        static ModInfo PartInfo(global::CoopPack host,PartRow row)
        {
            var info=(ModInfo)Activator.CreateInstance(typeof(ModInfo));
            info.Name=row.Name;
            info.Version=row.Version;
            info.Type="mod";
            info.DCCMVersion="35.13.5";
            info.Dependencies=new List<string>(row.Dependencies);
            foreach(var folder in new[]{"ModRoot","ModDataRoot"})
            {
                var property=typeof(ModInfo).GetProperty(folder,AnyInstance)??throw new InvalidOperationException("ModInfo."+folder+" missing");
                property.SetValue(info,property.GetValue(host.Info));
            }
            return info;
        }

        // ModCore.Events.EventSystem lives in ModCore.Common, which no original part referenced; reached by reflection.
        static MethodInfo ResolveRemoveReceiver()
        {
            Type receiver=null;
            foreach(var candidate in typeof(ModBase).GetInterfaces())if(candidate.FullName=="ModCore.Events.IEventReceiver"){receiver=candidate;break;}
            var system=receiver?.Assembly.GetType("ModCore.Events.EventSystem",false);
            return system?.GetMethod("RemoveReceiver",BindingFlags.Public|BindingFlags.Static,null,new[]{receiver},null);
        }
        static void RemoveReceiver(ModBase part)
        {
            try{_removeReceiver.Invoke(null,new object[]{part});}
            catch(TargetInvocationException error)when(error.InnerException!=null){throw error.InnerException;}
        }

        static bool RunRollback(string hook)
        {
            try
            {
                if(hook=="CsfRollback")return CsfRollback.Run();
                PackLog.Warn("unknown rollback hook "+hook);
                return false;
            }
            catch(Exception error){PackLog.Warn("rollback "+hook+" failed: "+Innermost(error));return false;}
        }

        // Returns a description of the first legacy Coop module found, or null.
        static string FindLegacy(string packLocation)
        {
            var pack=PackParts.PackAssembly;
            string packDir=null;
            try{packDir=Path.GetFullPath(Path.GetDirectoryName(packLocation)??"");}catch{}
            var names=new List<string>();
            foreach(var row in PartTable.Rows)names.Add(row.EntryType);
            names.AddRange(PartTable.GuardTypes);
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if(ReferenceEquals(assembly,pack))continue;
                try
                {
                    if(assembly.IsDynamic)continue;
                    foreach(var name in names)
                        if(assembly.GetType(name,false)!=null)
                            return "legacy module "+name+" loaded from "+assembly.Location+"; remove the old coremod\\mods\\<folder> (the Twin Cells updater does this)";
                    if(assembly.GetName().Name=="CoopSteamInviteWorker")
                    {
                        var dir=Path.GetFullPath(Path.GetDirectoryName(assembly.Location)??"");
                        if(packDir==null||!string.Equals(dir,packDir,StringComparison.OrdinalIgnoreCase))
                            return "legacy module CoopSteamInviteWorker loaded from "+assembly.Location+"; remove the old coremod\\mods\\<folder> (the Twin Cells updater does this)";
                    }
                }
                catch{}
            }
            return null;
        }
    }
}
