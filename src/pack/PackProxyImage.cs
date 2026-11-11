using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;

namespace CoopPackRuntime
{
    // S1 (PACK-DESIGN.md section 4.1): one GameProxy read, hash and Cecil model for the six GameProxy verifiers
    // (CorpseRotation, DeadCamera, ReviveFix, RuneDrop, ShrineFix, ShrineDeath).
    //
    // Every member is lazy and computed once per image. The expressions are copied verbatim from the verifiers
    // (*ProxyFingerprint.cs :25-36, :87, :88-93, :239-253, and the :113 enumeration), so every digest stays a pure
    // function of the GameProxy bytes, the roots and the label. Sequential use only: Cecil lazy loading is not
    // thread-safe, and all users run on the game thread.
    //
    // Lifetime: Acquire returns the process-wide shared image (COOP_PACK_SHARED_PROXY_DISABLE=1: a private image per
    // call). Users call Release() when done, which disposes private images only. PackHost releases the shared image
    // after the loader loop and after game init.
    public sealed class PackProxyImage : IDisposable
    {
        public const string SharedSwitch="COOP_PACK_SHARED_PROXY_DISABLE";
        static readonly object SharedGate=new();
        static PackProxyImage _shared;

        readonly object _gate=new();
        readonly string _path;
        byte[] _bytes;
        string _raw;
        IReadOnlyDictionary<string,string> _generators;
        MemoryStream _stream;
        AssemblyDefinition _definition;
        Dictionary<string,TypeDefinition[]> _types;
        Dictionary<string,List<CustomAttribute>> _bindings;
        Dictionary<string,MethodDefinition[]> _methods;
        long _readMs,_parseMs,_indexMs;int _indexed;bool _disposed;

        PackProxyImage(Assembly proxy,string path,byte[] bytes,bool shared){Proxy=proxy;_path=path;_bytes=bytes;Shared=shared;}

        public Assembly Proxy{get;}
        public bool Shared{get;}

        // Loader-phase and game-init users. Asserts that every user asks for the same loaded GameProxy.
        public static PackProxyImage Acquire(Assembly proxy)
        {
            if(proxy==null)throw new ArgumentNullException(nameof(proxy));
            if(Environment.GetEnvironmentVariable(SharedSwitch)=="1")return new PackProxyImage(proxy,proxy.Location,null,false);
            lock(SharedGate)
            {
                if(_shared==null)_shared=new PackProxyImage(proxy,proxy.Location,null,true);
                else if(!ReferenceEquals(_shared.Proxy,proxy))throw new InvalidOperationException("Shared GameProxy image requested for a different GameProxy assembly");
                return _shared;
            }
        }
        // A private image over given bytes (offline checks; ReviveProxyFingerprint.Compute(byte[])). Caller disposes it.
        public static PackProxyImage FromBytes(byte[] bytes)=>new PackProxyImage(null,null,bytes??throw new ArgumentNullException(nameof(bytes)),false);
        public static void ReleaseShared()
        {
            PackProxyImage image;
            lock(SharedGate){image=_shared;_shared=null;}
            image?.Dispose();
        }
        public void Release(){if(!Shared)Dispose();}

