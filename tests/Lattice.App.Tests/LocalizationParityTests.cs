using System.Globalization;
using System.Text;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Structural gate over the two shipped resource files (issue #210). Nothing else
/// keeps <c>Strings.resx</c> and <c>Strings.zh-CN.resx</c> in step: a key added to one
/// file only just falls back to English at runtime, and a <c>{0}</c> present in one
/// language and absent in the other is a latent <see cref="FormatException"/> that
/// fires under exactly one culture — neither shows up in a build or in any other test.
/// <para>
/// The files are read as XML through <see cref="ResxCatalog"/>, not through the
/// generated <c>Strings</c> class; see that type for why.
/// </para>
/// </summary>
public class LocalizationParityTests
{
    private static readonly IReadOnlyDictionary<string, string> Neutral =
        ResxCatalog.Load(ResxCatalog.NeutralFile);

    private static readonly IReadOnlyDictionary<string, string> Chinese =
        ResxCatalog.Load(ResxCatalog.ChineseFile);

    /// <summary>
    /// Keys whose value carries an argument slot in EITHER language — the composite format
    /// strings. Scoped by what the value is, not by which call sites pass it to
    /// <c>string.Format</c>; see <see cref="ResxCatalog.CarriesPlaceholder"/> for why that
    /// question is not worth asking. "Either language" matters: a zh-CN value that lost its
    /// <c>{0}</c> must still be judged as the format string it is.
    /// </summary>
    private static readonly IReadOnlySet<string> FormatKeys =
        Neutral.Concat(Chinese)
            .Where(entry => ResxCatalog.CarriesPlaceholder(entry.Value))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Zh_CN_defines_every_neutral_key()
    {
        string[] missing = Neutral.Keys.Except(Chinese.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0, Diff(
            $"{missing.Length} key(s) exist in {ResxCatalog.NeutralFile} but not in {ResxCatalog.ChineseFile}",
            missing.Select(key => $"- {key}")));
    }

    [Fact]
    public void Neutral_defines_every_zh_CN_key()
    {
        string[] extra = Chinese.Keys.Except(Neutral.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(extra.Length == 0, Diff(
            $"{extra.Length} key(s) exist in {ResxCatalog.ChineseFile} but not in {ResxCatalog.NeutralFile}",
            extra.Select(key => $"+ {key}")));
    }

    [Fact]
    public void Placeholder_sets_match_across_languages()
    {
        List<string> mismatches = [];
        foreach (string key in Neutral.Keys.Intersect(Chinese.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            ResxCatalog.PlaceholderScan neutral = ResxCatalog.ScanPlaceholders(Neutral[key]);
            ResxCatalog.PlaceholderScan chinese = ResxCatalog.ScanPlaceholders(Chinese[key]);
            if (neutral.Indexes.SetEquals(chinese.Indexes))
            {
                continue;
            }

            mismatches.Add($"- {key}: en {Format(neutral.Indexes)} vs zh-CN {Format(chinese.Indexes)}");
            mismatches.Add($"    en    = {Neutral[key]}");
            mismatches.Add($"    zh-CN = {Chinese[key]}");
        }

        Assert.True(mismatches.Count == 0, Diff(
            "placeholder sets differ between languages (order may differ, the set may not)",
            mismatches));
    }

    /// <summary>
    /// Only the values that are composite format strings have to satisfy the composite
    /// format grammar. A directly rendered label may legitimately contain a lone brace —
    /// <c>Set {</c> displays fine — and demanding it be doubled would make the label render
    /// two braces to satisfy a parser that never runs on it.
    /// </summary>
    [Theory]
    [InlineData(ResxCatalog.NeutralFile)]
    [InlineData(ResxCatalog.ChineseFile)]
    public void Every_format_string_value_is_well_formed(string fileName)
    {
        string[] broken = ResxCatalog.LoadEntries(fileName)
            .Where(entry => FormatKeys.Contains(entry.Name))
            .Select(entry => (entry.Name, Scan: ResxCatalog.ScanPlaceholders(entry.Value)))
            .Where(pair => pair.Scan.Error is not null)
            .Select(pair => $"- {pair.Name}: {pair.Scan.Error}")
            .ToArray();

        Assert.True(broken.Length == 0, Diff(
            $"{fileName} has value(s) string.Format would reject", broken));
    }

    [Theory]
    [InlineData(ResxCatalog.NeutralFile)]
    [InlineData(ResxCatalog.ChineseFile)]
    public void Keys_are_unique_within_each_file(string fileName)
    {
        string[] duplicates = ResxCatalog.LoadEntries(fileName)
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"- {group.Key} ({group.Count()} entries)")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(duplicates.Length == 0, Diff($"{fileName} declares a key more than once", duplicates));
    }

    /// <summary>
    /// A key that survives parity with an empty value renders a blank label, and every
    /// other check here stays green while it does: the key sets match and both placeholder
    /// sets are empty. <c>LocalizationTests.Every_resx_key_resolves_to_a_nonempty_string</c>
    /// walks the built resource table for the NEUTRAL culture only, so the zh-CN satellite
    /// has no other guard at all.
    /// </summary>
    [Theory]
    [InlineData(ResxCatalog.NeutralFile)]
    [InlineData(ResxCatalog.ChineseFile)]
    public void Values_are_never_blank(string fileName)
    {
        string[] blank = ResxCatalog.LoadEntries(fileName)
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => $"- {entry.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(blank.Length == 0, Diff($"{fileName} has key(s) with a blank value", blank));
    }

    /// <summary>
    /// Dead-key inventory: a key nothing references is a translation nobody reads and a
    /// string every future translator still has to service. The scan is sound only
    /// because no resource name in the app is built at runtime — that premise is pinned
    /// by <see cref="Resource_names_are_never_constructed_dynamically"/>.
    /// </summary>
    [Fact]
    public void Every_key_is_referenced_by_app_sources()
    {
        IReadOnlySet<string> referenced = ReferencedNames();
        string[] dead = Neutral.Keys.Where(key => !referenced.Contains(key)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(dead.Length == 0, Diff(
            $"{dead.Length} resx key(s) are referenced by no XAML (loc:Strings.X) or C# (Strings.X) under src/ — "
                + "wire them up or delete them from BOTH resx files",
            dead.Select(key => $"- {key}")));
    }

    /// <summary>
    /// The premise of the dead-key scan. A dynamic lookup
    /// (<c>Strings.ResourceManager.GetString(someName)</c>) would reach keys no static
    /// reference names, turning the inventory above into a source of wrong deletions.
    /// The app has none — every string is reached through a generated static property —
    /// and this keeps it that way. If one is ever genuinely needed, this test is the
    /// place to document the namespace it can reach and to exclude that prefix from the
    /// dead-key scan rather than to delete the guard.
    /// </summary>
    [Fact]
    public void Resource_names_are_never_constructed_dynamically()
    {
        // Same parser-backed scan as the inventory: `Strings.ResourceManager` is a member
        // access like any other, so it surfaces as the name "ResourceManager" — and a
        // comment or a literal discussing it cannot red this guard by accident.
        string[] offenders = ResxCatalog.AppSourceFiles()
            .Where(path => ResxCatalog.ReferencedNames(path).Contains("ResourceManager"))
            .Select(path => $"- {Path.GetRelativePath(ResxCatalog.RepositoryRoot, path)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0, Diff(
            "src/ reaches the resource table by name instead of through the generated accessors", offenders));
    }

    [Theory]
    // Escaped braces are literal text, not placeholders — a value may contain them
    // without consuming an argument. No shipped string does today; the parser is
    // pinned here so the first one that does cannot quietly break the parity gate.
    [InlineData("{{0}}", new int[0])]
    [InlineData("{{{0}}}", new[] { 0 })]
    [InlineData("literal {{ }} text", new int[0])]
    [InlineData("no placeholders", new int[0])]
    [InlineData("{0} of {1}", new[] { 0, 1 })]
    // Order is not part of the set: this is the CJK-reordering case the gate must allow.
    [InlineData("{1} / {0}", new[] { 0, 1 })]
    // A repeated index contributes once.
    [InlineData("{0} … {0}", new[] { 0 })]
    // Alignment and format specifiers are stripped down to the argument index.
    [InlineData("{0,-8}", new[] { 0 })]
    [InlineData("{0:F2} MB", new[] { 0 })]
    [InlineData("{0,10:0.##}%", new[] { 0 })]
    public void Scanner_reads_placeholder_indexes(string value, int[] expected)
    {
        ResxCatalog.PlaceholderScan scan = ResxCatalog.ScanPlaceholders(value);
        Assert.Null(scan.Error);
        Assert.Equal(expected.Order(), scan.Indexes.Order());
    }

    [Theory]
    [InlineData("{0")]
    [InlineData("{}")]
    [InlineData("{name}")]
    [InlineData("0}")]
    [InlineData("{0} }")]
    // Alignment must be an integer. An index-only scanner reads the 0 and calls these
    // well formed; string.Format throws on both, so the grammar is the framework's.
    [InlineData("{0,abc}")]
    [InlineData("{0,}")]
    public void Scanner_rejects_malformed_format_strings(string value)
    {
        Assert.NotNull(ResxCatalog.ScanPlaceholders(value).Error);
        Assert.Throws<FormatException>(() => string.Format(CultureInfo.InvariantCulture, value, "x", "y"));
    }

    /// <summary>
    /// What counts as a reference. Every <c>Ghost</c> below is a way for a resource name
    /// to appear in a file WITHOUT the code using the resource — the exact shapes that
    /// keep a dead key looking alive — and every <c>Live</c> is a real use that must
    /// survive the scan. All of them fall out of the parsers rather than being special
    /// cases in a scanner.
    /// </summary>
    [Theory]
    // Trivia is not an expression.
    [InlineData("class C { void M() { var a = Strings.Live; } } // Strings.Ghost", "Live")]
    [InlineData("class C { /* Strings.Ghost */ void M() { var a = Strings.Live; } }", "Live")]
    [InlineData("/// <summary>Strings.Ghost</summary>\nclass C { void M() { var a = Strings.Live; } }", "Live")]
    // Neither is a literal token — a diagnostic or a code sample that spells a resource
    // name does not use it.
    [InlineData("""class C { void M() { var s = "Strings.Ghost"; var a = Strings.Live; } }""", "Live")]
    [InlineData("""class C { void M() { var s = @"Strings.Ghost"; var a = Strings.Live; } }""", "Live")]
    [InlineData("""class C { void M() { var s = "http://x/y"; var a = Strings.Live; } }""", "Live")]
    // …and a raw string literal, which the previous hand-rolled scanner could not model.
    [InlineData("class C { void M() { var s = \"\"\"Strings.Ghost\"\"\"; var a = Strings.Live; } }", "Live")]
    // A branch NO shipped configuration compiles is trivia…
    [InlineData("class C { void M() {\n#if NEVER\nvar b = Strings.Ghost;\n#endif\nvar a = Strings.Live; } }", "Live")]
    // …but a DEBUG-only region is real code in the Debug build, and this repo has some.
    [InlineData("class C { void M() {\n#if DEBUG\nvar a = Strings.Live;\n#endif\n} }", "Live")]
    // Both sides of a conditional are read, since both ship in some configuration.
    [InlineData("class C { void M() {\n#if DEBUG\nvar a = Strings.Live;\n#else\nvar b = Strings.Live;\n#endif\n} }", "Live")]
    // An aliased import of the resource type is still the resource type.
    [InlineData("using Text = Lattice.App.Localization.Strings;\nclass C { void M() { var a = Text.Live; } }", "Live")]
    // nameof compiles to a literal and reads no resource, so it keeps none alive.
    [InlineData("class C { void M() { var n = nameof(Strings.Ghost); var a = Strings.Live; } }", "Live")]
    // …but a real read elsewhere in the same call still counts.
    [InlineData("class C { void M() { Log(nameof(Strings.Ghost), Strings.Live); } }", "Live")]
    // @nameof is an escaped identifier — an ordinary method whose argument IS evaluated,
    // so excluding it would report a live key as dead.
    [InlineData("class C { void M() { var n = @nameof(Strings.Live); } }", "Live")]
    // An interpolation hole IS an expression, so it counts.
    [InlineData("""class C { void M() { var s = $"{Strings.Live}"; } }""", "Live")]
    // Fully qualified access counts.
    [InlineData("class C { void M() { var a = Lattice.App.Localization.Strings.Live; } }", "Live")]
    public void CSharp_references_are_expressions_not_text(string source, string expected)
    {
        string[] names = ScanSource(source, "Sample.cs");
        Assert.Contains(expected, names);
        Assert.DoesNotContain("Ghost", names);
    }

    [Theory]
    // The binding form counts, in an attribute or in element text…
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="{x:Static loc:Strings.Live}" />""", "Live")]
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><T.Text>{x:Static loc:Strings.Live}</T.Text></T>""", "Live")]
    // …including nested inside another markup extension, which is how converters read.
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="{Binding X, Converter={x:Static loc:Strings.Live}}" />""", "Live")]
    // An XML comment does not — it is not in the element tree.
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="{x:Static loc:Strings.Live}"><!-- {x:Static loc:Strings.Ghost} --></T>""", "Live")]
    // Nor does prose that spells the name…
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="{x:Static loc:Strings.Live}" ToolTip.Tip="see Strings.Ghost" />""", "Live")]
    // …nor prose that spells the whole binding form: a value not opening with '{' is
    // literal text in XAML, braces and all.
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="{x:Static loc:Strings.Live}" ToolTip.Tip="use {x:Static loc:Strings.Ghost} here" />""", "Live")]
    // …nor a value whose leading brace is the {} literal escape.
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="{x:Static loc:Strings.Live}" ToolTip.Tip="{}{x:Static loc:Strings.Ghost}" />""", "Live")]
    // …nor the interior phrase without its delimiters.
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="{x:Static loc:Strings.Live}" ToolTip.Tip="{Binding x:Static loc:Strings.Ghost}" />""", "Live")]
    // A prefix may contain punctuation: NCName admits '-' and '.'.
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:app-loc="using:Lattice.App.Localization" Text="{x:Static app-loc:Strings.Live}" />""", "Live")]
    // The prefix is whatever the document binds to the XAML language namespace — `x` is a
    // convention, not a rule, and a key bound through another prefix is NOT dead.
    [InlineData("""<T xmlns:lang="http://schemas.microsoft.com/winfx/2006/xaml" Text="{lang:Static loc:Strings.Live}" />""", "Live")]
    // …while a prefix bound to some other namespace is not the Static extension at all.
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:other="urn:other" Text="{x:Static loc:Strings.Live}" Tag="{other:Static loc:Strings.Ghost}" />""", "Live")]
    // …nor the whole form quoted as a literal ARGUMENT of a real markup extension, which
    // is markup on the outside and text on the inside.
    [InlineData("""<T xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="{Binding X, FallbackValue='use {x:Static loc:Strings.Ghost} here', Converter={x:Static loc:Strings.Live}}" />""", "Live")]
    public void Xaml_references_require_the_binding_form(string source, string expected)
    {
        string[] names = ScanSource(source, "Sample.axaml");
        Assert.Contains(expected, names);
        Assert.DoesNotContain("Ghost", names);
    }

    private static string[] ScanSource(string source, string fileName)
    {
        string path = Path.Combine(Path.GetTempPath(), $"lattice-loc-{Guid.NewGuid():N}-{fileName}");
        try
        {
            File.WriteAllText(path, source);
            return ResxCatalog.ReferencedNames(path).ToArray();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IReadOnlySet<string> ReferencedNames() =>
        ResxCatalog.AppSourceFiles()
            .SelectMany(ResxCatalog.ReferencedNames)
            .ToHashSet(StringComparer.Ordinal);

    private static string Format(IReadOnlySet<int> indexes) =>
        indexes.Count == 0 ? "{}" : "{" + string.Join(", ", indexes.Order()) + "}";

    private static string Diff(string headline, IEnumerable<string> lines)
    {
        StringBuilder sb = new(headline);
        foreach (string line in lines)
        {
            sb.Append(Environment.NewLine).Append(line);
        }

        return sb.ToString();
    }
}
