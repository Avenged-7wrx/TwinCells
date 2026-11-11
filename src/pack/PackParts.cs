using System;
using System.Collections.Generic;
using System.Reflection;

namespace CoopPackRuntime
{
    // Part table row. The rows themselves are generated from parts.json into PartTable.g.cs by Build-Pack.ps1, so adding
    // a part is a parts.json row plus its sources; no host code changes.
    public sealed class PartRow
    {
        public PartRow(string name,string version,string entryType,string switchName,PartEvents events,string rollback,string[] dependencies)
        {Name=name;Version=version;EntryType=entryType;Switch=switchName;Events=events;Rollback=rollback;Dependencies=dependencies??Array.Empty<string>();}
        public string Name{get;}               // former mod name; PartInfo.Name
        public string Version{get;}            // former mod version; PartInfo.Version
        public string EntryType{get;}          // original ModBase entry type, global namespace
        public string Switch{get;}             // COOP_PACK_<PART>_DISABLE: "1" skips the part
        public PartEvents Events{get;}         // DCCM events the host forwards to this part
        public string Rollback{get;}           // host rollback hook, or null (the part rolls back itself)
        public string[] Dependencies{get;}     // former modinfo dependencies (documentation only)
    }

    [Flags]
    public enum PartEvents{None=0,FrameUpdate=1,GameInit=2}

    public enum PartState{Pending,Installed,Deferred,SwitchedOff,Missing,Failed}

    // Part states and the in-pack companion lookup (PACK-DESIGN.md section 3).
    public static class PackParts
    {
        static readonly object Gate=new();
        static readonly Dictionary<string,PartState> States=Initial();
        static readonly Dictionary<string,string> Errors=new(StringComparer.Ordinal);

        public static Assembly PackAssembly=>typeof(PackParts).Assembly;
        public static IReadOnlyList<PartRow> Rows=>PartTable.Rows;

        static Dictionary<string,PartState> Initial()
        {
            var states=new Dictionary<string,PartState>(StringComparer.Ordinal);
            foreach(var row in PartTable.Rows)states[row.Name]=PartState.Pending;
            return states;
        }

        // Unknown names read as Missing.
        public static PartState State(string part)
        {
            lock(Gate)return part!=null&&States.TryGetValue(part,out var state)?state:PartState.Missing;
        }
        public static string Error(string part)
        {
            lock(Gate)return part!=null&&Errors.TryGetValue(part,out var error)?error:null;
        }
        internal static void Set(string part,PartState state,string error=null)
        {
            lock(Gate){States[part]=state;if(error!=null)Errors[part]=error;}
        }

        // In-pack meaning of "the companion DLL with the pinned hash is installed": the part is in this installation and
        // has not failed or been switched off. Pending counts, as a loaded companion type counted before. PackHost marks
        // every pack-switched-off, absent or unloadable row before any part runs, so Pending only means "still to run".
        public static bool Present(string part)
        {
            var state=State(part);
            return state==PartState.Pending||state==PartState.Installed||state==PartState.Deferred;
        }

        // The companion type of a present part, from this assembly only; null otherwise.
        public static Type FindType(string part,string typeName)=>Present(part)?PackAssembly.GetType(typeName,false):null;
    }
}
