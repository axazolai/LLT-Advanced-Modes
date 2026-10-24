using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LegionModes.Core.Contracts;

namespace LegionModes.Updates;

public sealed record CompatibilityReport(bool Compatible, IReadOnlyList<string> Problems, int ReferencesChecked);

public static class CompatibilityChecker
{
    private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                            | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Regex ModTail = new(@"^(.*?)((?: modreq\([^()]*\))+)(&?)$", RegexOptions.Compiled);
    private static readonly Regex ModItem = new(@" modreq\([^()]*\)", RegexOptions.Compiled);
    private static readonly Regex Unresolved = new(@"(?<!!)!\d+", RegexOptions.Compiled);

    private static readonly Dictionary<ushort, OpCode> OpCodeTable = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => (ushort)o.Value);

    // Every assembly the plugin references that exists in the new host directory.
    public static IReadOnlyCollection<string> HostAssembliesOf(string pluginAssembly, string hostDir)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = File.OpenRead(pluginAssembly);
            using var pe = new PEReader(s);
            var md = pe.GetMetadataReader();
            foreach (var h in md.AssemblyReferences)
            {
                var name = md.GetString(md.GetAssemblyReference(h).Name);
                if (File.Exists(Path.Combine(hostDir, name + ".dll")) || File.Exists(Path.Combine(hostDir, name + ".exe")))
                    result.Add(name);
            }
        }
        catch { /* an unreadable plugin gives an empty set; Check reports the file */ }
        return result;
    }

    // The assemblies the plugin loads into the host (LegionModes.dll and the LegionModes.Core.dll beside it) reference host assemblies by
    // version; a host copy with a lower version than the reference fails to load (FileLoadException). One problem per such reference.
    public static IReadOnlyList<string> ReferenceVersionProblems(string pluginAssembly, string hostDir)
    {
        var problems = new List<string>();
        var core = Path.Combine(Path.GetDirectoryName(pluginAssembly) ?? "", "LegionModes.Core.dll");
        foreach (var path in new[] { pluginAssembly, core }.Where(File.Exists))
        {
            try
            {
                using var s = File.OpenRead(path);
                using var pe = new PEReader(s);
                var md = pe.GetMetadataReader();
                foreach (var h in md.AssemblyReferences)
                {
                    var r = md.GetAssemblyReference(h);
                    var name = md.GetString(r.Name);
                    var host = new[] { ".dll", ".exe" }.Select(e => Path.Combine(hostDir, name + e)).FirstOrDefault(File.Exists);
                    if (host is null) continue;
                    var hostVersion = VersionOf(host);
                    if (hostVersion is not null && hostVersion < r.Version)
                        problems.Add($"{Path.GetFileName(path)} references {name} {r.Version}, the host has {hostVersion}");
                }
            }
            catch (Exception ex) { problems.Add($"references of {Path.GetFileName(path)} cannot be read: {ex.Message}"); }
        }
        return problems;
    }

    private static Version? VersionOf(string assemblyFile)
    {
        try
        {
            using var s = File.OpenRead(assemblyFile);
            using var pe = new PEReader(s);
            return pe.GetMetadataReader().GetAssemblyDefinition().Version;
        }
        catch { return null; }   // not an assembly: Check reports the file where it is needed
    }

    // pluginAssembly: the built LegionModes.dll; hostDir: extracted {app} of the new release;
    // hostAssemblies: simple names that count as host (compared case-insensitively).
    public static CompatibilityReport Check(string pluginAssembly, string hostDir,
        IReadOnlyCollection<string> hostAssemblies, IReadOnlyList<ReflectionContract> contracts)
    {
        var problems = new List<string>();
        var count = 0;
        try
        {
            count = Run(pluginAssembly, hostDir, new HashSet<string>(hostAssemblies, StringComparer.OrdinalIgnoreCase),
                contracts, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"compatibility check failed: {ex.GetType().Name}: {ex.Message}");
        }
        problems = problems.Distinct().ToList();
        return new CompatibilityReport(problems.Count == 0, problems, count);
    }

    private static int Run(string pluginAssembly, string hostDir, HashSet<string> hostSet,
        IReadOnlyList<ReflectionContract> contracts, List<string> problems)
    {
        if (!File.Exists(pluginAssembly)) { problems.Add($"plugin assembly not found: {pluginAssembly}"); return 0; }
        if (!Directory.Exists(hostDir)) { problems.Add($"host directory not found: {hostDir}"); return 0; }

        problems.AddRange(ReferenceVersionProblems(pluginAssembly, hostDir));

        using var peStream = File.OpenRead(pluginAssembly);
        using var pe = new PEReader(peStream);
        var md = pe.GetMetadataReader();

        // Runtime first so the core assembly is always the running runtime's; host files fill in the rest.
        // WindowsBase, System.Drawing and Microsoft.VisualBasic are facades in Microsoft.NETCore.App; the real types are in
        // the WindowsDesktop copy, which therefore wins for those names.
        var desktop = WindowsDesktopRuntimeDlls().ToList();
        var desktopNames = new HashSet<string>(desktop.Select(Path.GetFileNameWithoutExtension)!, StringComparer.OrdinalIgnoreCase);
        var paths = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            .Where(p => !desktopNames.Contains(Path.GetFileNameWithoutExtension(p)!))
            .Concat(desktop)
            .Concat(Directory.GetFiles(hostDir, "*.dll")).Concat(Directory.GetFiles(hostDir, "*.exe"))
            .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib");
        var assemblies = new Dictionary<string, Assembly?>(StringComparer.OrdinalIgnoreCase);
        var perAsm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var provider = new SignatureTypeNameProvider();
        var count = 0;
        var derivedFrom = new HashSet<string>();   // full names of host classes some plugin type derives from

        Assembly? Load(string asm, string what)
        {
            if (assemblies.TryGetValue(asm, out var cached)) return cached;
            Assembly? loaded = null;
            var file = new[] { ".dll", ".exe" }.Select(e => Path.Combine(hostDir, asm + e)).FirstOrDefault(File.Exists);
            if (file is null) problems.Add($"host assembly file {asm}.dll/.exe missing in {hostDir} (needed for {what})");
            else
            {
                try { loaded = mlc.LoadFromAssemblyPath(file); }
                catch (Exception ex) { problems.Add($"host assembly {Path.GetFileName(file)} cannot be read: {ex.Message}"); }
            }
            assemblies[asm] = loaded;
            return loaded;
        }

        void Hit(string asm) { count++; perAsm[asm] = perAsm.GetValueOrDefault(asm) + 1; }

        Type? HostType(TypeReference tr, string asm, string what)
        {
            var typeName = FullName(md, tr);
            var a = Load(asm, what);
            if (a is null) return null;
            try
            {
                var t = a.GetType(typeName);
                if (t is null) problems.Add($"type {typeName} missing in {asm}");
                return t;
            }
            catch (Exception ex) { problems.Add($"type {typeName} in {asm} cannot be resolved: {ex.Message}"); return null; }
        }

        // A TypeRef (or generic instantiation of one) into a host assembly.
        bool TryHost(EntityHandle e, out TypeReference tr, out string asm, out ImmutableArray<string> args)
        {
            tr = default; asm = ""; args = ImmutableArray<string>.Empty;
            var h = ParentTypeReference(md, e);
            if (h is null) return false;
            tr = md.GetTypeReference(h.Value);
            var a = HostAssemblyOf(md, tr);
            if (a is null || !hostSet.Contains(a)) return false;
            asm = a;
            if (e.Kind == HandleKind.TypeSpecification)
            {
                provider.LastArgs = ImmutableArray<string>.Empty;
                md.GetTypeSpecification((TypeSpecificationHandle)e).DecodeSignature(provider, null);
                args = provider.LastArgs;
            }
            return true;
        }

        // 1. The plugin's own types that implement host interfaces or derive from host classes.
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            var tdName = FullName(md, td);
            var isInterface = (td.Attributes & TypeAttributes.Interface) != 0;
            var isAbstract = (td.Attributes & TypeAttributes.Abstract) != 0;

            var chain = new List<TypeDefinition> { td };
            var bt = td.BaseType;
            while (!bt.IsNil && bt.Kind == HandleKind.TypeDefinition)
            {
                var b = md.GetTypeDefinition((TypeDefinitionHandle)bt);
                chain.Add(b);
                bt = b.BaseType;
            }

            var impl = new HashSet<string>();
            foreach (var c in chain)
            {
                foreach (var mh in c.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    impl.Add(MethodKey(md.GetString(m.Name), m.DecodeSignature(provider, null)));
                }
                foreach (var ih in c.GetMethodImplementations())
                {
                    var decl = md.GetMethodImplementation(ih).MethodDeclaration;
                    if (decl.Kind != HandleKind.MemberReference) continue;
                    var mr = md.GetMemberReference((MemberReferenceHandle)decl);
                    if (mr.GetKind() == MemberReferenceKind.Method)
                        impl.Add(MethodKey(md.GetString(mr.Name), mr.DecodeMethodSignature(provider, null)));
                }
            }

            // The host base class (first non-plugin ancestor); its public implementations count as inherited.
            TypeReference btr = default;
            var basm = "";
            var bargs = ImmutableArray<string>.Empty;
            Type? btype = null;
            var hostChain = new List<Type>();
            var concreteClass = !isAbstract && !isInterface;
            if (!isInterface && TryHost(bt, out btr, out basm, out bargs))
            {
                Hit(basm);
                btype = HostType(btr, basm, tdName);
                if (btype is not null)
                {
                    try
                    {
                        hostChain = ClassChain(btype).ToList();
                        foreach (var t in hostChain) if (t.FullName is not null) derivedFrom.Add(t.FullName);
                        for (var i = 0; i < hostChain.Count; i++)
                            foreach (var m in hostChain[i].GetMethods(AllMembers).Where(x => x.IsPublic && !x.IsStatic && !x.IsAbstract))
                                impl.Add(HostMethodKey(m, i == 0 ? bargs : default));
                    }
                    catch (Exception ex) { problems.Add($"{tdName}: base {FullName(md, btr)} cannot be resolved: {ex.Message}"); btype = null; }
                }
            }

            // Interfaces of this type and, for a concrete class, of every plugin ancestor.
            for (var ci = 0; ci < chain.Count; ci++)
            {
                if (ci != 0 && !concreteClass) break;
                foreach (var ih in chain[ci].GetInterfaceImplementations())
                {
                    var itf = md.GetInterfaceImplementation(ih).Interface;
                    if (!TryHost(itf, out var itr, out var iasm, out var iargs)) continue;
                    if (ci == 0) Hit(iasm);
                    var itype = HostType(itr, iasm, tdName);
                    if (itype is null || !concreteClass) continue;
                    try
                    {
                        foreach (var (it, subst) in new[] { (itype, true) }.Concat(itype.GetInterfaces().Select(i => (i, false))))
                            foreach (var m in it.GetMethods(AllMembers).Where(x => x.IsAbstract && !x.IsStatic))
                            {
                                var key = HostMethodKey(m, subst ? iargs : default);
                                if (Unresolved.IsMatch(key)) continue;
                                if (!impl.Contains(key))
                                    problems.Add($"{tdName} does not implement {TypeName(it, false)}.{m.Name}{Clean(key)}");
                            }
                    }
                    catch (Exception ex) { problems.Add($"{tdName}: interface {FullName(md, itr)} cannot be resolved: {ex.Message}"); }
                }
            }

            if (btype is null) continue;
            try
            {
                if (btype.IsSealed) problems.Add($"{tdName} derives from {TypeName(btype, false)}, which is now sealed");

                if (concreteClass)
                {
                    var concrete = new HashSet<string>();
                    for (var i = 0; i < hostChain.Count; i++)
                        foreach (var m in hostChain[i].GetMethods(AllMembers).Where(x => x.IsVirtual && !x.IsStatic))
                        {
                            var key = HostMethodKey(m, i == 0 ? bargs : default);
                            if (!m.IsAbstract) { concrete.Add(key); continue; }
                            if (Unresolved.IsMatch(key) || concrete.Contains(key) || impl.Contains(key)) continue;
                            problems.Add($"{tdName} does not implement abstract {TypeName(hostChain[i], false)}.{m.Name}{Clean(key)}");
                        }
                }

                var ancestors = chain.Skip(1).SelectMany(c => c.GetMethods()).Select(md.GetMethodDefinition)
                    .Select(m => MethodKey(md.GetString(m.Name), m.DecodeSignature(provider, null))).ToHashSet();
                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if ((m.Attributes & MethodAttributes.Virtual) == 0 || (m.Attributes & MethodAttributes.NewSlot) != 0) continue;
                    var name = md.GetString(m.Name);
                    var sig = m.DecodeSignature(provider, null);
                    var key = MethodKey(name, sig);
                    if (ancestors.Contains(key)) continue;
                    MethodInfo? target = null;
                    for (var i = 0; i < hostChain.Count && target is null; i++)
                        target = hostChain[i].GetMethods(AllMembers).FirstOrDefault(hm => hm.IsVirtual && !hm.IsStatic && hm.Name == name
                            && (hm.IsPublic || hm.IsFamily || hm.IsFamilyOrAssembly)
                            && (HostMethodKey(hm, i == 0 ? bargs : default) is var hk && (hk == key || Unresolved.IsMatch(hk))));
                    if (target is null) problems.Add($"{tdName}.{name}{Format(sig)} overrides nothing in {TypeName(btype, false)}");
                    else if (target.IsFinal) problems.Add($"{tdName}.{name}{Format(sig)} overrides {TypeName(target.DeclaringType!, false)}.{name}, which is now sealed");
                }
            }
            catch (Exception ex) { problems.Add($"{tdName}: base {FullName(md, btr)} cannot be resolved: {ex.Message}"); }
        }

        // Field accesses: instance or static, read from the IL of the plugin's methods.
        var instUse = new HashSet<MemberReferenceHandle>();
        var statUse = new HashSet<MemberReferenceHandle>();
        foreach (var mh in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(mh);
            if (m.RelativeVirtualAddress == 0) continue;
            try { ScanFieldUse(pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes()!, instUse, statUse); }
            catch { problems.Add($"cannot read IL of {md.GetString(m.Name)}; field staticness unchecked"); }
        }

        // 2. Type references.
        foreach (var h in md.TypeReferences)
        {
            var tr = md.GetTypeReference(h);
            var asm = HostAssemblyOf(md, tr);
            if (asm is null || !hostSet.Contains(asm)) continue;
            Hit(asm);
            var t = HostType(tr, asm, FullName(md, tr));
            if (t is null) continue;
            try
            {
                var ok = t.IsPublic || t.IsNestedPublic
                    || ((t.IsNestedFamily || t.IsNestedFamORAssem) && t.DeclaringType?.FullName is { } dn && derivedFrom.Contains(dn));
                if (!ok) problems.Add($"type {t.FullName} in {asm} is not accessible from the plugin");
            }
            catch (Exception ex) { problems.Add($"type {FullName(md, tr)} in {asm} cannot be resolved: {ex.Message}"); }
        }

        // 3. Member references.
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (!TryHost(mr.Parent, out var tr, out var asm, out _)) continue;
            Hit(asm);
            var typeName = FullName(md, tr);
            var name = md.GetString(mr.Name);
            var a = Load(asm, $"{typeName}.{name}");
            if (a is null) continue;
            try
            {
                var type = a.GetType(typeName);
                if (type is null) { problems.Add($"type {typeName} missing in {asm} (member {name})"); continue; }
                bool Accessible(bool pub, bool fam, bool famOrAsm, Type declaring) =>
                    pub || ((fam || famOrAsm) && declaring.FullName is { } dn && derivedFrom.Contains(dn));
                if (mr.GetKind() == MemberReferenceKind.Field)
                {
                    var ft = mr.DecodeFieldSignature(provider, null);
                    bool? wantStatic = statUse.Contains(h) == instUse.Contains(h) ? null : statUse.Contains(h);
                    var found = Chain(type).SelectMany(t => t.GetFields(AllMembers)).Any(f => f.Name == name
                        && FieldTypeName(f) == Norm(ft)
                        && (wantStatic is null || f.IsStatic == wantStatic)
                        && Accessible(f.IsPublic, f.IsFamily, f.IsFamilyOrAssembly, f.DeclaringType!));
                    if (!found) problems.Add($"{typeName}.{name} field of type {Clean(ft)} missing, changed or not accessible");
                }
                else
                {
                    var sig = mr.DecodeMethodSignature(provider, null);
                    if (!HasMethod(type, name, sig, Accessible)) problems.Add($"{typeName}.{name}{Format(sig)} missing, changed or not accessible");
                }
            }
            catch (Exception ex) { problems.Add($"{typeName}.{name} cannot be resolved: {ex.Message}"); }
        }

        // 4. Declared reflection contracts (non-public members allowed).
        foreach (var c in contracts)
        {
            count++;
            var what = $"{c.TypeFullName}.{c.Member}";
            var a = Load(c.Assembly, what);
            if (a is null) continue;
            try
            {
                var type = a.GetType(c.TypeFullName);
                if (type is null) { problems.Add($"contract type {c.TypeFullName} missing in {c.Assembly}"); continue; }
                switch (c.Kind)
                {
                    case MemberKind.Type: break;
                    case MemberKind.Field:
                        if (!Chain(type).SelectMany(t => t.GetFields(AllMembers)).Any(f => f.Name == c.Member
                                && (c.MemberTypeFullName is null || TypeName(f.FieldType, false) == c.MemberTypeFullName)))
                            problems.Add($"contract field {what} of type {c.MemberTypeFullName} missing or changed");
                        break;
                    case MemberKind.Property:
                        if (!Chain(type).SelectMany(t => t.GetProperties(AllMembers)).Any(p => p.Name == c.Member
                                && (c.MemberTypeFullName is null || TypeName(p.PropertyType, false) == c.MemberTypeFullName)))
                            problems.Add($"contract property {what} of type {c.MemberTypeFullName} missing or changed");
                        break;
                    case MemberKind.Method:
                        if (!Chain(type).SelectMany(t => t.GetMethods(AllMembers)).Any(m => m.Name == c.Member
                                && (c.MemberTypeFullName is null || TypeName(m.ReturnType, false) == c.MemberTypeFullName)))
                            problems.Add($"contract method {what} returning {c.MemberTypeFullName} missing or changed");
                        break;
                }
            }
            catch (Exception ex) { problems.Add($"contract {what} cannot be resolved: {ex.Message}"); }
        }

        // 5. A passed host assembly the plugin references but nothing was checked into.
        var referenced = md.AssemblyReferences.Select(r => md.GetString(md.GetAssemblyReference(r).Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in hostSet)
            if (referenced.Contains(name) && perAsm.GetValueOrDefault(name) == 0)
                problems.Add($"no references checked into {name}");
        return count;
    }

    private static void ScanFieldUse(byte[] il, HashSet<MemberReferenceHandle> inst, HashSet<MemberReferenceHandle> stat)
    {
        var i = 0;
        while (i < il.Length)
        {
            ushort v = il[i++];
            if (v == 0xFE) v = (ushort)(0xFE00 | il[i++]);
            if (!OpCodeTable.TryGetValue(v, out var op)) return;
            var size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
            if (op.OperandType == OperandType.InlineField)
            {
                var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i));
                if (handle.Kind == HandleKind.MemberReference)
                {
                    var mrh = (MemberReferenceHandle)handle;
                    if (v is 0x7B or 0x7C or 0x7D) inst.Add(mrh); else stat.Add(mrh);
                }
            }
            i += size;
        }
    }

    // Returns the TypeRef of a TypeRef parent or of the generic type definition inside a generic-instantiation TypeSpec.
    private static TypeReferenceHandle? ParentTypeReference(MetadataReader md, EntityHandle parent)
    {
        if (parent.IsNil) return null;
        if (parent.Kind == HandleKind.TypeReference) return (TypeReferenceHandle)parent;
        if (parent.Kind != HandleKind.TypeSpecification) return null;
        var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        if (blob.ReadByte() != 0x15) return null;
        var kind = blob.ReadByte();
        if (kind != 0x11 && kind != 0x12) return null;
        var def = blob.ReadTypeHandle();
        return def.Kind == HandleKind.TypeReference ? (TypeReferenceHandle)def : null;
    }

    // Classes: the base-class chain only. Interfaces: the interface and the interfaces it extends.
    private static IEnumerable<Type> Chain(Type type)
    {
        if (type.IsInterface)
        {
            yield return type;
            foreach (var i in type.GetInterfaces()) yield return i;
            yield break;
        }
        for (Type? t = type; t is not null; t = t.BaseType) yield return t;
    }

    private static IEnumerable<Type> ClassChain(Type type)
    {
        for (Type? t = type; t is not null; t = t.BaseType) yield return t;
    }

    private static bool HasMethod(Type type, string name, MethodSignature<string> sig,
        Func<bool, bool, bool, Type, bool> accessible)
    {
        var want = MethodKey(name, sig);
        var scope = name == ".ctor" ? [type] : Chain(type);
        foreach (var t in scope)
        {
            IEnumerable<MethodBase> candidates = name == ".ctor"
                ? t.GetConstructors(AllMembers).Where(c => !c.IsStatic)
                : t.GetMethods(AllMembers).Where(m => m.Name == name);
            foreach (var m in candidates)
            {
                if (m.IsStatic == sig.Header.IsInstance) continue;
                if (!accessible(m.IsPublic, m.IsFamily, m.IsFamilyOrAssembly, m.DeclaringType!)) continue;
                if (HostMethodKey(m, default) == want) return true;
            }
        }
        return false;
    }

    // name|return|generic-parameter-count|parameters, each type with class/valuetype marker and required modifiers.
    private static string MethodKey(string name, MethodSignature<string> sig) =>
        name + "|" + Norm(sig.ReturnType) + "|" + sig.GenericParameterCount + "|" + string.Join(",", sig.ParameterTypes.Select(Norm));

    private static string HostMethodKey(MethodBase m, ImmutableArray<string> typeArgs)
    {
        var ret = m is MethodInfo mi ? TypeName(mi.ReturnType, true) + Mods(mi.ReturnParameter.GetRequiredCustomModifiers()) : "System.Void^";
        var ps = m.GetParameters().Select(p => Norm(TypeName(p.ParameterType, true) + Mods(p.GetRequiredCustomModifiers())));
        var gen = m.IsGenericMethodDefinition ? m.GetGenericArguments().Length : 0;
        var key = m.Name + "|" + Norm(ret) + "|" + gen + "|" + string.Join(",", ps);
        if (!typeArgs.IsDefaultOrEmpty)
            key = Regex.Replace(key, @"(?<!!)!(\d+)", x => int.Parse(x.Groups[1].Value) < typeArgs.Length ? typeArgs[int.Parse(x.Groups[1].Value)] : x.Value);
        return key;
    }

    private static string FieldTypeName(FieldInfo f) => Norm(TypeName(f.FieldType, true) + Mods(f.GetRequiredCustomModifiers()));

    private static string Mods(Type[] mods) => string.Concat(mods.Select(x => $" modreq({TypeName(x, true)})"));

    private static string Norm(string s)
    {
        var m = ModTail.Match(s);
        if (!m.Success) return s;
        // "T modreq(M)&" (metadata: modifier after BYREF) and "T& modreq(M)" (reflection) become "T& modreq(M)".
        return m.Groups[1].Value + m.Groups[3].Value + string.Concat(ModItem.Matches(m.Groups[2].Value).Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal));
    }

    private static string Clean(string s) => s.Replace("^", "");

    internal static string TypeName(Type t, bool strict)
    {
        if (t.IsGenericParameter) return (t.DeclaringMethod is not null ? "!!" : "!") + t.GenericParameterPosition;
        if (t.IsByRef) return TypeName(t.GetElementType()!, strict) + "&";
        if (t.IsPointer) return TypeName(t.GetElementType()!, strict) + "*";
        if (t.IsArray) return TypeName(t.GetElementType()!, strict) + (t.IsSZArray ? "[]" : "[" + new string(',', t.GetArrayRank() - 1) + "]");
        if (t.IsConstructedGenericType)
            return TypeName(t.GetGenericTypeDefinition(), strict) + "[" + string.Join(",", t.GetGenericArguments().Select(a => TypeName(a, strict))) + "]";
        return (t.FullName ?? t.Name) + (strict && t.IsValueType ? "^" : "");
    }

    private static string Format(MethodSignature<string> sig) =>
        Clean("(" + string.Join(", ", sig.ParameterTypes) + ") : " + sig.ReturnType);

    private static string? HostAssemblyOf(MetadataReader md, TypeReference tr)
    {
        while (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            tr = md.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope);
        return tr.ResolutionScope.Kind == HandleKind.AssemblyReference
            ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name)
            : null;
    }

    internal static string FullName(MetadataReader md, TypeReference tr)
    {
        var name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName(md, md.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope)) + "+" + name;
        var ns = md.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    internal static string FullName(MetadataReader md, TypeDefinition td)
    {
        var name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return FullName(md, md.GetTypeDefinition(decl)) + "+" + name;
        var ns = md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    // The running runtime's sibling Microsoft.WindowsDesktop.App: same version, else the highest with the same major.
    private static IEnumerable<string> WindowsDesktopRuntimeDlls()
    {
        try
        {
            var rt = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('\\', '/'));
            var shared = rt.Parent?.Parent;
            if (shared is null) return [];
            var root = Path.Combine(shared.FullName, "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(root)) return [];
            var dir = Path.Combine(root, rt.Name);
            if (!Directory.Exists(dir))
            {
                var major = rt.Name.Split('.')[0] + ".";
                dir = Directory.GetDirectories(root)
                    .Where(d => Path.GetFileName(d).StartsWith(major, StringComparison.Ordinal))
                    .OrderByDescending(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version(0, 0))
                    .FirstOrDefault() ?? "";
            }
            return dir.Length > 0 ? Directory.GetFiles(dir, "*.dll") : [];
        }
        catch { return []; }
    }

    private sealed class SignatureTypeNameProvider : ISignatureTypeProvider<string, object?>
    {
        public ImmutableArray<string> LastArgs { get; set; } = ImmutableArray<string>.Empty;

        // Value types carry a trailing "^" so that class and valuetype never compare equal.
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => "System.Void^",
            PrimitiveTypeCode.Boolean => "System.Boolean^",
            PrimitiveTypeCode.Char => "System.Char^",
            PrimitiveTypeCode.SByte => "System.SByte^",
            PrimitiveTypeCode.Byte => "System.Byte^",
            PrimitiveTypeCode.Int16 => "System.Int16^",
            PrimitiveTypeCode.UInt16 => "System.UInt16^",
            PrimitiveTypeCode.Int32 => "System.Int32^",
            PrimitiveTypeCode.UInt32 => "System.UInt32^",
            PrimitiveTypeCode.Int64 => "System.Int64^",
            PrimitiveTypeCode.UInt64 => "System.UInt64^",
            PrimitiveTypeCode.Single => "System.Single^",
            PrimitiveTypeCode.Double => "System.Double^",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.TypedReference => "System.TypedReference^",
            PrimitiveTypeCode.IntPtr => "System.IntPtr^",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr^",
            PrimitiveTypeCode.Object => "System.Object",
            _ => typeCode.ToString(),
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            FullName(reader, reader.GetTypeDefinition(handle)) + (rawTypeKind == 0x11 ? "^" : "");

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            FullName(reader, reader.GetTypeReference(handle)) + (rawTypeKind == 0x11 ? "^" : "");

        public string GetTypeFromSpecification(MetadataReader reader, object? ctx, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, ctx);

        public string GetSZArrayType(string element) => element + "[]";
        public string GetArrayType(string element, ArrayShape shape) => element + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string element) => element + "&";
        public string GetPointerType(string element) => element + "*";
        public string GetGenericInstantiation(string generic, ImmutableArray<string> args)
        {
            LastArgs = args;
            return generic + "[" + string.Join(",", args) + "]";
        }
        public string GetGenericTypeParameter(object? ctx, int index) => "!" + index;
        public string GetGenericMethodParameter(object? ctx, int index) => "!!" + index;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetModifiedType(string modifier, string unmodified, bool isRequired) =>
            isRequired ? unmodified + " modreq(" + modifier + ")" : unmodified;
        public string GetPinnedType(string element) => element;
    }
}
