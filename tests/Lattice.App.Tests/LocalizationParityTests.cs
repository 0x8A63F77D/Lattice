using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

    [Theory]
    [InlineData(ResxCatalog.NeutralFile)]
    [InlineData(ResxCatalog.ChineseFile)]
    public void Every_value_is_a_well_formed_composite_format_string(string fileName)
    {
        string[] broken = ResxCatalog.LoadEntries(fileName)
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
    /// A key that survives parity with an empty value renders a blank label, which no
    /// other check catches: <c>LocalizationTests.Every_resx_key_resolves_to_a_nonempty_string</c>
    /// walks the built resource table for the NEUTRAL culture only, so the zh-CN
    /// satellite had no such guard at all.
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
        string[] offenders = ResxCatalog.AppSourceFiles()
            .Where(path => File.ReadAllText(path).Contains("Strings.ResourceManager", StringComparison.Ordinal))
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
    /// The dead-key scan must read code, not prose. These are the cases where a naive
    /// text scan and a comment-aware one disagree — in both directions: a comment that
    /// names a resource must NOT keep it alive, and a string literal that happens to
    /// contain <c>//</c> (every URL in the codebase) must not swallow the real
    /// reference that follows it.
    /// </summary>
    [Theory]
    // C#: comments do not count.
    [InlineData("var a = Strings.Live; // Strings.Ghost", false, "Live")]
    [InlineData("/* Strings.Ghost */ var a = Strings.Live;", false, "Live")]
    [InlineData("/// <summary>Strings.Ghost</summary>\nvar a = Strings.Live;", false, "Live")]
    // C#: literals are not comments — the URL must not eat the rest of the line.
    [InlineData("var u = \"http://x/y\"; var a = Strings.Live;", false, "Live")]
    [InlineData("var v = @\"C:\\p // q\"; var a = Strings.Live;", false, "Live")]
    [InlineData("var q = '\"'; var a = Strings.Live;", false, "Live")]
    [InlineData("var e = \"a\\\"// b\"; var a = Strings.Live;", false, "Live")]
    // XAML: same rule, XML syntax.
    [InlineData("<!-- {x:Static loc:Strings.Ghost} -->\n<T Text=\"{x:Static loc:Strings.Live}\" />", true, "Live")]
    public void Comment_stripping_keeps_code_and_drops_prose(string source, bool isXaml, string expected)
    {
        string stripped = ResxCatalog.StripComments(source, isXaml);
        Assert.Contains($"Strings.{expected}", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("Strings.Ghost", stripped, StringComparison.Ordinal);
    }

    private static IReadOnlySet<string> ReferencedNames()
    {
        // Matches both dialects at once: C# `Strings.ColProject` and XAML
        // `{x:Static loc:Strings.ColProject}` — over comment-stripped text, so a comment
        // that names a resource cannot keep the resource alive after its code is gone.
        Regex reference = new(@"\bStrings\.(\w+)", RegexOptions.CultureInvariant);
        return ResxCatalog.AppSourceFiles()
            .SelectMany(path => reference.Matches(ResxCatalog.StripComments(
                File.ReadAllText(path),
                isXaml: path.EndsWith(".axaml", StringComparison.Ordinal))))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

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
