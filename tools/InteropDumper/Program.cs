using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Usage: dumper <dll> [TypeName...] -> types with fields/method signatures (filtered to names given)
class Prov : ISignatureTypeProvider<string, object> {
  readonly MetadataReader md; public Prov(MetadataReader m) { md = m; }
  public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString().ToLower();
  public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
  public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
  public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => "spec";
  public string GetSZArrayType(string e) => e + "[]";
  public string GetArrayType(string e, ArrayShape s) => e + "[,]";
  public string GetByReferenceType(string e) => "ref " + e;
  public string GetPointerType(string e) => e + "*";
  public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
  public string GetGenericTypeParameter(object c, int i) => "T" + i;
  public string GetGenericMethodParameter(object c, int i) => "M" + i;
  public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
  public string GetModifiedType(string m, string u, bool r) => u;
  public string GetPinnedType(string e) => e;
}
class P {
  static void Main(string[] a) {
    using var fs = File.OpenRead(a[0]);
    using var pe = new PEReader(fs);
    var md = pe.GetMetadataReader();
    var prov = new Prov(md);
    var only = a.Skip(1).ToHashSet();
    foreach (var th in md.TypeDefinitions) {
      var t = md.GetTypeDefinition(th);
      var name = md.GetString(t.Name);
      if (only.Count > 0 && !only.Contains(name)) continue;
      Console.WriteLine("T " + name);
      foreach (var ph in t.GetProperties()) {
        var p = md.GetPropertyDefinition(ph);
        var sig = p.DecodeSignature(prov, null);
        Console.WriteLine($"  P {sig.ReturnType} {md.GetString(p.Name)}");
      }
      foreach (var mh in t.GetMethods()) {
        var m = md.GetMethodDefinition(mh);
        var n = md.GetString(m.Name);
        if (n.StartsWith("get_") || n.StartsWith("set_") || n.StartsWith(".c")) continue;
        var sig = m.DecodeSignature(prov, null);
        var pn = m.GetParameters().Select(h => md.GetString(md.GetParameter(h).Name)).Where(s => s.Length > 0).ToList();
        var ps = sig.ParameterTypes.Select((p, i) => p + " " + (i < pn.Count ? pn[i] : ""));
        Console.WriteLine($"  M {(m.Attributes.HasFlag(System.Reflection.MethodAttributes.Static) ? "static " : "")}{sig.ReturnType} {n}({string.Join(", ", ps)})");
      }
    }
  }
}
