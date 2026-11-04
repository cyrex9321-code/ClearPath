using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;

namespace ClearPath.Infrastructure;

/// <summary>ClearPath-owned locations under %LOCALAPPDATA%\ClearPath (or a test-supplied base directory).</summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? baseDirectoryOverride = null)
    {
        var baseDirectory = baseDirectoryOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "ClearPath");

        // Validate through the external-path parser so a hostile environment value can't redirect us to a device/UNC path.
        DataDirectory = LocalPath.Parse(Path.GetFullPath(baseDirectory)).ToString();
        LogDirectory = Path.Combine(DataDirectory, "logs");
        DatabasePath = Path.Combine(DataDirectory, "clearpath.db");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        DefaultQuarantineDirectory = Path.Combine(DataDirectory, "Quarantine");
    }

    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string DatabasePath { get; }
    public string SettingsPath { get; }
    public string DefaultQuarantineDirectory { get; }
}
