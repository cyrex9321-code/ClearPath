using System.Windows.Controls;
using ClearPath.App.ViewModels;

namespace ClearPath.App.Views;

public sealed partial class SafetyCenterPage : Page
{
    public SafetyCenterPage(SafetyCenterViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }
}
