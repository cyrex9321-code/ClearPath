using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace ClearPath.Architecture.Tests;

/// <summary>
/// Layering rules, checked two ways so an empty assembly cannot hide a violation:
///  1. IL level: AssemblyRef rows in the built DLL (what the compiled code actually depends on).
///  2. Project level: ProjectReference items in each csproj (what the build allows).
/// </summary>
public class ReferenceRulesTests
{
    /// <summary>project name -> ClearPath projects it may reference. Bootstrap is the composition root and may reference all implementations.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Core"] = [],
        ["App"] = ["Core", "Bootstrap"],
        ["Bootstrap"] = ["Core", "Windows", "Infrastructure", "Scanning", "Cleaning", "Backup"],
        ["Windows"] = ["Core"],
        ["Infrastructure"] = ["Core"],
        ["Scanning"] = ["Core"],
        ["Cleaning"] = ["Core"],
        ["Backup"] = ["Core"],
    };

    public static TheoryData<string> Projects() => new(Allowed.Keys);

    private static bool IsClearPath(string assemblyName) => assemblyName == AssemblyLocator.AppAssemblyName || assemblyName.StartsWith("ClearPath.", StringComparison.Ordinal);

    /// <summary>Maps an assembly simple name to a project name ("ClearPath" => App, "ClearPath.Core" => Core).</summary>
    private static string ToProject(string assemblyName) => assemblyName == AssemblyLocator.AppAssemblyName ? "App" : assemblyName["ClearPath.".Length..];

    internal static IReadOnlyList<string> ReferencedClearPathProjects(string dllPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.AssemblyReferences
            .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
            .Where(IsClearPath)
            .Select(ToProject)
            .Distinct()
            .Order()
            .ToList();
    }

    private static string AssemblyNameOf(string dllPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.GetString(md.GetAssemblyDefinition().Name);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Built_assembly_has_expected_name(string project)
    {
        var expected = project == "App" ? "ClearPath" : $"ClearPath.{project}";
        AssemblyNameOf(AssemblyLocator.PathOf(project)).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Assembly_references_only_allowed_clearpath_assemblies(string project)
    {
        var referenced = ReferencedClearPathProjects(AssemblyLocator.PathOf(project));
        referenced.Should().BeSubsetOf(Allowed[project], $"ClearPath.{project} may only depend on [{string.Join(", ", Allowed[project])}]");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Csproj_references_only_allowed_projects(string project)
    {
        var csproj = Path.Combine(AssemblyLocator.SourceRoot(), $"ClearPath.{project}", $"ClearPath.{project}.csproj");
        File.Exists(csproj).Should().BeTrue();

        var referenced = XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar)))
            .Select(name => name == "ClearPath.App" ? "App" : name["ClearPath.".Length..])
            .ToList();

        referenced.Should().BeSubsetOf(Allowed[project]);
    }

    [Fact]
    public void App_references_exactly_core_and_bootstrap_in_its_project_file()
    {
        // App is the UI shell: it must be wired to the composition root and the contracts, nothing else.
        var csproj = Path.Combine(AssemblyLocator.SourceRoot(), "ClearPath.App", "ClearPath.App.csproj");
        XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')))
            .Should().BeEquivalentTo("ClearPath.Core", "ClearPath.Bootstrap");
    }

    [Fact]
    public void Nothing_references_the_app_assembly()
    {
        foreach (var project in Allowed.Keys.Where(p => p != "App"))
        {
            ReferencedClearPathProjects(AssemblyLocator.PathOf(project)).Should().NotContain("App", $"ClearPath.{project} must not depend on the UI");
        }
    }

    [Fact]
    public void Core_references_no_clearpath_assembly()
    {
        ReferencedClearPathProjects(AssemblyLocator.PathOf("Core")).Should().BeEmpty();
    }

    [Fact]
    public void Implementation_projects_never_reference_each_other()
    {
        string[] implementations = ["Windows", "Infrastructure", "Scanning", "Cleaning", "Backup"];
        foreach (var project in implementations)
        {
            ReferencedClearPathProjects(AssemblyLocator.PathOf(project)).Should().BeSubsetOf(["Core"], project);
        }
    }

    [Fact]
    public void Every_project_in_the_rule_table_has_a_built_assembly()
    {
        // Guards against the rules silently passing on a missing assembly.
        foreach (var name in AssemblyLocator.AllProjectNames) File.Exists(AssemblyLocator.PathOf(name)).Should().BeTrue(name);
        Allowed.Keys.Should().BeEquivalentTo(AssemblyLocator.AllProjectNames);
    }
}
