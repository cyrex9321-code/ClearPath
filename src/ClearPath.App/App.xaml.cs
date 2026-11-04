using System.Windows;
using System.Windows.Threading;
using ClearPath.App.Services;
using ClearPath.App.ViewModels;
using ClearPath.App.Views;
using ClearPath.Bootstrap;
using ClearPath.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClearPath.App;

public partial class App : Application
{
    private const string MutexName = @"Local\ClearPath.SingleInstance";
    private const string AppTitle = "ClearPath";

    private Mutex? _mutex;
    private IHost? _host;
    private ILogger<App>? _logger;
    private bool _errorDialogOpen;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            MessageBox.Show(
                "ClearPath is already running. Switch to the existing window instead of starting a second copy.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.AddClearPath();
            RegisterUi(builder.Services);
            _host = builder.Build();

            await _host.StartAsync();
            _logger = _host.Services.GetRequiredService<ILogger<App>>();

            await _host.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

            _host.Services.GetRequiredService<ThemeService>().ApplyInitial();

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            _logger.LogInformation("ClearPath UI started");
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Host shutdown failed");
        }

        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned by this thread; process exit releases it */ }
            _mutex.Dispose();
        }

        base.OnExit(e);
    }

    private static void RegisterUi(IServiceCollection services)
    {
        services.AddSingleton<ThemeService>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<QuickCleanViewModel>();
        services.AddSingleton<SafetyCenterViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<NotAvailableViewModel>();

        services.AddSingleton<DashboardPage>();
        services.AddSingleton<QuickCleanPage>();
        services.AddSingleton<SafetyCenterPage>();
        services.AddSingleton<HistoryPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<NotAvailablePage>();

        services.AddSingleton<MainWindow>();
    }

    private void ReportStartupFailure(Exception ex)
    {
        _logger?.LogCritical(ex, "ClearPath failed to start");

        var unsafeDirectory = FindUnsafeDirectory(ex);
        var message = unsafeDirectory is not null
            ? "ClearPath did not start because its data folder is not safe to use:\n\n"
              + unsafeDirectory.Message
              + "\n\nNothing was changed. Remove any junction or symbolic link in the ClearPath data location (under %LOCALAPPDATA%) and start ClearPath again."
            : "ClearPath could not start:\n\n" + ex.Message + "\n\nSee the log folder under %LOCALAPPDATA%\\ClearPath\\logs for details.";

        MessageBox.Show(message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static UnsafeDirectoryException? FindUnsafeDirectory(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is UnsafeDirectoryException found) return found;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var nested = FindUnsafeDirectory(inner);
                    if (nested is not null) return nested;
                }
            }
        }
        return null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        ShowErrorDialog(e.Exception);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
        Dispatcher.BeginInvoke(() => ShowErrorDialog(e.Exception));
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _logger?.LogCritical(ex, "Fatal unhandled exception (terminating: {Terminating})", e.IsTerminating);
        }
    }

    private void ShowErrorDialog(Exception ex)
    {
        if (_errorDialogOpen) return;
        _errorDialogOpen = true;
        try
        {
            MessageBox.Show(
                "Something went wrong:\n\n" + ex.GetBaseException().Message + "\n\nThe details were written to the ClearPath log.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _errorDialogOpen = false;
        }
    }
}
