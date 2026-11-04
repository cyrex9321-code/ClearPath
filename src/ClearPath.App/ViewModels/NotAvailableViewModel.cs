using CommunityToolkit.Mvvm.ComponentModel;

namespace ClearPath.App.ViewModels;

/// <summary>Backs the single shared "Not available in this version" page.</summary>
public sealed partial class NotAvailableViewModel : ObservableObject
{
    [ObservableProperty]
    private string _moduleName = "This module";

    [ObservableProperty]
    private string _description = "This module is planned for a later phase.";

    public void Show(PlannedModule module)
    {
        ModuleName = module.Name;
        Description = module.Description;
    }
}
