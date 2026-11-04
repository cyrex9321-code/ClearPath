using NetArchTest.Rules;

namespace ClearPath.Architecture.Tests;

/// <summary>Type-level dependency rules (NetArchTest), complementing the assembly-reference rules.</summary>
public class NetArchTests
{
    private static readonly string[] ImplementationNamespaces =
    [
        "ClearPath.Windows",
        "ClearPath.Infrastructure",
        "ClearPath.Scanning",
        "ClearPath.Cleaning",
        "ClearPath.Backup",
    ];

    [Fact]
    public void App_has_types_to_check()
    {
        // Guard against a vacuous pass if the file could not be read or contained nothing.
        Types.FromFile(AssemblyLocator.PathOf("App")).GetTypes().Should().NotBeEmpty();
    }

    [Fact]
    public void App_types_do_not_depend_on_implementation_namespaces()
    {
        var result = Types.FromFile(AssemblyLocator.PathOf("App"))
            .ShouldNot().HaveDependencyOnAny(ImplementationNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue("offending types: {0}", string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [InlineData("ClearPath.Windows")]
    [InlineData("ClearPath.Infrastructure")]
    [InlineData("ClearPath.Scanning")]
    [InlineData("ClearPath.Cleaning")]
    [InlineData("ClearPath.Backup")]
    public void App_types_do_not_depend_on(string ns)
    {
        var result = Types.FromFile(AssemblyLocator.PathOf("App")).ShouldNot().HaveDependencyOn(ns).GetResult();
        result.IsSuccessful.Should().BeTrue("offending types: {0}", string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Positive_control_bootstrap_does_depend_on_implementations()
    {
        // Proves the rule engine detects dependencies in these assemblies (so the App check above is meaningful).
        var result = Types.FromFile(AssemblyLocator.PathOf("Bootstrap"))
            .Should().HaveDependencyOnAny(ImplementationNamespaces)
            .GetResult();
        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Core_types_do_not_depend_on_any_implementation_namespace_or_ui()
    {
        var result = Types.FromFile(AssemblyLocator.PathOf("Core"))
            .ShouldNot().HaveDependencyOnAny([.. ImplementationNamespaces, "ClearPath.Bootstrap", "ClearPath.App", "System.Windows", "Microsoft.Data.Sqlite", "Serilog"])
            .GetResult();
        result.IsSuccessful.Should().BeTrue("offending types: {0}", string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Implementation_types_do_not_depend_on_the_ui_or_composition_root()
    {
        foreach (var project in new[] { "Windows", "Infrastructure", "Scanning", "Cleaning", "Backup" })
        {
            var result = Types.FromFile(AssemblyLocator.PathOf(project))
                .ShouldNot().HaveDependencyOnAny("ClearPath.App", "ClearPath.Bootstrap", "System.Windows")
                .GetResult();
            result.IsSuccessful.Should().BeTrue($"{project}: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }
}
