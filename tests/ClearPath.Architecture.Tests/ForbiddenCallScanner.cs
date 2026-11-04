using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace ClearPath.Architecture.Tests;

/// <summary>One forbidden-API reference found in an assembly.</summary>
internal sealed record Violation(string Assembly, string Kind, string Api)
{
    public override string ToString() => $"{Assembly}: {Kind} {Api}";
}

/// <summary>
/// IL-level scan for filesystem-mutating API usage, using only System.Reflection.Metadata (no extra packages).
///
/// What it inspects (metadata tables, not method bodies, so it is fast and cannot be fooled by dead-code elimination):
///  * MemberRef table: every external method/constructor the assembly references, as (declaring type, member name).
///    A call to File.Delete compiles to a MemberRef whose parent is TypeRef System.IO.File and name "Delete".
///  * MethodDef table: every P/Invoke declared in the assembly (MethodImport row; covers DllImport and the code that
///    [LibraryImport] generates), matched by managed name and by native entry-point name.
///
/// Limits (documented, not hidden):
///  * Generic-instantiation parents (TypeSpec) are not decoded. The BCL APIs below are non-generic types.
///  * Reflection/dynamic invocation, function pointers from NativeLibrary.GetExport and calls made through types in other
///    non-BCL libraries are not seen. The reference-rule tests keep other libraries out of the layers that matter.
///  * Names are matched, not semantics: a handle-based delete (e.g. NtSetInformationFile with disposition info) is not flagged;
///    add a rule to <see cref="NativeRules"/> when M3 introduces such P/Invokes and decide which assembly owns them.
///
/// EXTENDING IN M3: add types/members to <see cref="ManagedRules"/>, entry-point patterns to <see cref="NativeRules"/>, or
/// add an <see cref="Exemption"/> (assembly + API text) with a justification when an assembly legitimately owns a mutation.
/// </summary>
internal static class ForbiddenCallScanner
{
    /// <summary>(namespace.type) => predicate on member name. Anything matched is a violation.</summary>
    private static readonly (string Type, Func<string, bool> Member)[] ManagedRules =
    [
        ("System.IO.File", n => Is(n, "Delete", "Move", "Replace", "Copy", "Create", "CreateText", "Open", "OpenWrite", "OpenHandle",
                                    "AppendText", "Encrypt", "Decrypt", "SetAttributes", "CreateSymbolicLink")
                                || n.StartsWith("WriteAll", StringComparison.Ordinal)
                                || n.StartsWith("AppendAll", StringComparison.Ordinal)),
        ("System.IO.Directory", n => Is(n, "Delete", "Move", "CreateDirectory", "CreateSymbolicLink", "CreateTempSubdirectory")),
        ("System.IO.FileInfo", n => Is(n, "Delete", "MoveTo", "CopyTo", "Create", "CreateText", "AppendText", "Open", "OpenWrite", "Replace",
                                        "Encrypt", "Decrypt", "CreateAsSymbolicLink")),
        ("System.IO.DirectoryInfo", n => Is(n, "Delete", "MoveTo", "Create", "CreateSubdirectory", "CreateAsSymbolicLink")),
        ("System.IO.FileSystemInfo", n => Is(n, "Delete", "CreateAsSymbolicLink")),
    ];

    /// <summary>Regexes for P/Invoke names (managed method name OR native entry point). Trailing A/W/Ex/Transacted variants are covered by the prefix.</summary>
    private static readonly Regex[] NativeRules =
    [
        new(@"^(MoveFile|DeleteFile|RemoveDirectory|CopyFile|ReplaceFile)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    /// <summary>An API reference that is explicitly allowed in one assembly. Empty today; every entry needs a written reason.</summary>
    internal sealed record Exemption(string Assembly, string ApiContains, string Reason);

    private static bool Is(string name, params string[] names) => names.Contains(name, StringComparer.Ordinal);

    public static IReadOnlyList<Violation> Scan(string dllPath, IReadOnlyCollection<Exemption>? exemptions = null)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var assemblyName = md.GetString(md.GetAssemblyDefinition().Name);
        var found = new List<Violation>();

        // 1. External member references.
        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;

            var type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            var fullName = $"{md.GetString(type.Namespace)}.{md.GetString(type.Name)}";
            var memberName = md.GetString(member.Name);

            foreach (var (ruleType, matches) in ManagedRules)
            {
                if (fullName == ruleType && matches(memberName))
                {
                    found.Add(new Violation(assemblyName, "managed call", $"{fullName}::{memberName}"));
                }
            }
        }

        // 2. P/Invoke declarations.
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;

            var managedName = md.GetString(method.Name);
            var import = method.GetImport();
            var entryPoint = import.Name.IsNil ? managedName : md.GetString(import.Name);
            var module = import.Module.IsNil ? "?" : md.GetString(md.GetModuleReference(import.Module).Name);

            if (NativeRules.Any(r => r.IsMatch(managedName) || r.IsMatch(entryPoint)))
            {
                found.Add(new Violation(assemblyName, "P/Invoke", $"{module}!{entryPoint} (managed name {managedName})"));
            }
        }

        if (exemptions is { Count: > 0 })
        {
            found.RemoveAll(v => exemptions.Any(e => e.Assembly == v.Assembly && v.Api.Contains(e.ApiContains, StringComparison.Ordinal)));
        }

        return found.Distinct().ToList();
    }
}
