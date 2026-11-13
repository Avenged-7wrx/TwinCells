using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Mono.Cecil;

// Verifies the loaded GameProxy symbolically before CoopDiag reads any GameProxy member (PLAN-ALPHA2.md section 4
// rule 2). Pattern of the standalone CoopCorpseRotationFix (host-death-20261109\corpse-rotation, ValidateLoaded): the
// four generator assemblies are pinned by file hash (their identity checked against the loaded ones), the GameProxy
// image is the loaded one (MVID and name), and the symbolic digest over the frozen roots (CoopDiagPinTable.RootTypes /
// RootMethods = the members this DLL references) must equal CoopDiagPinTable.ProxyFingerprintPin. The raw GameProxy hash
// is only logged, never compared.
public static class CoopDiagProxyCheck
{
    public static string Validate(Assembly proxy,out string raw)
    {
        // CoopPack S1 (PACK-DESIGN.md 4.1): the GameProxy bytes, raw hash, generator pins (the loop that stood here, verbatim in
        // PackProxyImage.Generators) and Cecil model come from the one shared PackProxyImage. Every check is unchanged.
        var image=global::CoopPackRuntime.PackProxyImage.Acquire(proxy);
        try
        {
            raw=image.RawSha256;
            var pins=image.Generators;
            foreach(var pin in CoopDiagPinTable.GeneratorPins)
                if(!pins.TryGetValue(pin.Key,out var actual) || actual!=pin.Value)
                    throw new InvalidOperationException("CoopDiag unsupported generator: "+pin.Key);
            var assembly=image.Definition;
            if(assembly.Modules.Count!=1 || assembly.MainModule.Mvid!=proxy.ManifestModule.ModuleVersionId || assembly.Name.FullName!=proxy.FullName)
                throw new InvalidOperationException("Loaded GameProxy identity differs from the checked image");
            string symbolic=CoopDiagProxyFingerprint.Compute(image,CoopDiagPinTable.RootTypes,CoopDiagPinTable.RootMethods).Sha256;
            if(symbolic!=CoopDiagPinTable.ProxyFingerprintPin)
                throw new InvalidOperationException("CoopDiag unsupported symbolic proxy ABI/IL ("+symbolic.Substring(0,8)+")");
            return symbolic;
        }
        finally{image.Release();}
    }
}
