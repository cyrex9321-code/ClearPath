using System.Windows.Controls;
using ClearPath.App.ViewModels;

namespace ClearPath.App.Views;

public sealed partial class QuickCleanPage : Page
{
    public QuickCleanPage(QuickCleanViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
