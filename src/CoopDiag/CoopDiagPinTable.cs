using System;
using System.Collections.Generic;

// Pins of CoopDiag (pure data; also compiled into the managed pin check checks\CoopDiagPinChecks.cs, which re-reads
// every row from the pinned files with System.Reflection.Metadata).
//
// IL rows: SHA-256 of the method body IL bytes (MethodBody.GetILAsByteArray at run time = MethodBodyBlock.GetILBytes in
// metadata), as the Alpha1 parts pin (alpha2-opt\source\OptClient.cs Pin). Release member names only (PLAN-ALPHA2.md
// section 4 rule 1): every row was read from the release DLL 378BE6B0 (base) or the DCCM 35.13.5 HaxeProxy.dll 0DF96973.
// A row is resolved by declaring type, name and parameter count (exactly one match, static-ness checked) and must match
// both length and hash, or the part does not install.
public static class CoopDiagPinTable
{
    public const string BaseModSha256="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativeSha256="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";   // hlboot.dat v35
    public const string HaxeProxySha256="0DF9697335FFDBA4461E599CEE063BC41E4B76C881CCAEDB08AE16DC4A27A8B2";

    public sealed class Row
    {
        public string Key,Assembly,Type,Method;public int Params;public bool Static;public int IlLength;public string Sha256,Use;
        public Row(string key,string assembly,string type,string method,int parameters,bool isStatic,int ilLength,string sha256,string use)
        {Key=key;Assembly=assembly;Type=type;Method=method;Params=parameters;Static=isStatic;IlLength=ilLength;Sha256=sha256;Use=use;}
    }
    const string ME="DeadCellsMultiplayerMod.ModEntry",MS="DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization",
        GH="DeadCellsMultiplayerMod.GhostHero",GK="DeadCellsMultiplayerMod.Ghost.GhostBase.GhostKing",
        KWM="DeadCellsMultiplayerMod.Ghost.KingWeaponsManager",NN="NetNode",IM="HaxeProxy.Runtime.Internals.Inheritance.InheritanceManager";

