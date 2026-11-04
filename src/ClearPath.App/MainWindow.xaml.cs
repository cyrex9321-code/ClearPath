using System.Windows;
using ClearPath.App.Services;
using ClearPath.App.ViewModels;
using ClearPath.App.Views;
using Wpf.Ui.Controls;

namespace ClearPath.App;

public sealed partial class MainWindow : FluentWindow
{
    private readonly DashboardViewModel _dashboard;
    private readonly NotAvailableViewModel _notAvailable;

    public MainWindow(
        IServiceProvider services,
        DashboardViewModel dashboard,
        NotAvailableViewModel notAvailable,
        ThemeService themeService)
    {
        _dashboard = dashboard;
        _notAvailable = notAvailable;

        InitializeComponent();

        foreach (var module in PlannedModules.All)
        {
            RootNavigation.MenuItems.Add(new NavigationViewItem
            {
                Content = module.Name,
                Icon = new SymbolIcon(module.Icon),
                TargetPageType = typeof(NotAvailablePage),
                Tag = module,
            });
        }

        RootNavigation.SetPageProviderService(new DiNavigationPageProvider(services));
        RootNavigation.ItemInvoked += (_, args) => ShowModule(args.OriginalSource);
        RootNavigation.SelectionChanged += (_, _) => ShowModule(RootNavigation.SelectedItem);

        Loaded += (_, _) =>
        {
            themeService.AttachWindow(this);
            RootNavigation.Navigate(typeof(DashboardPage));
        };

        StateChanged += (_, _) => _dashboard.SetWindowMinimized(WindowState == WindowState.Minimized);
    }

    private void ShowModule(object? item)
    {
        if (item is FrameworkElement { Tag: PlannedModule module })
        {
            _notAvailable.Show(module);
        }
    }
}
