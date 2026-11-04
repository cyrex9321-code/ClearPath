using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using ClearPath.Windows.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPath.Windows.Tests;

public sealed class HandleDirectoryProvisionerTests : IDisposable
{
    private readonly TempSandbox _sandbox = new();
    private readonly HandleDirectoryProvisioner _sut = new(NullLogger<HandleDirectoryProvisioner>.Instance);

    public void Dispose() => _sandbox.Dispose();

    private LocalPath P(params string[] parts) => LocalPath.Parse(_sandbox.PathOf(parts));

    [Fact]
    public void Creates_nested_chain()
    {
        var target = P("a", "b", "c");
        _sut.EnsureDirectory(target);
        Directory.Exists(target.ToString()).Should().BeTrue();
        Directory.Exists(_sandbox.PathOf("a", "b")).Should().BeTrue();
    }

    [Fact]
    public void Second_call_is_idempotent()
    {
        var target = P("a", "b", "c");
        _sut.EnsureDirectory(target);
        File.WriteAllText(Path.Combine(target.ToString(), "keep.txt"), "x");

        var act = () => _sut.EnsureDirectory(target);
        act.Should().NotThrow();
        File.ReadAllText(Path.Combine(target.ToString(), "keep.txt")).Should().Be("x");
    }

    [Fact]
    public void Existing_directory_is_accepted_and_untouched()
    {
        Directory.CreateDirectory(_sandbox.PathOf("exists", "sub"));
        var act = () => _sut.EnsureDirectory(P("exists", "sub"));
        act.Should().NotThrow();
        Directory.EnumerateFileSystemEntries(_sandbox.PathOf("exists", "sub")).Should().BeEmpty();
    }

    [Fact]
    public void Existing_directory_with_different_case_is_accepted()
    {
        Directory.CreateDirectory(_sandbox.PathOf("MixedCase"));
        var act = () => _sut.EnsureDirectory(P("mixedcase", "child"));
        // Resolved path casing differs from the request only in case: comparison is case-insensitive.
        act.Should().NotThrow();
    }

    [Fact]
    public void Drive_root_is_accepted()
    {
        var root = LocalPath.Parse(Path.GetPathRoot(_sandbox.Root)!);
        var act = () => _sut.EnsureDirectory(root);
        act.Should().NotThrow();
    }

    [Fact]
    public void Null_throws()
    {
        var act = () => _sut.EnsureDirectory(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Creates_long_path_chain_over_260_chars()
    {
        var parts = Enumerable.Range(0, 7).Select(i => new string((char)('a' + i), 50)).ToArray();
        var target = P(parts);
        target.ToString().Length.Should().BeGreaterThan(260);

        _sut.EnsureDirectory(target);

        Directory.Exists(target.ToExtendedLengthString()).Should().BeTrue();
        // and idempotent for the long chain too
        var again = () => _sut.EnsureDirectory(target);
        again.Should().NotThrow();
    }

    // ---------- junction rejection ----------

    private string MakeJunction(string linkName, string targetName)
    {
        var target = _sandbox.PathOf(targetName);
        Directory.CreateDirectory(target);
        var link = _sandbox.PathOf(linkName);
        TempSandbox.TryCreateJunction(link, target).Should().BeTrue("mklink /J must work without elevation");
        return target;
    }

    [Fact]
    public void Rejects_intermediate_junction_and_writes_nothing_through_it()
    {
        var target = MakeJunction("link", "real");

        var act = () => _sut.EnsureDirectory(P("link", "x", "y"));

        act.Should().Throw<UnsafeDirectoryException>();
        Directory.EnumerateFileSystemEntries(target).Should().BeEmpty("the junction target must not receive new subdirectories");
    }

    [Fact]
    public void Rejects_intermediate_junction_even_when_target_already_has_the_subdirectory()
    {
        var target = MakeJunction("link", "real");
        Directory.CreateDirectory(Path.Combine(target, "x"));

        var act = () => _sut.EnsureDirectory(P("link", "x"));

        act.Should().Throw<UnsafeDirectoryException>();
        Directory.EnumerateFileSystemEntries(target).Should().Equal(Path.Combine(target, "x"));
    }

    [Fact]
    public void Rejects_final_component_junction()
    {
        var target = MakeJunction("link", "real");

        var act = () => _sut.EnsureDirectory(P("link"));

        act.Should().Throw<UnsafeDirectoryException>();
        Directory.EnumerateFileSystemEntries(target).Should().BeEmpty();
    }

    [Fact]
    public void Rejects_junction_deeper_in_chain()
    {
        Directory.CreateDirectory(_sandbox.PathOf("outer"));
        var target = _sandbox.PathOf("real");
        Directory.CreateDirectory(target);
        TempSandbox.TryCreateJunction(_sandbox.PathOf("outer", "link"), target).Should().BeTrue();

        var act = () => _sut.EnsureDirectory(P("outer", "link", "deep", "er"));

        act.Should().Throw<UnsafeDirectoryException>();
        Directory.EnumerateFileSystemEntries(target).Should().BeEmpty();
    }

    // ---------- non-directory components ----------

    [Fact]
    public void Rejects_intermediate_component_that_is_a_file()
    {
        File.WriteAllText(_sandbox.PathOf("file.txt"), "data");

        var act = () => _sut.EnsureDirectory(P("file.txt", "sub"));

        act.Should().Throw<UnsafeDirectoryException>();
        File.ReadAllText(_sandbox.PathOf("file.txt")).Should().Be("data");
    }

    [Fact]
    public void Rejects_final_component_that_is_a_file()
    {
        File.WriteAllText(_sandbox.PathOf("file.txt"), "data");

        var act = () => _sut.EnsureDirectory(P("file.txt"));

        act.Should().Throw<UnsafeDirectoryException>();
        File.ReadAllText(_sandbox.PathOf("file.txt")).Should().Be("data");
    }

    // ---------- symlinks (need Developer Mode / admin) ----------

    private bool TryCreateDirSymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return Directory.Exists(link);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [Fact]
    public void Rejects_directory_symlink()
    {
        var target = _sandbox.PathOf("real");
        Directory.CreateDirectory(target);
        if (!TryCreateDirSymlink(_sandbox.PathOf("sym"), target))
        {
            Assert.Skip("Directory symlinks cannot be created in this environment (needs Developer Mode or elevation).");
        }

        var intermediate = () => _sut.EnsureDirectory(P("sym", "x"));
        intermediate.Should().Throw<UnsafeDirectoryException>();
        var final = () => _sut.EnsureDirectory(P("sym"));
        final.Should().Throw<UnsafeDirectoryException>();
        Directory.EnumerateFileSystemEntries(target).Should().BeEmpty();
    }
}
