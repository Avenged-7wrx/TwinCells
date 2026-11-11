using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CoopPackRuntime
{
    // Host rollback for CoopSyncFixes, which has no install transaction of its own (PACK-DESIGN.md section 2.6).
    // Runs only when CSF's Initialize throws or fault injection names it. Every step is logged and the next step runs
    // even if one fails. CSF is reached by name only; no CSF file is edited and no CSF type is named here.
    // RepairInitializationComplete (CoopSyncFixes.cs:24/:80; the probe's fixesReady) is reset to false: a real failure
    // leaves it false already (Initialize clears it first, :40), but fault injection runs after a successful Initialize
    // that set it. Other never-reset flags (EliteDeathCompletionReady, WeaponIdentityReady, ...) are left alone:
    // dependents check the part state first.
    public static class CsfRollback
    {
        public const string Part="CoopSyncFixes";
        public const string ReadyProperty="RepairInitializationComplete";
        public static readonly string[] HarmonyOwners={"local.deadcells.syncfixes.1","CoopSyncFixes.RouteGeneratedGraphs"};
        // Delegate fields: Colors.cs:13, CinematicVisualManagers.cs:37, RouteGeneratedGraphs.cs:29, SecretFamily.cs:20, World.cs:401.
        public static readonly string[] HookFields={"BodyDrawHook","CineVisualUpdateHook","GeneratedRouteGraphHook","SecretFamilyHook","WorldLoadHook"};
        // Delegate lists: Elevators.cs:17, JoinLease.cs:17, Routes.cs:40.
        public static readonly string[] HookLists={"ElevatorNativeHooks","TransitionPhaseHooks","RouteHooks"};
        // Native hook events CSF subscribes to (type, event); plus Hook_<Portal subclass>.onActivate, resolved as Routes.cs:48-50.
        static readonly (string Type,string Event)[] HookEvents={
            ("dc.libs.heaps.slib.Hook_HSprite","draw"),("dc.libs.heaps.slib.Hook_AnimManager","_update"),
            ("dc.en.inter.Hook_Elevator","fixedUpdate"),("dc.en.inter.elevator.Hook_SimpleElevator","onActivate"),
            ("dc.pr.Hook_Level","registerEntity"),("dc.pr.Hook_Level","unregisterEntity"),
            ("dc.cine.Hook_LevelTransition","loadNewLevel"),
            ("dc.pr.Hook_Game","genSecretLevel"),("dc.pr.Hook_Game","loadMainLevel"),("dc.pr.Hook_Game","activateSubLevel")};
        const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;

        // Returns true when every step succeeded.
        public static bool Run()
        {
            bool complete=true;
            foreach(var owner in HarmonyOwners)
            {
                var id=owner;
                complete&=Step("unpatch "+id,()=>new Harmony(id).UnpatchSelf());
            }
            Type csf=null;List<EventInfo> events=null;
            complete&=Step("locate "+Part,()=>{csf=PackParts.PackAssembly.GetType(Part,true);});
            if(csf!=null)complete&=Step("reset "+ReadyProperty,()=>{
                var property=csf.GetProperty(ReadyProperty,Static)??throw new MissingMemberException(Part,ReadyProperty);
                var setter=property.GetSetMethod(true)??throw new MissingMethodException(Part,"set_"+ReadyProperty);
                if((bool)property.GetValue(null))setter.Invoke(null,new object[]{false});
            });
            complete&=Step("native hook events",()=>{events=CandidateEvents();});
            if(csf==null||events==null)return false;
            foreach(var name in HookFields)
            {
                var fieldName=name;
                complete&=Step("native hook "+fieldName,()=>{
                    var field=csf.GetField(fieldName,Static)??throw new MissingFieldException(Part,fieldName);
                    if(field.GetValue(null) is Delegate hook){Remove(events,hook);field.SetValue(null,null);}
                });
            }
            foreach(var name in HookLists)
            {
                var fieldName=name;
                complete&=Step("native hooks "+fieldName,()=>{
                    var field=csf.GetField(fieldName,Static)??throw new MissingFieldException(Part,fieldName);
                    var list=field.GetValue(null) as List<Delegate>??throw new InvalidOperationException(fieldName+" is not a List<Delegate>");
                    foreach(var hook in list.ToArray())Remove(events,hook);
                    list.Clear();
                });
            }
            return complete;
        }

        static bool Step(string step,Action action)
        {
            try{action();PackLog.Info(Part+" rollback: "+step+" ok");return true;}
            catch(Exception error){PackLog.Warn(Part+" rollback: "+step+" failed: "+PackHost.Innermost(error));return false;}
        }

        // Removes the handler from every candidate hook event whose handler type is exactly the delegate's type.
        static void Remove(List<EventInfo> events,Delegate hook)
        {
            int matched=0;
            foreach(var ev in events)
                if(ev.EventHandlerType==hook.GetType()){ev.RemoveEventHandler(null,hook);matched++;}
            if(matched==0)throw new InvalidOperationException("no native hook event takes "+hook.GetType().FullName);
        }

        static List<EventInfo> CandidateEvents()
        {
            var proxy=typeof(dc.pr.Game).Assembly;
            var events=new List<EventInfo>();
            foreach(var (typeName,eventName) in HookEvents)
            {
                var ev=proxy.GetType(typeName,false)?.GetEvent(eventName);
                if(ev!=null)events.Add(ev);
            }
            var portal=typeof(dc.en.inter.Portal);
            foreach(var type in portal.Assembly.GetTypes())
            {
                if(!portal.IsAssignableFrom(type))continue;
                var hook=type.Assembly.GetType(type.Namespace+".Hook_"+type.Name)?.GetEvent("onActivate");
                if(hook!=null)events.Add(hook);
            }
            return events;
        }
    }
}
