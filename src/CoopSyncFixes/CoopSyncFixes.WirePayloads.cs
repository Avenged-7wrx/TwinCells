using System;
using System.Collections;
using System.Collections.Generic;
using DeadCellsMultiplayerMod;

public sealed partial class CoopSyncFixes
{
    private static bool ValidateWirePayload(string line,WireTicket ticket,int? sender,bool host)
    {
        try
        {
            var separator=line.IndexOf('|');if(separator<=0)return false;
            var kind=line.Substring(0,separator).ToUpperInvariant();var payload=line.Substring(separator+1).TrimEnd('\r','\n');
            IEnumerable records=null;string key="Index";var gen="Generation";
            object Parse(string method,params object[] args)=>typeof(NetNode).GetMethod(method,Flags).Invoke(null,args);
            switch(kind)
            {
                case "MOBSTATE": records=Parse("ParseMobStatesPayload",payload) as IEnumerable;break;
                case "MOBMOVE": records=Parse("ParseMobMovesPayload",payload) as IEnumerable;break;
                case "MOBEVENT": records=Parse("ParseMobEventsPayload",payload) as IEnumerable;break;
                case "MOBSTATE2":
                {
                    var list=new List<NetNode.MobStateSnapshot>();
                    var binary=typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.MobWireBinary",true);
                    if(!(bool)binary.GetMethod("TryParseMobStatesBase64",Flags).Invoke(null,new object[] {payload,list}))return false;
                    records=list;break;
                }
                case "MOBREG":
                {var args=new object[] {payload,null};if(!(bool)Parse("TryParseMobRegistryPayload",args))return false;records=args[1] as IEnumerable;key="NetId";break;}
                case "MOBATK":
                {var args=new object[] {payload,null};if(!(bool)Parse("TryParseMobAttackPayload",args))return false;records=new[] {args[1]};break;}
                case "MOBHIT": case "MOBDIE": case "MOBDRAW":
                {
                    var args=new object[] {payload,sender,host && sender.HasValue,null};
                    if(!(bool)Parse(kind=="MOBHIT"?"TryParseMobHitPayload":kind=="MOBDIE"?"TryParseMobDiePayload":"TryParseMobDrawPayload",args))return false;
                    records=kind=="MOBDRAW"?args[3] as IEnumerable:new[] {args[3]};key="MobIndex";break;
                }
                default:return false;
            }
            if(records==null)return false;int count=0;
            foreach(var record in records)
            {
                if(++count>8192 || record==null)return false;
                var type=record.GetType();
                if((int)type.GetField(gen).GetValue(record)!=ticket.Generation || (int)type.GetField(key).GetValue(record)<ticket.Floor)return false;
            }
            return count>0;
        }
        catch{return false;}
    }
}
