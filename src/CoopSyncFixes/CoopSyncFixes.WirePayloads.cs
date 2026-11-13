using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeadCellsMultiplayerMod;

public sealed partial class CoopSyncFixes
{
    // Pre-check of one inner MOB line against its ticket: the base mod's own parser for the kind must accept the payload
    // and every record it yields must carry the ticket's generation and an id at or above the ticket's floor; at least
    // one record, at most 8192; anything else, and any exception, is a reject. The parsers are the same base-mod methods
    // as before, entered through typed delegates instead of MethodInfo.Invoke, and the records are read as their own
    // struct types instead of boxed objects. One kind is different: a MOBREG line (the whole registry table, about 4000
    // chars) is tested on the text itself by CsfWireParsers.RegistryFits, with the rules of the base parser, so the table
    // is not parsed into strings and a list a second time.
    private static bool ValidateWirePayload(string line,WireTicket ticket,int? sender,bool host)
    {
        CsfWireLocal local=null;List<NetNode.MobStateSnapshot> states=null;
        try
        {
            var separator=line.IndexOf('|');if(separator<=0)return false;
            var kind=CsfWireParsers.Kind(line,separator);if(kind==0)return false;
            // line.Substring(separator+1).TrimEnd('\r','\n') as one copy.
            var stop=line.Length;while(stop>separator+1 && (line[stop-1]=='\r' || line[stop-1]=='\n'))stop--;
            if(kind==5)return CsfWireParsers.RegistryFits(line.AsSpan(separator+1,stop-separator-1),ticket);
            var payload=line.Substring(separator+1,stop-separator-1);
            switch(kind)
            {
                case 1:return CsfWireParsers.Fits(CsfWireParsers.States(payload),ticket);
                case 2:return CsfWireParsers.Fits(CsfWireParsers.Moves(payload),ticket);
                case 3:return CsfWireParsers.Fits(CsfWireParsers.Events(payload),ticket);
                case 4:
                    // The parser clears and fills the list it is given; nothing keeps it afterwards.
                    local=CsfWireLocal.Get();states=local.TakeStates();
                    return CsfWireParsers.Binary(payload,states) && CsfWireParsers.Fits(states,ticket);
                case 6:return CsfWireParsers.Attack(payload,out var attack) && attack.Generation==ticket.Generation && attack.Index>=ticket.Floor;
                case 7:return CsfWireParsers.Hit(payload,sender,host && sender.HasValue,out var hit) && hit.Generation==ticket.Generation && hit.MobIndex>=ticket.Floor;
                case 8:return CsfWireParsers.Die(payload,sender,host && sender.HasValue,out var die) && die.Generation==ticket.Generation && die.MobIndex>=ticket.Floor;
                default:return CsfWireParsers.Draw(payload,sender,host && sender.HasValue,out var draws) && CsfWireParsers.Fits(draws,ticket);
            }
        }
        catch{return false;}
        finally{if(states!=null)local.GiveStates(states);}
    }

    // Typed entry points of the base mod's payload parsers, looked up by the same names and flags as before, each on
    // first use of its kind (a lookup that fails throws inside ValidateWirePayload's try and rejects that line only).
    // The registry parser (TryParseMobRegistryPayload) is the one that is no longer called from here: see RegistryFits.
    private static class CsfWireParsers
    {
        private delegate bool CsfAttackParser(string payload,out NetNode.MobAttack attack);
        private delegate bool CsfHitParser(string payload,int? sender,bool senderKnown,out NetNode.MobHit hit);
        private delegate bool CsfDieParser(string payload,int? sender,bool senderKnown,out NetNode.MobDie die);
        private delegate bool CsfDrawParser(string payload,int? sender,bool senderKnown,out List<NetNode.MobDraw> draws);
        private static Func<string,List<NetNode.MobStateSnapshot>> StatesParser;
        private static Func<string,List<NetNode.MobMoveSnapshot>> MovesParser;
        private static Func<string,List<NetNode.MobEventUpdate>> EventsParser;
        private static Func<string,List<NetNode.MobStateSnapshot>,bool> BinaryParser;
        private static CsfAttackParser AttackParser;private static CsfHitParser HitParser;
        private static CsfDieParser DieParser;private static CsfDrawParser DrawParser;
        private static T Bind<T>(Type owner,string method) where T:Delegate=>(T)Delegate.CreateDelegate(typeof(T),owner.GetMethod(method,Flags));

