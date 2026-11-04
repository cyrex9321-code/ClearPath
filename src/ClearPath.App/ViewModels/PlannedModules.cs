using Wpf.Ui.Controls;

namespace ClearPath.App.ViewModels;

/// <summary>A navigation entry for a module that is planned for a later phase.</summary>
public sealed record PlannedModule(string Name, string Description, SymbolRegular Icon);

public static class PlannedModules
{
    public static IReadOnlyList<PlannedModule> All { get; } =
    [
        new("Deep Clean", "Deep Clean is a planned module for finding and reviewing larger or less common cleanup candidates beyond the Quick Clean categories.", SymbolRegular.Broom24),
        new("Registry", "Registry is a planned module for reviewing registry entries that point to items that no longer exist.", SymbolRegular.Library24),
        new("Browser Privacy", "Browser Privacy is a planned module for reviewing browser caches and browsing traces.", SymbolRegular.Eye24),
        new("Startup Manager", "Startup Manager is a planned module for reviewing which programs start with Windows.", SymbolRegular.Rocket24),
        new("Disk Analyzer", "Disk Analyzer is a planned module for showing what is using space on your drives.", SymbolRegular.HardDrive24),
        new("Duplicate Finder", "Duplicate Finder is a planned module for locating duplicate files for review.", SymbolRegular.Copy24),
        new("Software Uninstaller", "Software Uninstaller is a planned module for listing and removing installed programs.", SymbolRegular.Apps24),
        new("Privacy Protection", "Privacy Protection is a planned module for reviewing Windows privacy-related settings.", SymbolRegular.ShieldLock24),
        new("AI Advisor", "AI Advisor is a planned module for explaining findings and suggesting next steps.", SymbolRegular.Sparkle24),
    ];
}
