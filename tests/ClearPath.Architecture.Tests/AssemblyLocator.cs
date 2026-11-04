using System.Reflection;

namespace ClearPath.Architecture.Tests;

/// <summary>
/// Locates the built ClearPath assemblies as FILES (never loaded into the test process for inspection),
/// so the rules can inspect assemblies that contain no types yet (Scanning/Cleaning/Backup) and the WPF App.
///
/// How each assembly is found:
///  * Core, Bootstrap, Windows, Infrastructure, Scanning, Cleaning, Backup: normal ProjectReferences, so the build copies
///    ClearPath.&lt;Name&gt;.dll next to the test binary (AppContext.BaseDirectory) even if no code uses them.
///  * App (assembly name "ClearPath"): referenced with ReferenceOutputAssembly=false (build ordering only; avoids pulling WPF
///    into the test host). The csproj bakes the built path into AssemblyMetadata "ClearPath.App.AssemblyPath".
/// </summary>
internal static class AssemblyLocator
{
    public static readonly string[] LibraryNames = ["Core", "Bootstrap", "Windows", "Infrastructure", "Scanning", "Cleaning", "Backup"];

    /// <summary>Assembly simple name used by ClearPath.App (see its csproj AssemblyName).</summary>
    public const string AppAssemblyName = "ClearPath";

    public static string PathOf(string projectName)
    {
        if (projectName == "App")
        {
            var meta = typeof(AssemblyLocator).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "ClearPath.App.AssemblyPath")?.Value;
            if (string.IsNullOrWhiteSpace(meta) || !File.Exists(meta))
            {
                throw new FileNotFoundException($"ClearPath.App output not found (metadata value: '{meta}'). Build the solution first.");
            }
            return meta;
        }

        var path = Path.Combine(AppContext.BaseDirectory, $"ClearPath.{projectName}.dll");
        if (!File.Exists(path)) throw new FileNotFoundException($"ClearPath.{projectName}.dll was not copied to the test output.", path);
        return path;
    }

    /// <summary>Directory of src/ found by walking up from the test binary to the folder containing ClearPath.slnx.</summary>
    public static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClearPath.slnx"))) return Path.Combine(dir.FullName, "src");
        }
        throw new DirectoryNotFoundException("ClearPath.slnx not found above the test output directory.");
    }

    public static IEnumerable<string> AllProjectNames => ["App", .. LibraryNames];
}