        // The calls. Each of these small methods is compiled without optimization and is never inlined, so the delegate
        // call in it is a plain call to the parser's entry point: the JIT never folds a parser body into the caller, and
        // a Harmony patch on a parser is reached whenever it is installed, as it was with MethodInfo.Invoke.
        private const MethodImplOptions Plain=MethodImplOptions.NoInlining|MethodImplOptions.NoOptimization;
        [MethodImpl(Plain)] internal static List<NetNode.MobStateSnapshot> States(string payload)=>
            (StatesParser??=Bind<Func<string,List<NetNode.MobStateSnapshot>>>(typeof(NetNode),"ParseMobStatesPayload"))(payload);
        [MethodImpl(Plain)] internal static List<NetNode.MobMoveSnapshot> Moves(string payload)=>
            (MovesParser??=Bind<Func<string,List<NetNode.MobMoveSnapshot>>>(typeof(NetNode),"ParseMobMovesPayload"))(payload);
        [MethodImpl(Plain)] internal static List<NetNode.MobEventUpdate> Events(string payload)=>
            (EventsParser??=Bind<Func<string,List<NetNode.MobEventUpdate>>>(typeof(NetNode),"ParseMobEventsPayload"))(payload);
        [MethodImpl(Plain)] internal static bool Binary(string payload,List<NetNode.MobStateSnapshot> states)=>
            (BinaryParser??=Bind<Func<string,List<NetNode.MobStateSnapshot>,bool>>(typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.MobWireBinary",true),"TryParseMobStatesBase64"))(payload,states);
        [MethodImpl(Plain)] internal static bool Attack(string payload,out NetNode.MobAttack attack)=>
            (AttackParser??=Bind<CsfAttackParser>(typeof(NetNode),"TryParseMobAttackPayload"))(payload,out attack);
        [MethodImpl(Plain)] internal static bool Hit(string payload,int? sender,bool senderKnown,out NetNode.MobHit hit)=>
            (HitParser??=Bind<CsfHitParser>(typeof(NetNode),"TryParseMobHitPayload"))(payload,sender,senderKnown,out hit);
        [MethodImpl(Plain)] internal static bool Die(string payload,int? sender,bool senderKnown,out NetNode.MobDie die)=>
            (DieParser??=Bind<CsfDieParser>(typeof(NetNode),"TryParseMobDiePayload"))(payload,sender,senderKnown,out die);
        [MethodImpl(Plain)] internal static bool Draw(string payload,int? sender,bool senderKnown,out List<NetNode.MobDraw> draws)=>
            (DrawParser??=Bind<CsfDrawParser>(typeof(NetNode),"TryParseMobDrawPayload"))(payload,sender,senderKnown,out draws);

        // Kind of the line by the text in front of the first '|': 1 MOBSTATE, 2 MOBMOVE, 3 MOBEVENT, 4 MOBSTATE2, 5 MOBREG,
        // 6 MOBATK, 7 MOBHIT, 8 MOBDIE, 9 MOBDRAW, 0 none of them. The nine names written exactly so are their own
        // upper-case form; every other spelling goes through the original Substring + ToUpperInvariant.
        internal static int Kind(string line,int separator)
        {
            var kind=Exact(line.AsSpan(0,separator));
            return kind!=0?kind:Exact(line.Substring(0,separator).ToUpperInvariant());
        }
        private static int Exact(ReadOnlySpan<char> kind)
        {
            if(kind.SequenceEqual("MOBSTATE"))return 1;
            if(kind.SequenceEqual("MOBMOVE"))return 2;
            if(kind.SequenceEqual("MOBEVENT"))return 3;
            if(kind.SequenceEqual("MOBSTATE2"))return 4;
            if(kind.SequenceEqual("MOBREG"))return 5;
            if(kind.SequenceEqual("MOBATK"))return 6;
            if(kind.SequenceEqual("MOBHIT"))return 7;
            if(kind.SequenceEqual("MOBDIE"))return 8;
            if(kind.SequenceEqual("MOBDRAW"))return 9;
            return 0;
        }

        // The record loop of the pre-check, once per list type (generation field and id field of that record type).
        internal static bool Fits(List<NetNode.MobStateSnapshot> records,WireTicket ticket)
        {
            if(records==null)return false;var count=0;
            foreach(var record in records){if(++count>8192)return false;if(record.Generation!=ticket.Generation || record.Index<ticket.Floor)return false;}
            return count>0;
        }
        internal static bool Fits(List<NetNode.MobMoveSnapshot> records,WireTicket ticket)
        {
            if(records==null)return false;var count=0;
            foreach(var record in records){if(++count>8192)return false;if(record.Generation!=ticket.Generation || record.Index<ticket.Floor)return false;}
            return count>0;
        }
        internal static bool Fits(List<NetNode.MobEventUpdate> records,WireTicket ticket)
        {
            if(records==null)return false;var count=0;
            foreach(var record in records){if(++count>8192)return false;if(record.Generation!=ticket.Generation || record.Index<ticket.Floor)return false;}
            return count>0;
        }
        // The MOBREG pre-check on the payload text "generation|id,type,x,y;id,type,x,y;...", without building anything.
        // It answers what NetNode.TryParseMobRegistryPayload (base mod 0.5.1, release IL 5530-5663) followed by the record
        // loop answered, step by step:
        //   parser: a payload that is empty or all white space, has no '|' behind at least one character, or whose text in
        //     front of the first '|' is not an int (NumberStyles.Integer, invariant) is refused; a rest that is empty or
        //     all white space gives an empty list; otherwise the rest is cut at every ';', empty pieces are dropped, and a
        //     piece becomes an entry (id = its first field, generation = the one in front) when it has at least four
        //     ',' fields, the first is an int (NumberStyles.Integer) and the third and fourth are doubles
        //     (NumberStyles.Float); every other piece is skipped. The type text (second field) never decides anything.
        //   record loop: at least one entry, at most 8192, each with the ticket's generation and an id not below its floor.
        // The same int / double readers are used on the same characters (the string overloads the parser calls read
        // the string as this span). A null ticket ends in the caller's catch as before (only when an entry exists).
        internal static bool RegistryFits(ReadOnlySpan<char> payload,WireTicket ticket)
        {
            if(payload.IsWhiteSpace())return false;
            var bar=payload.IndexOf('|');if(bar<=0)return false;
            if(!int.TryParse(payload.Slice(0,bar),NumberStyles.Integer,CultureInfo.InvariantCulture,out var generation))return false;
            var rest=payload.Slice(bar+1);if(rest.IsWhiteSpace())return false;
            var count=0;
            while(true)
            {
                var cut=rest.IndexOf(';');
                // An empty piece is never an entry. A run of them (";;;;") is stepped over at once instead of piece by
                // piece: a table of a million ';' is then one scan. If nothing but ';' is left, the table has ended.
                if(cut==0){var next=rest.IndexOfAnyExcept(';');if(next<0)return count>0;rest=rest.Slice(next);continue;}
                if(RegistryEntry(cut<0?rest:rest.Slice(0,cut),out var id))
                {
                    if(++count>8192)return false;
                    if(generation!=ticket.Generation || id<ticket.Floor)return false;
                }
                if(cut<0)return count>0;
                rest=rest.Slice(cut+1);
            }
        }
        // True when the parser makes an entry of this piece; id is then the entry's NetId. An empty piece has no ','.
        private static bool RegistryEntry(ReadOnlySpan<char> piece,out int id)
        {
            id=0;
            var first=piece.IndexOf(',');if(first<0)return false;
            var tail=piece.Slice(first+1);var second=tail.IndexOf(',');if(second<0)return false;
            tail=tail.Slice(second+1);var third=tail.IndexOf(',');if(third<0)return false;
            var x=tail.Slice(0,third);var y=tail.Slice(third+1);var fourth=y.IndexOf(',');if(fourth>=0)y=y.Slice(0,fourth);
            return int.TryParse(piece.Slice(0,first),NumberStyles.Integer,CultureInfo.InvariantCulture,out id) &&
                double.TryParse(x,NumberStyles.Float,CultureInfo.InvariantCulture,out _) &&
                double.TryParse(y,NumberStyles.Float,CultureInfo.InvariantCulture,out _);
        }
        internal static bool Fits(List<NetNode.MobDraw> records,WireTicket ticket)
        {
            if(records==null)return false;var count=0;
            foreach(var record in records){if(++count>8192)return false;if(record.Generation!=ticket.Generation || record.MobIndex<ticket.Floor)return false;}
            return count>0;
        }
    }
}