    // Use: "frame" heartbeat anchor (postfix), "rx" receive timestamp (prefix), "ready" hero-tick readiness (postfix),
    // "flush" issue 16 stopwatches, "avatar" issue 15 stopwatches, "call" read-only calls (never patched).
    public static readonly Row[] Rows=
    {
        new Row("frame","base",ME,"OnFrameUpdate",1,false,270,"2246501C5B61327925F02715B4034722C6E9648434F7111AB114EBFED22717B3","frame"),
        new Row("rx","base",NN,"TryHandleFastPathLine",2,false,68,"ADADDE52AF55BF3BC6A2566E018A2617CEB271074A76E04E8B2A07A3A6BBF457","rx"),
        new Row("ready","base",ME,"IsHeroRuntimeReadyForCoop",2,true,288,"F2644BD7CE204C2CE5EE56E6EEB48B499E5EE5B6AA0677A9B15C22FC23059D0C","ready"),

        new Row("frameCore","base",MS,"OnFrameUpdateCore",1,false,418,"9A23EB241A31A4B222E07A8BC57270B4B55BEB8A9319A717285B8E5DE36033A0","flush"),
        new Row("dirty","base",MS,"FlushHostDirtyMobQueue",1,true,510,"1F4C6D6232D37A52E33D76866CE45C1016DAF0CF4AB58C95807C7068E0AE4473","flush"),
        new Row("bossScan","base",MS,"ScanHostBossPartDespawns",1,true,700,"462F6F799549D155D71767F61EFDFBBE92942D3CF75D57DED2EF00839C82B4A1","flush"),
        new Row("registry","base",MS,"FlushHostMobRegistry",1,true,190,"ECE17A89DD982741086E6AC86958E5BF326EC39F1813173FE7FBACAC7FACD0A4","flush"),
        new Row("priority","base",MS,"FlushHostPriorityResync",1,true,442,"240623F1D86FDAD2519A683341F541AC72F056D4C1A378FCAB134FD142FCA6E8","flush"),
        new Row("pass","base",MS,"FlushHostPriorityResyncPass",4,true,326,"25FF39C164C329DBB4C5D6003858E0E79A889E8E4760062246F818F8E2E83A2A","flush"),
        new Row("tombstone","base",MS,"FlushHostDeathTombstoneResends",1,true,678,"E2D3E0600DE6750C7CCA13665496057E1DBB38941F6B88BA14F99AE807535774","flush"),
        new Row("activation","base",MS,"RunHostRemotePlayerActivationPass",1,true,331,"F4F2503B464552E243D92E2EDB9362E27178A70A9CF18EDE8690DFAC09422C45","flush"),

        new Row("slot","base",ME,"EnsureClientKingSlot",1,false,602,"8931E5F5F0248203DA2EA9F7E2AE5AA88FAADFEB7CECCFEEEF1A9D3715C44C93","avatar"),
        new Row("create","base",GH,"CreateGhostKing",2,false,434,"C4E984AF7351D20B546D2C6303EB90856101CC4B72281035B51C5E205DF7F76A","avatar"),
        new Row("init","base",GK,"init",0,false,47,"3AF5200537204184E04009895407246D7F5A36712E0C4604AFE5EECD85FE22AE","avatar"),
        new Row("initGfx","base",GK,"initGfx",0,false,436,"F75B51613432DAEA7AA53A223DE150CA49258A0CFE8A5EA433B01C7CE7F188C8","avatar"),
        new Row("deps","base",GK,"EnsureRuntimeDependencies",0,false,110,"B96AA6D9576E6A261C9F1F94B4B43D70615654F722DB4DD1DC3F11BDFC6E085A","avatar"),
        new Row("weapons","base",KWM,".ctor",2,false,65,"380D19A43DAA86DAA61BA28B90D8E6765586DF7A24BBDF22C8EB5D2064D315B7","avatar"),
        new Row("skin","base",GK,"ApplyRemoteSkin",1,false,110,"3A994755DFACD12C238D37068A9C7A519836F17585060ED80B2B9FF88DE179BE","avatar"),
        new Row("head","base",ME,"RecreateClientHead",1,false,415,"736FDDF971C89F162C38FE679B0D39F81E61E044934F898418056EBC345C590F","avatar"),
        new Row("label","base",GH,"SetLabel",2,false,549,"111A045C32DB81041E88D2DD35FAC0AF208342B75BEF9BD53F35A6F29E25620E","avatar"),
        new Row("dive","base",ME,"ApplyCachedRemoteDiveSkillInfoIfAny",2,false,51,"E4B7183BE32EDCEDDC4E33B003894B41E7CED955316B07F97D75FFD3E9F61C34","avatar"),
        new Row("notify","base",MS,"NotifyPlayerCombatStateChanged",1,true,76,"B2E27B9A1265950BF9A80BA1FC0D4C9C56FE50B624D8A8F1334EE47FC4851946","avatar"),
        new Row("check","haxeproxy",IM,"Check",3,true,159,"A59439D863B5B81B3FC7AF4409B6019A2F36FBCE1A5AA1D40B2E322407E06909","avatar"),

        new Row("isDowned","base",ME,"IsLocalPlayerDowned",0,true,20,"6D4919CB5E9052A0D72DD0EF46CC7E28276BB39018B131E61DCE69FD8986453F","call"),
        new Row("inRun","base","DeadCellsMultiplayerMod.LobbySession","IsInActualRun",0,true,14,"9163568D5920EEAD88209D8F9B938EA666E82A5D266A37FE3A64F6AFDFB89A1C","call"),
        new Row("isBossMob","base","DeadCellsMultiplayerMod.Mobs.Bosses.BossSyncHelpers","IsBossMob",1,true,88,"872788C212B308E8B0962721F77D4AAC9824FD4D716BE930D4D474C77D943533","call"),
        new Row("showPerf","base","DeadCellsMultiplayerMod.MultiplayerSettingsStorage","get_ShowPerfLogs",0,true,41,"CC82833C49BD76C1AF9F26D6EDC35D38ED60EB624F731F35B8B4CB9CBF5BB70F","call"),
        new Row("bossTraceSetting","base","DeadCellsMultiplayerMod.MultiplayerSettingsStorage","get_DebugBossSyncTrace",0,true,41,"B7D28A080BE2A81CAFFF3C9AA45E3820C3D7FF136C5AB01F390750BAEBC61EDE","call"),
        new Row("netAlive","base",NN,"get_IsAlive",0,false,73,"DF7301143B75925C856BC8066819C45FE0A86271C32C42E108C394E6E3EDFD44","call"),
        new Row("netHost","base",NN,"get_IsHost",0,false,10,"8C6536BC50071C1664FEFE3D337DE57DFC723FAF18B226CCDD7CAF2964D73104","call"),
        new Row("instance","base",ME,"get_Instance",0,true,6,"1BD2EBF071AAC7EB536D0DDB223A83D7890F0812C43E409B1B64576AF7A13CB6","call"),
    };

    // Fields read (never written): declaring type, name, field type full name (reflection FieldType.FullName; null: the
    // generic type is checked in CoopDiagPins), static.
    public sealed class FieldRow
    {
        public string Key,Type,Name,FieldType;public bool Static;
        public FieldRow(string key,string type,string name,string fieldType,bool isStatic){Key=key;Type=type;Name=name;FieldType=fieldType;Static=isStatic;}
    }
    public static readonly FieldRow[] Fields=
    {
        new FieldRow("doorToken",ME,"_localLastDoorMarkerToken","System.Int32",false),
        new FieldRow("doorLevel",ME,"_localLastDoorMarkerLevelId","System.String",false),
        new FieldRow("remoteDowned",ME,"_remoteDowned",null,false),   // Dictionary<int,ModEntry+RemoteDownedState>: only Count is read
        new FieldRow("bossLevels",ME,"BossLevelIds",null,true),       // HashSet<string>: checked in CoopDiagPins
        new FieldRow("net",ME,"_net","NetNode",true),
        new FieldRow("me",ME,"me","dc.en.Hero",true),
        new FieldRow("levelToken",MS,"s_levelIdentityToken","System.Int32",true),
        new FieldRow("levelReady",MS,"s_levelIdentityReady","System.Boolean",true),
        new FieldRow("tracked",MS,"trackedMobs",null,true),            // List<dc.en.Mob>
        new FieldRow("sync",MS,"Sync","System.Object",true),
        new FieldRow("scratch",MS,"s_batchMobsScratch",null,true),     // List<dc.en.Mob>
        new FieldRow("registrySendFrame",MS,"s_lastHostMobRegistrySendFrame","System.Double",true),
    };

