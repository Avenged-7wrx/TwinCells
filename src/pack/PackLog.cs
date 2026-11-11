using System;
using System.Runtime.CompilerServices;

namespace CoopPackRuntime
{
    // All [CoopPack] lines (PACK-DESIGN.md section 6). The text is pre-formatted and written as one literal
    // property, so the rendered message is exactly "[CoopPack] <text>" (no Serilog quoting). Never throws.
    public static class PackLog
    {
        public const string Prefix="[CoopPack] ";
        public static void Info(string text){try{Write(0,text);}catch{}}
        public static void Warn(string text){try{Write(1,text);}catch{}}
        public static void Error(string text){try{Write(2,text);}catch{}}
        // Kept out of line so a missing Serilog (offline harnesses) fails inside the callers' try blocks.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Write(int level,string text)
        {
            if(level==0)Serilog.Log.Information("{Text:l}",Prefix+text);
            else if(level==1)Serilog.Log.Warning("{Text:l}",Prefix+text);
            else Serilog.Log.Error("{Text:l}",Prefix+text);
        }
    }
}
