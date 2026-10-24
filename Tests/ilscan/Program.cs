using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// IL 元数据成员列表器（临时开发工具）：不加载类型，纯元数据列出指定类型的字段/属性/方法
class Program
{
    static int Main(string[] args)
    {
        string dll = args.Length > 0 ? args[0] : @"f:\Worldbox_v0.51.2\Worldbox_v0.51.2\worldbox_Data\Managed\Assembly-CSharp.dll";
        string typeName = args.Length > 1 ? args[1] : "City";
        string filter = args.Length > 2 ? args[2] : "";

        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        // 找到目标类型及其基类链
        var chain = new System.Collections.Generic.List<string>();
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            if (md.GetString(t.Name) == typeName && string.IsNullOrEmpty(md.GetString(t.Namespace)))
            {
                var cur = t;
                while (true)
                {
                    chain.Add(md.GetString(cur.Name));
                    if (!cur.BaseType.IsNil)
                    {
                        var baseHandle = cur.BaseType;
                        string baseName = "?";
                        if (baseHandle.Kind == HandleKind.TypeReference)
                        {
                            var tr = md.GetTypeReference((TypeReferenceHandle)baseHandle);
                            baseName = md.GetString(tr.Name);
                            // 查基类 TypeDefinition
                            var found = default(EntityHandle);
                            foreach (var th2 in md.TypeDefinitions)
                            {
                                var t2 = md.GetTypeDefinition(th2);
                                if (md.GetString(t2.Name) == baseName) { found = th2; break; }
                            }
                            if (found.IsNil) { chain.Add(baseName + "(外部)"); break; }
                            cur = md.GetTypeDefinition((TypeDefinitionHandle)found);
                            continue;
                        }
                        chain.Add(baseName + "(外部)");
                        break;
                    }
                    break;
                }
                break;
            }
        }
        if (chain.Count == 0) { Console.WriteLine("类型未找到: " + typeName); return 1; }
        Console.WriteLine("继承链: " + string.Join(" -> ", chain));

        // 列出链上每个类型的字段/属性/方法（按 filter 过滤）
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            string name = md.GetString(t.Name);
            if (!chain.Contains(name)) continue;
            Console.WriteLine($"── {name} ──");
            foreach (var fh in t.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                string fn = md.GetString(f.Name);
                if (string.IsNullOrEmpty(filter) || fn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string vis = f.Attributes.HasFlag(System.Reflection.FieldAttributes.Public) ? "public" : "nonpublic";
                    Console.WriteLine($"  field {vis} {fn}");
                }
            }
            foreach (var ph in t.GetProperties())
            {
                var p = md.GetPropertyDefinition(ph);
                string pn = md.GetString(p.Name);
                if (string.IsNullOrEmpty(filter) || pn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    Console.WriteLine($"  prop  {pn}");
            }
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                string mn = md.GetString(m.Name);
                if (string.IsNullOrEmpty(filter) || mn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string sig = "";
                    try
                    {
                        var dec = new MethodDecoder(md, m);
                        sig = dec.DecodeSignature();
                    }
                    catch { }
                    Console.WriteLine($"  method {mn} {sig}");
                }
            }
        }
        return 0;
    }
}

/// <summary>方法签名解码（2026-08-19 增强：参数/返回类型）。</summary>
class MethodDecoder
{
    private readonly MetadataReader _md;
    private readonly MethodDefinition _m;
    public MethodDecoder(MetadataReader md, MethodDefinition m) { _md = md; _m = m; }

    public string DecodeSignature()
    {
        var sig = _m.DecodeSignature(new TypeProvider(), null);
        var ps = new System.Collections.Generic.List<string>();
        foreach (var p in sig.ParameterTypes) ps.Add(p);
        string ret = sig.ReturnType;
        return ret == "Void" ? $"({string.Join(", ", ps)})" : $"({string.Join(", ", ps)}) -> {ret}";
    }
}

class TypeProvider : ISignatureTypeProvider<string, object>
{
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode code) => code.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        return reader.GetString(td.Name);
    }
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        return reader.GetString(tr.Name);
    }
    public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
