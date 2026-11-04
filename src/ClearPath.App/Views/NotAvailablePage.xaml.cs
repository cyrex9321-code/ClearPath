using System.Windows.Controls;
using ClearPath.App.ViewModels;

namespace ClearPath.App.Views;

public sealed partial class NotAvailablePage : Page
{
    public NotAvailablePage(NotAvailableViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
