using System.Windows.Controls;
using ClearPath.App.ViewModels;

namespace ClearPath.App.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }
}
