using ClearPath.Core.Cleaning;
using ClearPath.Scanning;

namespace ClearPath.Scanning.Tests;

/// <summary>
/// Static shape checks on the seven allow-listed rule roots (plan 4.1). Nothing else is ever scanned;
/// this is the data the rest of the suite relies on being exactly these roots and no others.
/// </summary>
public class RuleCatalogTests
{
    private readonly RuleCatalog _sut = new();

    [Fact]
    public void Exactly_seven_rules()
    {
        _sut.Rules.Should().HaveCount(7);
    }

    [Fact]
    public void Every_rule_root_is_UserTemp_or_LocalAppData()
    {
        _sut.Rules.Should().OnlyContain(r => r.Root == RuleRoot.UserTemp || r.Root == RuleRoot.LocalAppData);
    }

    [Fact]
    public void User_temp_rule_has_no_subpath_and_a_24_hour_minimum_age()
    {
        var rule = _sut.Rules.Single(r => r.Id == "user-temp");
        rule.Root.Should().Be(RuleRoot.UserTemp);
        rule.RootSubPath.Should().BeEmpty();
        rule.MinimumAge.Should().Be(TimeSpan.FromHours(24));
        rule.RequiresAdmin.Should().BeFalse();
    }

    [Theory]
    [InlineData("d3d-shader-cache", new[] { "D3DSCache" })]
    [InlineData("inet-cache", new[] { "Microsoft", "Windows", "INetCache" })]
    [InlineData("crash-dumps", new[] { "CrashDumps" })]
    [InlineData("thumbnail-cache", new[] { "Microsoft", "Windows", "Explorer" })]
    [InlineData("wer-archive", new[] { "Microsoft", "Windows", "WER", "ReportArchive" })]
    [InlineData("wer-queue", new[] { "Microsoft", "Windows", "WER", "ReportQueue" })]
    public void LocalAppData_rules_have_the_exact_documented_subpath(string ruleId, string[] expectedSubPath)
    {
        var rule = _sut.Rules.Single(r => r.Id == ruleId);
        rule.Root.Should().Be(RuleRoot.LocalAppData);
        rule.RootSubPath.Should().Equal(expectedSubPath);
    }

    [Fact]
    public void No_rule_requires_admin()
    {
        _sut.Rules.Should().OnlyContain(r => !r.RequiresAdmin);
    }

    [Fact]
    public void Rule_ids_are_unique()
    {
        _sut.Rules.Select(r => r.Id).Distinct().Should().HaveCount(_sut.Rules.Count);
    }
}
