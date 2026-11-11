using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using dc;
using dc.pr;
using dc.libs.heaps.slib;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using Hashlink;
using Hashlink.Proxy.Objects;
using Hashlink.Proxy.Clousre;
using Hashlink.Reflection.Members;
using Hashlink.Marshaling;
using HarmonyLib;
using SpriteView=Hashlink.Virtuals.virtual_anim_animAllocated_destroyed_frame_get_anim_groupName_lib_remove_set_setFrame_toString_totalFrames_;

// Prototype only. Not integrated into a shared initializer or deployed.
public sealed partial class CoopSyncFixes
{
 private sealed class CineCallbackStamp {public IntPtr Closure,Function,Receiver;}
 private sealed class CineVisualLease
 {
  public HSprite Sprite,Outline;public GhostKing Ghost;public AnimManager Original,Shadow;
  public SpriteLib OriginalLib;public dc.String OriginalGroup;public int OriginalFrame;public IntPtr LibPtr;
  public NetNode Net;public object Connection;public CancellationTokenSource Cts;public string Nonce,Run,Kind,MapId;public int Slot,RemoteId,Generation,Seed;
  public IntPtr SpritePtr,GhostPtr,GamePtr,HeroPtr,LevelPtr,MapPtr,CinePtr;public bool Frozen;
  public CineCallbackStamp Frame,Page;
 }
 private static readonly Dictionary<IntPtr,CineVisualLease> CineVisualBySprite=new();
 private static readonly Dictionary<IntPtr,CineVisualLease> CineVisualByManager=new();
 private static HashSet<IntPtr> CineVisualManagerSnapshot=new();
 private static bool CineVisualManagersInitialized;
 private static int CineVisualOwnerThread;
 private static Delegate CineVisualUpdateHook;
 private static readonly FieldInfo CineVisualKingField=typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.Ghost.KingWeaponsManager",true).GetField("king",BindingFlags.Instance|BindingFlags.NonPublic);
 private static long CineVisualLeasesCreated,CineVisualRestored,CineVisualRetired,CineVisualUnsupported,CineVisualFrozen,CineVisualErrors,CineVisualBlockedRuntime,CineVisualUpdatesForwarded,CineVisualUpdatesCompleted,CineVisualCrossThread,CineVisualMissingOriginal,CineVisualDisplayRestored,CineVisualDisplayRestoreSkipped,CineVisualDisplayRestoreFailed;
 private static string CineVisualLastReason;

