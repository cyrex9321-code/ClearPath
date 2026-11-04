using System.Runtime.InteropServices;

namespace ClearPath.Architecture.Tests;

/// <summary>
/// Source-operation safety rule: only ClearPath.Infrastructure (settings, database, logs: files it owns) may mutate the filesystem
/// through the BCL or the named Win32 file APIs. Core, Scanning, Cleaning, Backup, App and Windows must not, so that no
/// source-file mutation can bypass the handle-based, PathGuard-checked operations designed in M2/M3.
/// </summary>
public class ForbiddenCallTests
{
    // Infrastructure is deliberately absent: it owns its settings/db/log files. Bootstrap is the composition root and also
    // provisions nothing itself (it delegates to IDirectoryProvisioner), so it is covered as well.
    public static TheoryData<string> CheckedProjects() => new("Core", "Scanning", "Cleaning", "Backup", "App", "Windows", "Bootstrap");

    [Theory]
    [MemberData(nameof(CheckedProjects))]
    public void Assembly_contains_no_filesystem_mutation_apis(string project)
    {
        var violations = ForbiddenCallScanner.Scan(AssemblyLocator.PathOf(project));
        violations.Should().BeEmpty("ClearPath.{0} must not delete/move/copy/create files or directories directly: {1}", project, string.Join("; ", violations));
    }

    [Fact]
    public void Infrastructure_is_the_only_exempt_assembly_and_does_use_file_mutations()
    {
        // Documents (and pins) the exemption: if Infrastructure stopped needing it the exemption would be dead weight.
        var violations = ForbiddenCallScanner.Scan(AssemblyLocator.PathOf("Infrastructure"));
        violations.Should().NotBeEmpty("JsonSettingsStore writes/moves its own settings file; the scanner must see that");
        violations.Select(v => v.Api).Should().Contain(a => a.StartsWith("System.IO.File::", StringComparison.Ordinal));
    }

    // ---------- scanner self-tests: prove the scanner actually detects each category ----------

    [Fact]
    public void Scanner_detects_every_category_in_the_canary_assembly()
    {
        var testAssembly = typeof(ForbiddenCallTests).Assembly.Location;
        var apis = ForbiddenCallScanner.Scan(testAssembly).Select(v => v.Api).ToList();

        apis.Should().Contain("System.IO.File::Delete");
        apis.Should().Contain("System.IO.File::Move");
        apis.Should().Contain("System.IO.File::WriteAllText");
        apis.Should().Contain("System.IO.File::AppendAllText");
        apis.Should().Contain("System.IO.File::Create");
        apis.Should().Contain("System.IO.File::Open");
        apis.Should().Contain("System.IO.File::Copy");
        apis.Should().Contain("System.IO.File::Replace");
        apis.Should().Contain("System.IO.Directory::Delete");
        apis.Should().Contain("System.IO.Directory::Move");
        apis.Should().Contain("System.IO.Directory::CreateDirectory");
        // FileInfo.Delete()/DirectoryInfo.Delete() compile to a reference on the declaring base type FileSystemInfo.
        apis.Should().Contain("System.IO.FileSystemInfo::Delete");
        apis.Should().Contain("System.IO.FileInfo::MoveTo");
        apis.Should().Contain("System.IO.FileInfo::CopyTo");
        apis.Should().Contain("System.IO.DirectoryInfo::Create");
        apis.Should().Contain(a => a.StartsWith("kernel32.dll!MoveFileExW", StringComparison.Ordinal));
        apis.Should().Contain(a => a.StartsWith("kernel32.dll!DeleteFileW", StringComparison.Ordinal));
        apis.Should().Contain(a => a.StartsWith("kernel32.dll!RemoveDirectoryW", StringComparison.Ordinal));
        apis.Should().Contain(a => a.StartsWith("kernel32.dll!CopyFileExW", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_does_not_flag_read_only_file_apis()
    {
        var apis = ForbiddenCallScanner.Scan(typeof(ForbiddenCallTests).Assembly.Location).Select(v => v.Api).ToList();
        // The canary class also calls these read-only APIs; they must stay unflagged.
        apis.Should().NotContain("System.IO.File::Exists");
        apis.Should().NotContain("System.IO.File::ReadAllText");
        apis.Should().NotContain("System.IO.Directory::EnumerateFiles");
        apis.Should().NotContain("System.IO.FileInfo::get_Length");
    }

    [Fact]
    public void Exemptions_remove_matching_violations_only()
    {
        var dll = typeof(ForbiddenCallTests).Assembly.Location;
        var name = typeof(ForbiddenCallTests).Assembly.GetName().Name!;
        var before = ForbiddenCallScanner.Scan(dll);
        var after = ForbiddenCallScanner.Scan(dll, [new ForbiddenCallScanner.Exemption(name, "System.IO.File::Delete", "self-test")]);

        before.Should().Contain(v => v.Api == "System.IO.File::Delete");
        after.Should().NotContain(v => v.Api == "System.IO.File::Delete");
        after.Should().HaveCount(before.Count - 1);
    }
}

/// <summary>
/// Intentionally violates the rules so the scanner has something to find. NEVER CALLED: it only has to exist in this
/// test assembly's metadata. The scanner reads metadata tables, so unreachable code still shows up.
/// </summary>
internal static class CanaryForbiddenCalls
{
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existing, string? target, int flags);

    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode)]
    private static extern bool Delete(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool RemoveDirectoryW(string path);

    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode)]
    private static extern bool Copy(string existing, string target, IntPtr progress, IntPtr data, IntPtr cancel, int flags);

    internal static void NeverCalled(string p)
    {
        File.Delete(p);
        File.Move(p, p + "1");
        File.Copy(p, p + "2");
        File.Replace(p, p + "3", null);
        File.WriteAllText(p, "x");
        File.AppendAllText(p, "x");
        File.Create(p).Dispose();
        File.Open(p, FileMode.Open).Dispose();
        Directory.Delete(p);
        Directory.Move(p, p + "1");
        Directory.CreateDirectory(p);
        new FileInfo(p).Delete();
        new FileInfo(p).MoveTo(p + "1");
        new FileInfo(p).CopyTo(p + "2");
        new DirectoryInfo(p).Delete();
        new DirectoryInfo(p).Create();
        MoveFileEx(p, null, 0);
        Delete(p);
        RemoveDirectoryW(p);
        Copy(p, p, 0, 0, 0, 0);
    }

    internal static void ReadOnlyCalls(string p)
    {
        _ = File.Exists(p);
        _ = File.ReadAllText(p);
        _ = Directory.EnumerateFiles(p).Count();
        _ = new FileInfo(p).Length;
    }
}
