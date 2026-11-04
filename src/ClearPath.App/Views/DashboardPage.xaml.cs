using System.Windows.Controls;
using ClearPath.App.ViewModels;

namespace ClearPath.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        Loaded += (_, _) => viewModel.SetPageVisible(true);
        Unloaded += (_, _) => viewModel.SetPageVisible(false);
    }
}
