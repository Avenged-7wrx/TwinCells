using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace CoopPackRuntime
{
    // S2 (PACK-DESIGN.md section 4.1): memoized file SHA-256, uppercase hex exactly like
    // Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))). Keyed by full path (OrdinalIgnoreCase) and valid
    // for the process lifetime: the pinned files are loaded assemblies or the native boot image. Each part keeps its own
    // constants and comparisons; only the hashing call is shared. A failed read is not cached and throws exactly what
    // File.ReadAllBytes throws.
    public static class PackPins
    {
        static readonly ConcurrentDictionary<string,string> Memo=new(StringComparer.OrdinalIgnoreCase);

        public static string Sha256(string path)
        {
            if(string.IsNullOrEmpty(path))return Compute(path);          // same exception as the original expression
            string key;
            try{key=Path.GetFullPath(path);}catch{return Compute(path);}
            if(Memo.TryGetValue(key,out var known))return known;
            var hash=Compute(path);
            Memo.TryAdd(key,hash);
            return hash;
        }
        static string Compute(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
