using System.Text.Json;
using System.Text.Json.Serialization;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClearPath.Infrastructure;

/// <summary>
/// Settings persisted as JSON in ClearPath's own data directory. A corrupt or unreadable file falls back to
/// defaults (logged) and is never deleted; it is preserved as settings.json.corrupt-&lt;timestamp&gt; on the next save.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _loadedFromCorruptFile;

    public JsonSettingsStore(IAppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _paths = paths;
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_paths.SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(_paths.SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            return Sanitize(settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Settings file {Path} could not be read; using defaults", _paths.SettingsPath);
            _loadedFromCorruptFile = File.Exists(_paths.SettingsPath);
            return new AppSettings();
        }
    }

    internal static AppSettings Sanitize(AppSettings settings) => settings with
    {
        QuarantineRetentionDays = Math.Clamp(settings.QuarantineRetentionDays, 1, 3650),
        Exclusions = settings.Exclusions?.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
    };

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Sanitize(settings);

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loadedFromCorruptFile && File.Exists(_paths.SettingsPath))
            {
                // Keep the unreadable file for diagnosis instead of overwriting it.
                var preserved = _paths.SettingsPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(_paths.SettingsPath, preserved, overwrite: false);
                _loadedFromCorruptFile = false;
            }

            // Write to a temp file and atomically replace. Only ClearPath's own settings file is touched here.
            var temp = _paths.SettingsPath + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(_paths.SettingsPath)) File.Replace(temp, _paths.SettingsPath, destinationBackupFileName: null);
            else File.Move(temp, _paths.SettingsPath, overwrite: false);

            Current = settings;
        }
        finally
        {
            _saveGate.Release();
        }

        Changed?.Invoke(this, settings);
    }
}
