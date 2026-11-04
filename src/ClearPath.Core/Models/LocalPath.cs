namespace ClearPath.Core.Models;

/// <summary>
/// A validated, structured absolute local path of the form <c>X:\a\b\c</c>.
/// Construction rejects device paths (<c>\\.\</c>, <c>\\?\</c>, <c>\??\</c>, <c>\Device\</c>), UNC paths,
/// alternate data streams, relative segments (<c>.</c>/<c>..</c>), reserved device names, empty components
/// and components with trailing dots or spaces. Comparison is ordinal case-insensitive (NTFS default).
/// This is the parser for <b>external</b> paths; trusted handle-derived paths use <see cref="TryParseHandlePath"/>.
/// </summary>
public sealed class LocalPath : IEquatable<LocalPath>
{
    private const char Separator = '\\';
    private const string ExtendedPrefix = @"\\?\";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    private static readonly char[] InvalidComponentChars = ['<', '>', ':', '"', '/', Separator, '|', '?', '*'];

    private LocalPath(char drive, IReadOnlyList<string> components)
    {
        Drive = char.ToUpperInvariant(drive);
        Components = components;
    }

    public char Drive { get; }
    public IReadOnlyList<string> Components { get; }

    /// <summary>Drive root, e.g. <c>C:\</c>.</summary>
    public string DriveRoot => $"{Drive}:{Separator}";

    public override string ToString() => Components.Count == 0 ? DriveRoot : DriveRoot + string.Join(Separator, Components);

    /// <summary>Win32 extended-length form (<c>\\?\C:\...</c>) for passing to native APIs.</summary>
    public string ToExtendedLengthString() => ExtendedPrefix + ToString();

    /// <summary>Appends exactly one validated component (no separators, not empty).</summary>
    public LocalPath Append(string component)
    {
        if (string.IsNullOrEmpty(component) || component.Contains(Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException("Append takes exactly one non-empty path component.", nameof(component));
        }

        var combined = Components.Count == 0 ? DriveRoot + component : ToString() + Separator + component;
        return Parse(combined);
    }

    public static LocalPath Parse(string path) =>
        TryParse(path, out var result, out var error) ? result! : throw new ArgumentException(error, nameof(path));

    /// <summary>Parse an external (untrusted) path. Only plain drive-letter absolute paths are accepted.</summary>
    public static bool TryParse(string? path, out LocalPath? result, out string? error)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(path)) { error = "Path is empty."; return false; }
        if (path.Contains('\0', StringComparison.Ordinal)) { error = "Path contains a NUL character."; return false; }
        if (path.Contains('/', StringComparison.Ordinal)) { error = "Forward slashes are not accepted; use canonical backslash paths."; return false; }
        if (path[0] == Separator)
        {
            error = "Device, NT-namespace, UNC and root-relative paths are not accepted.";
            return false;
        }

        return TryParseDriveAbsolute(path, out result, out error);
    }

    /// <summary>
    /// Parse a path returned by a trusted handle API (GetFinalPathNameByHandle with VOLUME_NAME_DOS).
    /// Only the exact <c>\\?\X:\...</c> form is accepted; <c>\\?\UNC\</c> and volume-GUID forms are rejected.
    /// The prefix is not stripped blindly: the remainder must independently pass full validation.
    /// </summary>
    public static bool TryParseHandlePath(string? path, out LocalPath? result, out string? error)
    {
        result = null;
        if (path is null || !path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            error = "Handle path is not in extended-length drive form.";
            return false;
        }

        var rest = path[ExtendedPrefix.Length..];
        if (rest.Length < 3 || !char.IsAsciiLetter(rest[0]) || rest[1] != ':' || rest[2] != Separator)
        {
            error = "Handle path is not a drive-letter path (UNC and volume-GUID forms are rejected).";
            return false;
        }

        return TryParseDriveAbsolute(rest, out result, out error);
    }

    private static bool TryParseDriveAbsolute(string path, out LocalPath? result, out string? error)
    {
        result = null;
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != Separator)
        {
            error = "Path must be fully qualified (drive letter, colon, backslash).";
            return false;
        }

        var body = path[3..];
        // A separator directly after the root ("C:\\") is an empty component, not a trailing separator.
        if (body.StartsWith(Separator)) { error = "Path contains an empty component."; return false; }
        if (body.EndsWith(Separator)) body = body[..^1];

        var components = new List<string>();
        if (body.Length > 0)
        {
            foreach (var c in body.Split(Separator))
            {
                if (c.Length == 0) { error = "Path contains an empty component."; return false; }
                if (c is "." or "..") { error = "Relative components ('.' or '..') are not accepted."; return false; }
                if (c.IndexOfAny(InvalidComponentChars) >= 0) { error = $"Component '{c}' contains an invalid character (':' would denote an alternate data stream)."; return false; }
                if (c.EndsWith('.') || c.EndsWith(' ')) { error = $"Component '{c}' ends with a dot or space."; return false; }
                if (c.Any(char.IsControl)) { error = $"Component '{c}' contains a control character."; return false; }
                var stem = c.Split('.')[0].TrimEnd(' ');
                if (ReservedNames.Contains(stem)) { error = $"Component '{c}' is a reserved device name."; return false; }
                components.Add(c);
            }
        }

        result = new LocalPath(path[0], components);
        error = null;
        return true;
    }

    /// <summary>True when this path is strictly beneath <paramref name="root"/> (component-wise, case-insensitive).</summary>
    public bool IsStrictlyUnder(LocalPath root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (Drive != root.Drive || Components.Count <= root.Components.Count) return false;
        for (var i = 0; i < root.Components.Count; i++)
        {
            if (!string.Equals(Components[i], root.Components[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public bool Equals(LocalPath? other) =>
        other is not null && Drive == other.Drive && Components.Count == other.Components.Count &&
        Components.Zip(other.Components).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

    public override bool Equals(object? obj) => obj is LocalPath p && Equals(p);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Drive);
        foreach (var c in Components) h.Add(c, StringComparer.OrdinalIgnoreCase);
        return h.ToHashCode();
    }
}
