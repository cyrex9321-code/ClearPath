using ClearPath.Core.Models;

namespace ClearPath.Core.Tests;

public class LocalPathTests
{
    private static string Long(int components, int width)
        => string.Join('\\', Enumerable.Range(0, components).Select(i => new string((char)('a' + i % 26), width)));

    // ---------- accepted ----------

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\a\b", @"C:\a\b")]
    [InlineData(@"C:\a\b\", @"C:\a\b")]          // trailing backslash normalised
    [InlineData(@"c:\a\b", @"C:\a\b")]           // lowercase drive normalised
    [InlineData(@"d:\", @"D:\")]
    [InlineData(@"C:\Program Files (x86)\Some App", @"C:\Program Files (x86)\Some App")]
    [InlineData(@"C:\a\b.c.d\e-f_g", @"C:\a\b.c.d\e-f_g")]
    [InlineData(@"C:\a\console", @"C:\a\console")]   // not reserved: only exact stems
    public void Accepts_valid_paths_and_normalises(string input, string expected)
    {
        LocalPath.TryParse(input, out var result, out var error).Should().BeTrue(error);
        result!.ToString().Should().Be(expected);
        error.Should().BeNull();
    }

    [Fact]
    public void Root_has_no_components_and_drive_is_upper_case()
    {
        var p = LocalPath.Parse(@"c:\");
        p.Components.Should().BeEmpty();
        p.Drive.Should().Be('C');
        p.DriveRoot.Should().Be(@"C:\");
    }

    [Fact]
    public void Components_are_split()
    {
        LocalPath.Parse(@"C:\a\b\c").Components.Should().Equal("a", "b", "c");
    }

    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    public void Accepts_long_paths(int approxLength)
    {
        var body = Long(approxLength / 50, 49);
        var input = @"C:\" + body;
        input.Length.Should().BeGreaterThan(260);
        LocalPath.TryParse(input, out var result, out var error).Should().BeTrue(error);
        result!.ToString().Should().Be(input);
        result.ToExtendedLengthString().Should().Be(@"\\?\" + input);
    }

    [Fact]
    public void Extended_length_string_round_trips_through_handle_parser()
    {
        var p = LocalPath.Parse(@"C:\a\" + Long(25, 40));
        LocalPath.TryParseHandlePath(p.ToExtendedLengthString(), out var back, out var error).Should().BeTrue(error);
        back.Should().Be(p);
    }

    // ---------- rejected ----------

    public static TheoryData<string> RejectedInputs() => new()
    {
        "", " ", "   ", "\t",
        @"a\b", @"C:a", @"C:", @"1:\a", @"::\a",
        "C:/a/b", @"C:\a/b", "C:\\a/",
        @"C:\..", @"C:\.", @"C:\a\..\b", @"C:\a\.\b", @"C:\a\..", @"C:\..\a", @"C:\a\b\..\..\..",
        @"\\.\C:\x", @"\\?\C:\x", @"\\?\GLOBALROOT\Device\HarddiskVolume1\x", @"\??\C:\x",
        @"\Device\HarddiskVolume1\x", @"\\server\share\x", @"\\?\UNC\server\share", @"\x", @"\",
        @"C:\a\file.txt:stream", @"C:\a\file.txt::$DATA", @"C:\a:b", @"C:\a\b:",
        @"C:\CON", @"C:\a\nul.txt", @"C:\a\COM1", @"C:\a\LPT9.log", @"C:\a\CONIN$", @"C:\a\CONOUT$",
        @"C:\a\con", @"C:\a\Prn.x.y", @"C:\a\AUX", "C:\\a\\COM\u00B2", "C:\\a\\LPT\u00B3.txt", @"C:\a\con .txt",
        @"C:\a.", @"C:\a ", @"C:\a\b.", @"C:\a\b ", @"C:\a\...", @"C:\a\ \b",
        "C:\\a\\b\u0001", "C:\\a\\\u001Fb", "C:\\a\\b\u007F", "C:\\a\\b\0c", "C:\\a\0",
        @"C:\a\\b", @"C:\\a", @"C:\a\\",
        @"C:\a<b", @"C:\a>b", "C:\\a\"b", @"C:\a|b", @"C:\a?b", @"C:\a*b",
    };

    [Theory]
    [MemberData(nameof(RejectedInputs))]
    public void Rejects_invalid_paths(string input)
    {
        LocalPath.TryParse(input, out var result, out var error).Should().BeFalse($"'{input}' must be rejected");
        result.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rejects_doubled_separator_directly_after_drive_root()
    {
        // "C:\\" (two backslashes) has an empty component; the trailing-separator normalisation swallows it.
        LocalPath.TryParse(@"C:\\", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_throws_ArgumentException_for_invalid()
    {
        var act = () => LocalPath.Parse(@"C:\a\..\b");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_null()
    {
        LocalPath.TryParse(null, out var result, out var error).Should().BeFalse();
        result.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rejects_dotdot_anywhere_even_when_it_would_stay_in_bounds()
    {
        LocalPath.TryParse(@"C:\a\b\..\c", out _, out _).Should().BeFalse();
    }

    // ---------- handle path ----------

    [Fact]
    public void Handle_path_accepts_extended_drive_form()
    {
        LocalPath.TryParseHandlePath(@"\\?\C:\a\b", out var result, out var error).Should().BeTrue(error);
        result!.ToString().Should().Be(@"C:\a\b");
        LocalPath.TryParseHandlePath(@"\\?\D:\", out var root, out _).Should().BeTrue();
        root!.Components.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"C:\a")]
    [InlineData(@"\\?\UNC\server\share\x")]
    [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\x")]
    [InlineData(@"\\?\C:\a\..\..\Windows")]
    [InlineData(@"\\?\C:\a\..")]
    [InlineData(@"\\.\C:\a")]
    [InlineData(@"\\?\C:")]
    [InlineData(@"\\?\")]
    [InlineData(@"\\?\GLOBALROOT\Device\x")]
    [InlineData(@"\??\C:\x")]
    [InlineData(@"\\?\C:\a\file.txt:stream")]
    [InlineData(@"\\?\C:\\?\D:\x")]
    [InlineData(@"\\?\C:\a\CON")]
    [InlineData("")]
    [InlineData(null)]
    public void Handle_path_rejects(string? input)
    {
        LocalPath.TryParseHandlePath(input, out var result, out var error).Should().BeFalse();
        result.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    // ---------- IsStrictlyUnder ----------

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"C:\a\b\c\d", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\", true)]
    [InlineData(@"C:\a", @"C:\a", false)]          // itself
    [InlineData(@"C:\", @"C:\", false)]            // root itself
    [InlineData(@"C:\", @"C:\a", false)]           // ancestor
    [InlineData(@"C:\ab", @"C:\a", false)]         // shared string prefix, different component
    [InlineData(@"C:\ab\c", @"C:\a", false)]
    [InlineData(@"D:\a\b", @"C:\a", false)]        // different drive
    [InlineData(@"C:\A\B", @"c:\a", true)]         // case-insensitive
    [InlineData(@"C:\a\b", @"C:\x", false)]
    public void IsStrictlyUnder(string path, string root, bool expected)
    {
        LocalPath.Parse(path).IsStrictlyUnder(LocalPath.Parse(root)).Should().Be(expected);
    }

    [Fact]
    public void IsStrictlyUnder_null_throws()
    {
        var act = () => LocalPath.Parse(@"C:\a").IsStrictlyUnder(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---------- equality ----------

    [Fact]
    public void Equality_and_hash_are_case_insensitive()
    {
        var a = LocalPath.Parse(@"C:\Users\Test\File");
        var b = LocalPath.Parse(@"c:\USERS\test\FILE\");
        a.Equals(b).Should().BeTrue();
        a.Equals((object)b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        new HashSet<LocalPath> { a }.Contains(b).Should().BeTrue();
    }

    [Fact]
    public void Inequality_cases()
    {
        var a = LocalPath.Parse(@"C:\a\b");
        a.Equals(LocalPath.Parse(@"C:\a\c")).Should().BeFalse();
        a.Equals(LocalPath.Parse(@"D:\a\b")).Should().BeFalse();
        a.Equals(LocalPath.Parse(@"C:\a")).Should().BeFalse();
        object notAPath = "C:\\a\\b";
        a.Equals(notAPath).Should().BeFalse();
        LocalPath? none = null;
        a.Equals(none).Should().BeFalse();
    }

    // ---------- Append ----------

    [Fact]
    public void Append_rejects_empty_component()
    {
        var act = () => LocalPath.Parse(@"C:\a").Append("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Append_rejects_multiple_components_in_one_call()
    {
        var act = () => LocalPath.Parse(@"C:\a").Append(@"x\y");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Append_adds_component()
    {
        LocalPath.Parse(@"C:\a").Append("b").ToString().Should().Be(@"C:\a\b");
        LocalPath.Parse(@"C:\").Append("b").ToString().Should().Be(@"C:\b");
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a\\b\\..")]
    [InlineData("b:stream")]
    [InlineData("CON")]
    [InlineData("x.")]
    [InlineData("x ")]
    [InlineData("a?b")]
    [InlineData("a/b")]
    public void Append_rejects_invalid_component(string component)
    {
        var act = () => LocalPath.Parse(@"C:\a").Append(component);
        act.Should().Throw<ArgumentException>();
        var rootAct = () => LocalPath.Parse(@"C:\").Append(component);
        rootAct.Should().Throw<ArgumentException>();
    }
}
