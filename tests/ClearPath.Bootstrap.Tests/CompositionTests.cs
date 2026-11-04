using ClearPath.Bootstrap;
using ClearPath.Core.Abstractions;
using ClearPath.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ClearPath.Bootstrap.Tests;

public sealed class CompositionTests : IDisposable
{
    private readonly TempSandbox _sandbox = new();
    private readonly ServiceProvider _provider;

    public CompositionTests()
    {
        var services = new ServiceCollection().AddLogging().AddClearPathCore(new AppPaths(_sandbox.Root));
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public void Dispose()
    {
        _provider.Dispose();
        _sandbox.Dispose();
    }

    public static readonly Type[] AllInterfaces =
    [
        typeof(ISystemMetrics),
        typeof(IDriveInfoProvider),
        typeof(IOsInfoProvider),
        typeof(IRecycleBinInfo),
        typeof(IPrivilegeInfo),
        typeof(IDirectoryProvisioner),
        typeof(ISettingsStore),
        typeof(IDatabaseInitializer),
        typeof(IHistoryRepository),
        typeof(IAppPaths),
    ];

    public static TheoryData<Type> CoreInterfaces() => new(AllInterfaces);

    [Theory]
    [MemberData(nameof(CoreInterfaces))]
    public void Every_core_interface_resolves(Type serviceType)
    {
        _provider.GetRequiredService(serviceType).Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(CoreInterfaces))]
    public void Every_core_interface_is_a_singleton(Type serviceType)
    {
        var first = _provider.GetRequiredService(serviceType);
        var second = _provider.GetRequiredService(serviceType);
        second.Should().BeSameAs(first);

        // Same instance from a created scope too (a scoped/transient registration would differ).
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService(serviceType).Should().BeSameAs(first);
    }

    [Fact]
    public void Registered_descriptors_are_all_singletons()
    {
        var services = new ServiceCollection().AddLogging().AddClearPathCore(new AppPaths(_sandbox.Root));
        foreach (var type in AllInterfaces)
        {
            services.Where(d => d.ServiceType == type).Should().ContainSingle($"{type.Name} must be registered exactly once");
            services.Single(d => d.ServiceType == type).Lifetime.Should().Be(ServiceLifetime.Singleton, type.Name);
        }
    }

    [Fact]
    public void AppPaths_registration_is_the_instance_supplied()
    {
        var paths = new AppPaths(_sandbox.Root);
        using var provider = new ServiceCollection().AddLogging().AddClearPathCore(paths)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        provider.GetRequiredService<IAppPaths>().Should().BeSameAs(paths);
    }

    [Fact]
    public void Resolving_services_does_not_create_the_database()
    {
        // Resolving everything must not create the database (initialisation is an explicit step).
        foreach (var d in AllInterfaces) _provider.GetRequiredService(d);
        File.Exists(Path.Combine(_sandbox.Root, "clearpath.db")).Should().BeFalse();
    }

    [Fact]
    public void Add_core_rejects_null_paths()
    {
        var act = () => new ServiceCollection().AddLogging().AddClearPathCore(null!).BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });
        act.Should().Throw<Exception>();
    }
}
