using System.Windows;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ClearPath.App.Services;

/// <summary>Applies System/Light/Dark from settings and follows later settings changes.</summary>
public sealed class ThemeService(ISettingsStore settings, ILogger<ThemeService> logger)
{
    private Window? _window;
    private bool _subscribed;

    public static bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    /// <summary>Applies the persisted preference before any window is shown.</summary>
    public void ApplyInitial()
    {
        ApplyTheme(settings.Current.Theme);
        if (_subscribed) return;
        _subscribed = true;
        settings.Changed += (_, changed) =>
            Application.Current.Dispatcher.BeginInvoke(() => Apply(changed.Theme));
    }

    /// <summary>Called once the main window is loaded so the system-theme watcher can attach to it.</summary>
    public void AttachWindow(Window window)
    {
        _window = window;
        Apply(settings.Current.Theme);
    }

    private void Apply(ThemePreference preference)
    {
        try
        {
            if (_window is not null)
            {
                SystemThemeWatcher.UnWatch(_window);
                if (preference == ThemePreference.System)
                {
                    SystemThemeWatcher.Watch(_window, WindowBackdropType.Mica, true);
                }
            }

            ApplyTheme(preference);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Applying theme {Theme} failed", preference);
        }
    }

    private static void ApplyTheme(ThemePreference preference)
    {
        var theme = preference switch
        {
            ThemePreference.Light => ApplicationTheme.Light,
            ThemePreference.Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.CapturedMotion or SystemTheme.Glow
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light,
        };
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);
    }
}
