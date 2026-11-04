using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ClearPath.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;

    private readonly ISettingsStore _store;
    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(ISettingsStore store, IAppPaths paths, ILogger<SettingsViewModel> logger)
    {
        _store = store;
        _paths = paths;
        _logger = logger;
        Load();
    }

    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();
    public string DefaultQuarantinePath => _paths.DefaultQuarantineDirectory;
    public string LogDirectory => _paths.LogDirectory;

    public string AppVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
         ?? "unknown").Split('+')[0];

    [ObservableProperty] private ThemePreference _selectedTheme;
    [ObservableProperty] private double? _retentionDays;
    [ObservableProperty] private string _newExclusion = string.Empty;
    public ObservableCollection<string> Exclusions { get; } = [];
    [ObservableProperty] private bool _hasNoExclusions = true;

    // Feedback shown in an InfoBar
    [ObservableProperty] private bool _successOpen;
    [ObservableProperty] private string _feedbackTitle = string.Empty;
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _errorOpen;

    /// <summary>Reloads the editable fields from the persisted settings (called whenever the page is shown).</summary>
    public void Load()
    {
        var s = _store.Current;
        SelectedTheme = s.Theme;
        RetentionDays = s.QuarantineRetentionDays;
        Exclusions.Clear();
        foreach (var e in s.Exclusions) Exclusions.Add(e);
        HasNoExclusions = Exclusions.Count == 0;
        NewExclusion = string.Empty;
    }

    [RelayCommand]
    private void AddExclusion()
    {
        var value = NewExclusion.Trim();
        if (value.Length == 0)
        {
            ShowFeedback(true, "Exclusion not added", "Enter a path or pattern first.");
            return;
        }

        if (Exclusions.Any(e => string.Equals(e, value, StringComparison.OrdinalIgnoreCase)))
        {
            ShowFeedback(true, "Exclusion not added", "That exclusion is already in the list.");
            return;
        }

        Exclusions.Add(value);
        HasNoExclusions = false;
        NewExclusion = string.Empty;
        SuccessOpen = false;
        ErrorOpen = false;
    }

    [RelayCommand]
    private void RemoveExclusion(string? exclusion)
    {
        if (exclusion is null) return;
        Exclusions.Remove(exclusion);
        HasNoExclusions = Exclusions.Count == 0;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (RetentionDays is not { } days || double.IsNaN(days) || days != Math.Floor(days)
            || days < MinRetentionDays || days > MaxRetentionDays)
        {
            ShowFeedback(true, "Settings not saved", $"Retention must be a whole number of days between {MinRetentionDays} and {MaxRetentionDays}.");
            return;
        }

        var updated = _store.Current with
        {
            Theme = SelectedTheme,
            QuarantineRetentionDays = (int)days,
            Exclusions = Exclusions.Select(e => e.Trim()).Where(e => e.Length > 0).ToList(),
        };

        try
        {
            await _store.SaveAsync(updated);
            ShowFeedback(false, "Settings saved", "Your changes were saved.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed");
            ShowFeedback(true, "Settings not saved", "Saving failed: " + ex.GetBaseException().Message);
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Process.Start("explorer.exe", $"\"{_paths.LogDirectory}\"");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening log folder failed");
            ShowFeedback(true, "Could not open the log folder", ex.GetBaseException().Message);
        }
    }

    private void ShowFeedback(bool isError, string title, string message)
    {
        ErrorOpen = isError;
        FeedbackTitle = title;
        FeedbackMessage = message;
        SuccessOpen = !isError;
    }
}