 private static void InitializeCineVisualManagers(Harmony harmony)
 {
  if(CineVisualManagersInitialized)return;
  if(ModEntry._net?.IsAlive==true)throw new InvalidOperationException("Initialize cine visual isolation before connecting peers");
  var before=typeof(CoopSyncFixes).GetMethod(nameof(CineVisualBeforeOriginal),Flags);
  var receive=typeof(CoopSyncFixes).GetMethod(nameof(CineVisualBeforeReceive),Flags);
  var weapon=typeof(CoopSyncFixes).GetMethod(nameof(CineVisualBeforeWeapon),Flags);
  var apply=typeof(CoopSyncFixes).GetMethod(nameof(CineVisualBeforeApplyWeapon),Flags);
  var patched=new List<(MethodBase Original,MethodInfo Prefix)>();EventInfo ev=null;
  try{
   var onFrame=typeof(ModEntry).GetMethod("OnFrameUpdate",CineInstance);
   MethodInfo onHero=null;foreach(var method in typeof(ModEntry).GetMethods(CineInstance))if(method.Name=="ModCore.Events.Interfaces.Game.Hero.IOnHeroUpdate.OnHeroUpdate")onHero=method;
   if(onFrame==null || onHero==null)throw new MissingMethodException("Pinned original frame/explicit hero-update boundary missing");
   foreach(var method in new[]{onFrame,onHero}){harmony.Patch(method,prefix:new HarmonyMethod(before){priority=HarmonyLib.Priority.First});patched.Add((method,before));}
   foreach(var name in new[]{"ReceiveGhostWeapons","ReceiveGhostAttacks"}){var method=typeof(ModEntry).GetMethod(name,CineInstance)??throw new MissingMethodException(name);harmony.Patch(method,prefix:new HarmonyMethod(receive){priority=HarmonyLib.Priority.First});patched.Add((method,receive));}
   var applyMethod=typeof(ModEntry).GetMethod("TryApplyRemoteWeaponUpdate",CineInstance)??throw new MissingMethodException("TryApplyRemoteWeaponUpdate");
   harmony.Patch(applyMethod,prefix:new HarmonyMethod(apply){priority=HarmonyLib.Priority.First});patched.Add((applyMethod,apply));
   var manager=typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.Ghost.KingWeaponsManager",true);
   var update=manager.GetMethod("update",CineInstance)??throw new MissingMethodException("KingWeaponsManager.update");
   harmony.Patch(update,prefix:new HarmonyMethod(weapon){priority=HarmonyLib.Priority.First});patched.Add((update,weapon));
   ev=typeof(AnimManager).Assembly.GetType("dc.libs.heaps.slib.Hook_AnimManager",true).GetEvent("_update");
   var signature=ev?.EventHandlerType.GetMethod("Invoke");var pars=signature?.GetParameters();
   if(pars==null || pars.Length!=3 || pars[1].ParameterType!=typeof(AnimManager) || pars[2].ParameterType!=typeof(double) || signature.ReturnType!=typeof(void))throw new InvalidOperationException("Pinned native AnimManager._update signature changed");
   var types=new[]{pars[0].ParameterType,pars[1].ParameterType,pars[2].ParameterType};
   var dm=new DynamicMethod("CsfOwnedCineVisualUpdate",typeof(void),types,typeof(CoopSyncFixes),true);var il=dm.GetILGenerator();var forward=il.DefineLabel();
   il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(CineVisualShouldUpdate),Flags));il.Emit(OpCodes.Brtrue,forward);il.Emit(OpCodes.Ret);
   il.MarkLabel(forward);il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Callvirt,types[0].GetMethod("Invoke"));
   il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(CineVisualUpdateCompleted),Flags));il.Emit(OpCodes.Ret);
   CineVisualUpdateHook=dm.CreateDelegate(ev.EventHandlerType);RuntimeHelpers.PrepareDelegate(CineVisualUpdateHook);ev.AddEventHandler(null,CineVisualUpdateHook);
   CineVisualManagersInitialized=true;
  }catch{
   if(ev!=null && CineVisualUpdateHook!=null)try{ev.RemoveEventHandler(null,CineVisualUpdateHook);}catch{}
   foreach(var patch in patched)try{harmony.Unpatch(patch.Original,patch.Prefix);}catch{}
   CineVisualUpdateHook=null;throw;
  }
 }
 private static bool CineVisualThread(bool adopt=false)
 {
  int now=Environment.CurrentManagedThreadId;if(CineVisualOwnerThread==0 && adopt)CineVisualOwnerThread=now;
  if(now==CineVisualOwnerThread)return true;Interlocked.Increment(ref CineVisualCrossThread);return false;
 }
 private static string CineVisualRun(NetNode net)
 {
  var run=CineRun(net);if(run==null)return null;
  return CineProperty(run,"SessionId")+":"+CineProperty(run,"RunId")+":"+CineProperty(run,"Sequence");
 }
 private static bool CineVisualConnection(NetNode net,int id,out object connection,out string nonce)
 {
  connection=null;nonce=null;
  if(net.IsHost){foreach(var peer in CinematicPeers(net))if(peer.Id==id){connection=peer.Connection;nonce=peer.Nonce;return true;}return false;}
  if(!CineHandshakes.TryGetValue(net,out var hello) || !hello.WelcomeAccepted || !ClientConnections.TryGetValue(net,out var client))return false;
  connection=client;nonce=client.Nonce;return !string.IsNullOrEmpty(nonce);
 }
 private static bool CineVisualFindOwner(HSprite sprite,Level level,out GhostKing ghost,out int slot,out string kind)
 {
  ghost=null;slot=-1;kind=null;int matches=0;var net=ModEntry._net;
  for(int i=0;i<ModEntry.clients.Length && i<ModEntry.clientIds.Length;i++){
   var current=ModEntry.clients[i];if(current==null || current.destroyed || current.life<=0 || current._level?.HashlinkPointer!=level.HashlinkPointer)continue;
   string selected=null;if(current.spr?.HashlinkPointer==sprite.HashlinkPointer)selected="body";
   else if(current.head?.customHeadSpr?.HashlinkPointer==sprite.HashlinkPointer)selected="head";
   if(selected==null)continue;
   var accepted=PresentationGhost(net,level,ModEntry.clientIds[i],level.map.id.ToString());
   if(accepted?.HashlinkPointer!=current.HashlinkPointer)continue;
   ghost=current;slot=i;kind=selected;matches++;
  }return matches==1;
 }
 private static bool CineVisualCallback(HSprite sprite,string name,int allowed,IntPtr receiver,out CineCallbackStamp stamp)
 {
  stamp=null;var raw=((IHashlinkFieldObject)sprite.HashlinkObj).GetFieldValue(name);
  if(raw==null){stamp=new CineCallbackStamp();return true;}
  if(allowed<0 || raw is not HashlinkClosure closure || closure.BindingThis is not IHashlinkPointer bound || bound.HashlinkPointer!=receiver){CineVisualLastReason="callback-"+name+"-unknown-kind-or-receiver";return false;}
  var expected=HashlinkMarshal.Module.GetFunctionByFIndex(allowed) as HashlinkFunction;
  if(expected==null || expected.FunctionIndex!=allowed || closure.FunctionPtr!=expected.EntryPointer){CineVisualLastReason="callback-"+name+"-unknown-function";return false;}
  stamp=new CineCallbackStamp {Closure=closure.HashlinkPointer,Function=closure.FunctionPtr,Receiver=bound.HashlinkPointer};return true;
 }
 private static bool CineVisualSameStamp(CineCallbackStamp a,CineCallbackStamp b)=>a!=null && b!=null && a.Closure==b.Closure && a.Function==b.Function && a.Receiver==b.Receiver;
 private static bool CineVisualCallbacks(CineVisualLease lease,bool capture=false)
 {
  if(lease.Sprite.lib?.HashlinkPointer!=lease.LibPtr){CineVisualLastReason="sprite-library-replaced";return false;}
  if(!CineVisualCallback(lease.Sprite,"onFrameChange",806,lease.GhostPtr,out var frame) || !CineVisualCallback(lease.Sprite,"onPageChange",805,lease.GhostPtr,out var page))return false;
  // Entity closure806 can recursively update outlineDummy. Only null callbacks
  // are admitted there until another concrete native closure has its own proof.
  var outline=lease.Ghost.outlineDummy;
  if(lease.Kind=="body" && outline!=null && !outline.destroyed){
   if(!CineVisualCallback(outline,"onFrameChange",-1,IntPtr.Zero,out _) || !CineVisualCallback(outline,"onPageChange",-1,IntPtr.Zero,out _))return false;
  }
  if(capture){lease.Frame=frame;lease.Page=page;lease.Outline=outline;return true;}
  return CineVisualSameStamp(lease.Frame,frame) && CineVisualSameStamp(lease.Page,page) && lease.Outline?.HashlinkPointer==outline?.HashlinkPointer;
 }
 private static bool CineVisualScope(CineVisualLease lease)
 {
  if(!CinematicPresentationContext(out var game,out var level) || !ReferenceEquals(ModEntry._net,lease.Net) ||
   !ReferenceEquals(typeof(NetNode).GetField("_cts",CineInstance).GetValue(lease.Net),lease.Cts) || lease.Cts==null || lease.Cts.IsCancellationRequested ||
   game.HashlinkPointer!=lease.GamePtr || game.hero.HashlinkPointer!=lease.HeroPtr || level.HashlinkPointer!=lease.LevelPtr || level.map.HashlinkPointer!=lease.MapPtr ||
   level.map.id.ToString()!=lease.MapId || level.map.seed!=lease.Seed || game.curCine.HashlinkPointer!=lease.CinePtr ||
   (int)Field("s_levelIdentityGeneration")!=lease.Generation || CineVisualRun(lease.Net)!=lease.Run ||
   lease.Slot>=ModEntry.clients.Length || lease.Slot>=ModEntry.clientIds.Length || ModEntry.clientIds[lease.Slot]!=lease.RemoteId ||
   ModEntry.clients[lease.Slot]?.HashlinkPointer!=lease.GhostPtr || lease.Ghost.destroyed || lease.Ghost.life<=0 ||
   lease.Ghost._level?.HashlinkPointer!=lease.LevelPtr || PresentationGhost(lease.Net,level,lease.RemoteId,lease.MapId)?.HashlinkPointer!=lease.GhostPtr || lease.Sprite.destroyed || lease.Sprite.HashlinkPointer!=lease.SpritePtr ||
   (lease.Kind=="body"?lease.Ghost.spr?.HashlinkPointer:lease.Ghost.head?.customHeadSpr?.HashlinkPointer)!=lease.SpritePtr)return false;
  return CineVisualConnection(lease.Net,lease.RemoteId,out var connection,out var nonce) && ReferenceEquals(connection,lease.Connection) && nonce==lease.Nonce;
 }
 private static bool CineVisualSameNativeBoundary(CineVisualLease lease)
 {
  // A destroyed-but-not-cleared curCine still blocks original readiness. Keep
  // the owned manager inert until that native boundary actually ends, rather
  // than resuming old callbacks one disposal frame too early.
  var game=Game.Class.ME;var level=game?.hero?._level;
  if(game==null || game.destroyed || game.HashlinkPointer!=lease.GamePtr || game.hero?.HashlinkPointer!=lease.HeroPtr || game.curCine?.HashlinkPointer!=lease.CinePtr ||
   level==null || level.destroyed || game.curLevel?.HashlinkPointer!=lease.LevelPtr || level.HashlinkPointer!=lease.LevelPtr || level.map?.HashlinkPointer!=lease.MapPtr || level.map.id.ToString()!=lease.MapId || level.map.seed!=lease.Seed ||
   !ReferenceEquals(ModEntry._net,lease.Net) || !lease.Net.IsAlive || !ReferenceEquals(typeof(NetNode).GetField("_cts",CineInstance).GetValue(lease.Net),lease.Cts) ||
   lease.Cts==null || lease.Cts.IsCancellationRequested || CineVisualRun(lease.Net)!=lease.Run ||
   lease.Slot>=ModEntry.clients.Length || lease.Slot>=ModEntry.clientIds.Length || ModEntry.clientIds[lease.Slot]!=lease.RemoteId ||
   ModEntry.clients[lease.Slot]?.HashlinkPointer!=lease.GhostPtr || lease.Ghost.destroyed || lease.Sprite.destroyed ||
   lease.Ghost._level?.HashlinkPointer!=lease.LevelPtr ||
   (lease.Kind=="body"?lease.Ghost.spr?.HashlinkPointer:lease.Ghost.head?.customHeadSpr?.HashlinkPointer)!=lease.SpritePtr)return false;
  return CineVisualConnection(lease.Net,lease.RemoteId,out var connection,out var nonce) && ReferenceEquals(connection,lease.Connection) && nonce==lease.Nonce;
 }
 private static void CineVisualLeaveOrFreeze(CineVisualLease lease,string reason)
 {
  if(CineVisualSameNativeBoundary(lease))CineVisualFreeze(lease,reason+"-await-native-cine-exit");else CineVisualRetire(lease,reason);
 }
 private static void CineVisualFreeze(CineVisualLease lease,string reason)
 {
  if(!lease.Frozen){lease.Frozen=true;CineVisualFrozen++;}
  CineVisualLastReason=reason;
  // Do not restore the original callbacks/manager while the cine is still live.
  try{if(lease.Shadow!=null && !lease.Shadow.destroyed)lease.Shadow.needUpdates=false;}catch{CineVisualErrors++;}
 }
 private static void CineVisualRetire(CineVisualLease lease,string reason)
 {
  CineVisualLastReason=reason;bool detached=false;
  try{
   if(lease.Sprite!=null && !lease.Sprite.destroyed && lease.Sprite.HashlinkPointer==lease.SpritePtr && lease.Sprite._animManager?.HashlinkPointer==lease.Shadow?.HashlinkPointer){
    // Restore only this captured display, while the owned shadow is still
    // attached. stop=false is essential: never clear the original anim stack.
    // A foreign library/callback mutation is left alone, not invoked/reverted.
    try{
     bool canRestore=lease.Ghost!=null && !lease.Ghost.destroyed && lease.Ghost.life>0 &&
      (lease.Kind=="body"?lease.Ghost.spr?.HashlinkPointer:lease.Ghost.head?.customHeadSpr?.HashlinkPointer)==lease.SpritePtr &&
      lease.OriginalLib!=null && lease.OriginalGroup!=null && CineVisualCallbacks(lease);
     var group=canRestore?lease.OriginalLib.groups.get(lease.OriginalGroup) as LibGroup:null;
     if(canRestore && group?.frames!=null && lease.OriginalFrame>=0 && lease.OriginalFrame<group.frames.length){
      int frame=lease.OriginalFrame;bool stop=false;lease.Sprite.set(lease.OriginalLib,lease.OriginalGroup,ref frame,ref stop);CineVisualDisplayRestored++;
     }else{CineVisualDisplayRestoreSkipped++;CineVisualLastReason="display-restore-skipped-foreign-or-invalid-visual";}
    }catch{CineVisualDisplayRestoreFailed++;CineVisualLastReason="display-restore-failed";}
    // Never stop, play, tick, clear or destroy the original manager.
    bool originalLive=lease.Original!=null && !lease.Original.destroyed;if(!originalLive)CineVisualMissingOriginal++;
    lease.Sprite._animManager=originalLive?lease.Original:null;CineVisualRestored++;detached=true;
   }else detached=true; // Foreign replacement or destroyed sprite: never overwrite it.
   if(detached && lease.Shadow!=null && !lease.Shadow.destroyed)lease.Shadow.destroy();
  }catch{CineVisualErrors++;if(!detached){CineVisualFreeze(lease,"retire-failed");return;}}
  CineVisualBySprite.Remove(lease.SpritePtr);if(lease.Shadow!=null)CineVisualByManager.Remove(lease.Shadow.HashlinkPointer);
  Volatile.Write(ref CineVisualManagerSnapshot,new HashSet<IntPtr>(CineVisualByManager.Keys));CineVisualRetired++;
 }
 private static void CineVisualBeforeOriginal()
 {
  if(!CineVisualManagersInitialized || CineVisualBySprite.Count==0 || !CineVisualThread())return;
  try{foreach(var lease in new List<CineVisualLease>(CineVisualBySprite.Values))if(!CineVisualScope(lease))CineVisualLeaveOrFreeze(lease,"scope-ended-before-original");}
  catch{CineVisualErrors++;foreach(var lease in CineVisualBySprite.Values)CineVisualFreeze(lease,"scope-query-failed");}
 }
 private static bool CineVisualBeforeReceive()
 {
  try{
   if(!CineVisualManagersInitialized || CineVisualBySprite.Count==0)return true;
   if(!CineVisualThread())return false;CineVisualBeforeOriginal();
   // Preserve whole original queues, including pre-cine backlog; do not consume
   // then reject individual packets merely to protect the detached manager.
   if(CineVisualBySprite.Count>0){CineVisualBlockedRuntime++;return false;}return true;
  }catch{CineVisualErrors++;return CineVisualBySprite.Count==0;}
 }
 private static bool CineVisualBeforeApplyWeapon(int remoteId,ref bool __result)
 {
  try{if(!CineVisualManagersInitialized || CineVisualBySprite.Count==0)return true;if(!CineVisualThread()){__result=false;return false;}
   CineVisualBeforeOriginal();foreach(var lease in CineVisualBySprite.Values)if(lease.RemoteId==remoteId){__result=false;CineVisualBlockedRuntime++;return false;}return true;
  }catch{CineVisualErrors++;__result=false;return CineVisualBySprite.Count==0;}
 }
 private static bool CineVisualBeforeWeapon(object __instance)
 {
  try{if(!CineVisualManagersInitialized || CineVisualBySprite.Count==0)return true;if(!CineVisualThread())return false;
   CineVisualBeforeOriginal();var king=__instance==null?null:CineVisualKingField?.GetValue(__instance) as GhostKing;
   foreach(var lease in CineVisualBySprite.Values)if(king?.HashlinkPointer==lease.GhostPtr){CineVisualBlockedRuntime++;return false;}return true;
  }catch{CineVisualErrors++;return CineVisualBySprite.Count==0;}
 }
 private static bool CineVisualSafeShadowStack(CineVisualLease lease)
 {
  var shadow=lease.Shadow;
  if(shadow==null || shadow.destroyed || shadow.onEnterFrame!=null || shadow.overlap!=null || shadow.stack==null || shadow.stack.length>64 ||
   shadow.spr==null || ((HSprite)shadow.spr).HashlinkPointer!=lease.SpritePtr ||
   shadow.stateAnims==null || shadow.stateAnims.length!=0 || shadow.transitions==null || shadow.transitions.length!=0)return false;
  for(int i=0;i<shadow.stack.length;i++){
   var instance=shadow.stack.getDyn(i) as dc.libs.heaps.slib._AnimManager.AnimInstance;if(instance==null || instance.killAfterPlay || instance.spr==null || ((HSprite)instance.spr).HashlinkPointer!=lease.SpritePtr)return false;
   foreach(var pair in new[]{("onEnd",1213),("onEachLoop",1214)}){
    var raw=((IHashlinkFieldObject)instance.HashlinkObj).GetFieldValue(pair.Item1);
    if(raw is not HashlinkClosure closure || closure.BindingThis is not IHashlinkPointer receiver || receiver.HashlinkPointer!=instance.HashlinkPointer ||
     closure.FunctionPtr!=((HashlinkFunction)HashlinkMarshal.Module.GetFunctionByFIndex(pair.Item2)).EntryPointer)return false;
   }
  }return true;
 }
 private static bool CineVisualShouldUpdate(AnimManager self)
 {
  if(self==null)return true;var pointer=self.HashlinkPointer;
  if(CineVisualOwnerThread!=Environment.CurrentManagedThreadId){if(Volatile.Read(ref CineVisualManagerSnapshot).Contains(pointer)){Interlocked.Increment(ref CineVisualCrossThread);return false;}return true;}
  if(!CineVisualByManager.TryGetValue(pointer,out var lease))return true;
  try{
   if(!CineVisualScope(lease)){CineVisualLeaveOrFreeze(lease,"render-scope-ended");return false;}
   if(lease.Frozen)return false;
   if(lease.Sprite._animManager?.HashlinkPointer!=pointer || !CineVisualCallbacks(lease) || !CineVisualSafeShadowStack(lease)){CineVisualFreeze(lease,"render-callback-or-manager-replaced");return false;}
   CineVisualUpdatesForwarded++;return true;
  }catch{CineVisualErrors++;CineVisualFreeze(lease,"render-validator-error");return false;}
 }
 private static void CineVisualUpdateCompleted(AnimManager self)
 {
  // Called only AFTER the native original returned; never catches its exception.
  if(self!=null && Volatile.Read(ref CineVisualManagerSnapshot).Contains(self.HashlinkPointer))Interlocked.Increment(ref CineVisualUpdatesCompleted);
 }
 private static AnimManager GetCineVisualManager(HSprite sprite)
 {
  if(!CineVisualManagersInitialized || !CineVisualThread(true) || sprite==null || sprite.destroyed)return null;
  CineVisualLease admission=null;
  try{
   CineVisualBeforeOriginal();
   if(!CinematicPresentationContext(out var game,out var level))return null;
   if(game.hero.spr?.HashlinkPointer==sprite.HashlinkPointer || game.hero.heroHead?.customHeadSpr?.HashlinkPointer==sprite.HashlinkPointer){CineVisualUnsupported++;CineVisualLastReason="local-hero-sprite-alias";return null;}
   if(CineVisualBySprite.TryGetValue(sprite.HashlinkPointer,out var lease)){
    if(lease.Frozen)return null;
    if(!CineVisualScope(lease)){CineVisualLeaveOrFreeze(lease,"get-scope-ended");return null;}
    if(sprite._animManager?.HashlinkPointer!=lease.Shadow.HashlinkPointer || !CineVisualCallbacks(lease) || !CineVisualSafeShadowStack(lease) || lease.Shadow.stack.length>=64){CineVisualFreeze(lease,"get-callback-or-manager-replaced");return null;}
    return lease.Shadow;
   }
   if(!CineVisualFindOwner(sprite,level,out var ghost,out var slot,out var kind) || CineVisualBySprite.Count>=16){CineVisualUnsupported++;CineVisualLastReason="unsupported-owner-or-capacity";return null;}
   var net=ModEntry._net;int id=ModEntry.clientIds[slot];var original=sprite._animManager;var run=CineVisualRun(net);
   var cts=typeof(NetNode).GetField("_cts",CineInstance).GetValue(net) as CancellationTokenSource;
   if(original==null || original.destroyed || original.spr==null || ((HSprite)original.spr).HashlinkPointer!=sprite.HashlinkPointer || sprite.lib==null || sprite.groupName==null || sprite.frame<0 ||
    !double.IsFinite(original.genSpeed) || original.genSpeed<=0 || original.genSpeed>8 || run==null ||
    cts==null || cts.IsCancellationRequested || !CineVisualConnection(net,id,out var connection,out var nonce)){CineVisualUnsupported++;CineVisualLastReason="unsupported-original-or-connection";return null;}
   lease=new CineVisualLease {Sprite=sprite,SpritePtr=sprite.HashlinkPointer,Ghost=ghost,GhostPtr=ghost.HashlinkPointer,Original=original,Net=net,Cts=cts,Connection=connection,Nonce=nonce,
    OriginalLib=sprite.lib,OriginalGroup=sprite.groupName,OriginalFrame=sprite.frame,LibPtr=sprite.lib.HashlinkPointer,
    Run=run,Kind=kind,Slot=slot,RemoteId=id,Generation=(int)Field("s_levelIdentityGeneration"),GamePtr=game.HashlinkPointer,HeroPtr=game.hero.HashlinkPointer,LevelPtr=level.HashlinkPointer,MapPtr=level.map.HashlinkPointer,
    MapId=level.map.id.ToString(),Seed=level.map.seed,CinePtr=game.curCine.HashlinkPointer};
   admission=lease;
   if(!CineVisualCallbacks(lease,true)){CineVisualUnsupported++;return null;}
   var shadow=new AnimManager(sprite.ToVirtual<SpriteView>());shadow.onEnterFrame=null;shadow.genSpeed=original.genSpeed;lease.Shadow=shadow;
   if(!CineVisualScope(lease) || sprite._animManager?.HashlinkPointer!=original.HashlinkPointer || !CineVisualCallbacks(lease)){shadow.destroy();return null;}
   CineVisualBySprite.Add(lease.SpritePtr,lease);CineVisualByManager.Add(shadow.HashlinkPointer,lease);
   Volatile.Write(ref CineVisualManagerSnapshot,new HashSet<IntPtr>(CineVisualByManager.Keys));sprite._animManager=shadow;CineVisualLeasesCreated++;return shadow;
  }catch{CineVisualErrors++;CineVisualLastReason="manager-admission-error";if(admission?.Shadow!=null)CineVisualRetire(admission,"admission-rollback");return null;}
 }
}