        public byte[] Bytes
        {
            get
            {
                lock(_gate)
                {
                    Live();
                    if(_bytes==null){var sw=Stopwatch.StartNew();_bytes=File.ReadAllBytes(_path);_readMs+=sw.ElapsedMilliseconds;}
                    return _bytes;
                }
            }
        }
        public string RawSha256
        {
            get
            {
                lock(_gate)
                {
                    if(_raw==null){var bytes=Bytes;var sw=Stopwatch.StartNew();_raw=Convert.ToHexString(SHA256.HashData(bytes));_readMs+=sw.ElapsedMilliseconds;}
                    return _raw;
                }
            }
        }
        // The four generator pins, with the loaded-identity check of *ProxyFingerprint.cs :28-36 (verbatim).
        public IReadOnlyDictionary<string,string> Generators
        {
            get
            {
                lock(_gate)
                {
                    Live();
                    if(_generators!=null)return _generators;
                    var pins=new Dictionary<string,string>(StringComparer.Ordinal);
                    foreach(var assembly in new[]{typeof(ModCore.Mods.ModBase).Assembly,typeof(HashlinkNET.Compiler.HashlinkCompiler).Assembly,
                        typeof(ModuleDefinition).Assembly,typeof(HaxeProxy.Runtime.HaxeProxyBase).Assembly})
                    {
                        byte[] image=File.ReadAllBytes(assembly.Location);
                        using(var stream=new MemoryStream(image,false))using(var parsed=AssemblyDefinition.ReadAssembly(stream))
                            if(parsed.MainModule.Mvid!=assembly.ManifestModule.ModuleVersionId || parsed.Name.FullName!=assembly.FullName)
                                throw new InvalidOperationException("Loaded generator identity differs from its file");
                        pins.Add(assembly.GetName().Name,Convert.ToHexString(SHA256.HashData(image)));
                    }
                    return _generators=new ReadOnlyDictionary<string,string>(pins);
                }
            }
        }
        // The checked image, as AssemblyDefinition.ReadAssembly(new MemoryStream(bytes,false)) in *ProxyFingerprint.cs :48.
        public AssemblyDefinition Definition
        {
            get
            {
                lock(_gate)
                {
                    if(_definition==null)
                    {
                        var bytes=Bytes;var sw=Stopwatch.StartNew();
                        var stream=new MemoryStream(bytes,false);
                        try{_definition=AssemblyDefinition.ReadAssembly(stream);_stream=stream;}
                        catch{stream.Dispose();throw;}
                        _parseMs+=sw.ElapsedMilliseconds;
                    }
                    return _definition;
                }
            }
        }
        // *ProxyFingerprint.cs :87 (verbatim).
        public Dictionary<string,TypeDefinition[]> TypesByFullName
        {
            get
            {
                lock(_gate)
                {
                    if(_types==null)
                    {
                        var value=Definition;var sw=Stopwatch.StartNew();
                        _types=AllTypes(value.MainModule.Types).GroupBy(t=>t.FullName,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
                        _indexMs+=sw.ElapsedMilliseconds;
                    }
                    return _types;
                }
            }
        }
        // *ProxyFingerprint.cs :88-93 (verbatim). Read-only for its users.
        public Dictionary<string,List<CustomAttribute>> Bindings
        {
            get
            {
                lock(_gate)
                {
                    if(_bindings==null)
                    {
                        var value=Definition;var sw=Stopwatch.StartNew();
                        var bindings=new Dictionary<string,List<CustomAttribute>>(StringComparer.Ordinal);
                        foreach(var attr in value.CustomAttributes.Where(a=>a.AttributeType.FullName=="HaxeProxy.Runtime.Internals.HaxeProxyBindingAttribute"))
                        {
                            string target=BindingTarget(attr);
                            if(!bindings.TryGetValue(target,out var list))bindings.Add(target,list=new List<CustomAttribute>());
                            list.Add(attr);
                        }
                        _bindings=bindings;_indexMs+=sw.ElapsedMilliseconds;
                    }
                    return _bindings;
                }
            }
        }
        // Candidates for the root lookup of *ProxyFingerprint.cs :113 (DeadCamera :121): every method of the same
        // enumeration whose FullName equals name, in enumeration order. The callers keep the original
        // .Single(v=>v.FullName==name) on this sequence, so one match returns it and zero or several throw exactly as
        // before; only the linear scan over all GameProxy methods per root is gone.
        public MethodDefinition[] MethodsByFullName(string name)
        {
            lock(_gate)
            {
                if(_methods==null)
                {
                    var types=TypesByFullName;var sw=Stopwatch.StartNew();
                    var index=new Dictionary<string,List<MethodDefinition>>(StringComparer.Ordinal);int count=0;
                    foreach(var method in types.Values.SelectMany(group=>group).SelectMany(t=>t.Methods))
                    {
                        var key=method.FullName;
                        if(!index.TryGetValue(key,out var list))index.Add(key,list=new List<MethodDefinition>(1));
                        list.Add(method);count++;
                    }
                    var methods=new Dictionary<string,MethodDefinition[]>(index.Count,StringComparer.Ordinal);
                    foreach(var pair in index)methods.Add(pair.Key,pair.Value.ToArray());
                    _methods=methods;_indexed=count;_indexMs+=sw.ElapsedMilliseconds;
                }
                return _methods.TryGetValue(name,out var found)?found:Array.Empty<MethodDefinition>();
            }
        }

        public void Dispose()
        {
            bool log;
            lock(_gate)
            {
                if(_disposed)return;
                _disposed=true;
                log=_bytes!=null&&_path!=null;
                try{_definition?.Dispose();}catch{}
                try{_stream?.Dispose();}catch{}
                _definition=null;_stream=null;_types=null;_bindings=null;_methods=null;_bytes=null;
            }
            if(log)PackLog.Info("GameProxy image: read+hash "+_readMs+" ms, parse "+_parseMs+" ms, index "+_indexed+" methods in "+_indexMs+" ms, shared="+Shared);
        }
        void Live(){if(_disposed)throw new ObjectDisposedException(nameof(PackProxyImage));}

        // *ProxyFingerprint.cs :95-96 (verbatim).
        static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> source)
        {foreach(var t in source){yield return t;foreach(var n in AllTypes(t.NestedTypes))yield return n;}}
        // *ProxyFingerprint.cs :239-253 (verbatim).
        static string BindingTarget(CustomAttribute a)
        {
            if(a.Constructor.FullName!="System.Void HaxeProxy.Runtime.Internals.HaxeProxyBindingAttribute::.ctor(System.Int32,System.Type)")
                throw new InvalidOperationException("Unknown native binding constructor");
            var b=a.GetBlob();int p=6;
            if(b.Length<9 || b[0]!=1 || b[1]!=0)throw new InvalidOperationException("Malformed native binding");
            int head=b[p++],length;
            if((head&0x80)==0)length=head;
            else if((head&0xc0)==0x80){if(p>=b.Length)throw new InvalidOperationException("Truncated binding");length=((head&0x3f)<<8)|b[p++];}
            else if((head&0xe0)==0xc0){if(p+3>b.Length)throw new InvalidOperationException("Truncated binding");length=((head&0x1f)<<24)|(b[p++]<<16)|(b[p++]<<8)|b[p++];}
            else throw new InvalidOperationException("Unknown binding string prefix");
            if(length>b.Length-p-2 || p+length+2!=b.Length || b[b.Length-1]!=0 || b[b.Length-2]!=0)
                throw new InvalidOperationException("Unknown binding payload");
            return new UTF8Encoding(false,true).GetString(b,p,length);
        }
    }
}
