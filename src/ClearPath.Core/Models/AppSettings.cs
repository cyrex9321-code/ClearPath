namespace ClearPath.Core.Models;

public enum ThemePreference { System, Light, Dark }

/// <summary>User settings persisted as JSON. Phase 1–2 subset only.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>User-defined exclusions: absolute paths or simple wildcard patterns. Never widen what can be cleaned.</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

    /// <summary>Days after which quarantined items are flagged as expired. Expiry never purges automatically.</summary>
    public int QuarantineRetentionDays { get; init; } = 30;

    /// <summary>Null means the default location (%LOCALAPPDATA%\ClearPath\Quarantine).</summary>
    public string? QuarantineLocation { get; init; }
}
