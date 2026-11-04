using System.Diagnostics;

namespace ClearPath.TestSupport;

/// <summary>
/// Per-test filesystem sandbox under %TEMP%\ClearPathTests\&lt;guid&gt;. Every test that touches the disk
/// must work only inside <see cref="Root"/>; the directory is removed on dispose.
/// Shared by all test projects via a linked compile item.
/// </summary>
internal sealed class TempSandbox : IDisposable
{
    public TempSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "ClearPathTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Creates a directory junction (no admin needed). Returns false if creation failed.</summary>
    public static bool TryCreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) RemoveTree(new DirectoryInfo(Root));
        }
        catch (IOException) { /* best effort; temp dir */ }
        catch (UnauthorizedAccessException) { }

        // Remove the parent only if it became empty (other sandboxes may be live in parallel).
        try
        {
            var parent = Path.GetDirectoryName(Root)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Deletes a tree without ever following reparse points (junction/symlink are removed as links).</summary>
    private static void RemoveTree(DirectoryInfo dir)
    {
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            dir.Delete(); // removes the link only
            return;
        }

        foreach (var sub in dir.EnumerateDirectories()) RemoveTree(sub);
        foreach (var file in dir.EnumerateFiles())
        {
            file.Attributes = FileAttributes.Normal;
            file.Delete();
        }
        dir.Delete();
    }
}