    // GameProxy generator pins (the four DCCM 35.13.5 assemblies that generate GameProxy), as every Alpha1 symbolic
    // verifier pins them (CorpseRotationProxyFingerprint.cs GeneratorPins).
    public static readonly KeyValuePair<string,string>[] GeneratorPins=
    {
        new KeyValuePair<string,string>("ModCore","630FBBCEDFE8B2E66C5F75234BE80C2EC42FB178C30706E7398583D9FA1A14AE"),
        new KeyValuePair<string,string>("HashlinkNET.Compiler","4E6E19C786FF26475A1FEFC6D3A0F718EA429A901770F4FDD68A81F388602B62"),
        new KeyValuePair<string,string>("Mono.Cecil","831DCA77470D85CB6FFBEA3072DAA7A3DF5B7C9FCFD9C3F43674A9BE99D4BFCF"),
        new KeyValuePair<string,string>("HaxeProxy",HaxeProxySha256),
    };
    // Symbolic GameProxy fingerprint over the frozen roots below (CoopDiagProxyFingerprint). The roots are exactly the
    // GameProxy-scoped TypeReferences and method MemberReferences of CoopDiag.dll (CoopDiagProxyFingerprint.ExtractRoots,
    // = *ProxyFingerprint.cs :80-82). They are frozen here, as the pack freezes the Alpha1 verifiers' roots, so the same
    // digest holds standalone and inside CoopPack.dll (whose references cover every part). Build.ps1 refuses a build whose
    // extracted roots differ from these or whose digest differs from the pin.
    public const string ProxyFingerprintPin="2D99AD0A487B464F6D2DFBB9BBE3EFB87D89EA2200A9E3253661F573B10E8A43";
    public static readonly string[] RootTypes={
        "dc.Entity","dc.GameCinematic","dc.Main","dc.String","dc._Main","dc.en.Hero","dc.en.Mob","dc.hl.types.ArrayAccess",
        "dc.hl.types.ArrayBase","dc.hl.types.ArrayObj","dc.hxd.Pad","dc.level.LevelMap","dc.libs.GitVersion","dc.libs.Process",
        "dc.libs._GitVersion","dc.libs.heaps.GamePad","dc.pr.Game","dc.pr.Level","dc.pr._Game","dc.steam.Api","dc.steam._Api_",
        "dc.tool.Controller","dc.tool.ControllerAccess","dc.tool.DLC","dc.tool._DLC",
    };
    public static readonly string[] RootMethods={
        "System.Boolean dc.Entity::get_destroyed()",
        "System.Boolean dc.en.Mob::aiLocked()",
        "System.Boolean dc.libs.Process::get_destroyed()",
        "System.Boolean dc.pr.Level::get_isSubLevel()",
        "System.Boolean dc.steam._Api_::get_active()",
        "System.Boolean dc.tool._DLC::get_installMaskCacheDirty()",
        "System.Int32 dc.Entity::get_life()",
        "System.Int32 dc.Entity::get_maxLife()",
        "System.Int32 dc._Main::get_GAME_VERSION()",
        "System.Int32 dc.hl.types.ArrayBase::get_length()",
        "System.Int32 dc.libs.Process::get_uniqId()",
        "System.Int32 dc.tool._DLC::get_installMask()",
        "System.Object dc.hl.types.ArrayAccess::getDyn(System.Int32)",
        "dc.Entity dc.en.Mob::get_aTarget()",
        "dc.Entity dc.en.Mob::get_nemesisTarget()",
        "dc.GameCinematic dc.pr.Game::get_curCine()",
        "dc.String dc.hxd.Pad::get_name()",
        "dc.String dc.level.LevelMap::get_id()",
        "dc.String dc.libs._GitVersion::get_SHORT_HASH()",
        "dc._Main dc.Main::get_Class()",
        "dc.en.Hero dc.pr.Game::get_hero()",
        "dc.hl.types.ArrayObj dc.tool.Controller::get_activeGamepads()",
        "dc.hxd.Pad dc.libs.heaps.GamePad::get_device()",
        "dc.level.LevelMap dc.pr.Level::get_map()",
        "dc.libs._GitVersion dc.libs.GitVersion::get_Class()",
        "dc.pr.Game dc.pr._Game::get_ME()",
        "dc.pr.Level dc.Entity::get__level()",
        "dc.pr._Game dc.pr.Game::get_Class()",
        "dc.steam._Api_ dc.steam.Api::get_Class()",
        "dc.tool.Controller dc.tool.ControllerAccess::get_parent()",
        "dc.tool.ControllerAccess dc.pr.Game::get_controller()",
        "dc.tool._DLC dc.tool.DLC::get_Class()",
    };
}
