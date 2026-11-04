using Wpf.Ui.Abstractions;

namespace ClearPath.App.Services;

/// <summary>Resolves navigation pages from the DI container.</summary>
public sealed class DiNavigationPageProvider(IServiceProvider services) : INavigationViewPageProvider
{
    public object? GetPage(Type pageType) => services.GetService(pageType);
}
