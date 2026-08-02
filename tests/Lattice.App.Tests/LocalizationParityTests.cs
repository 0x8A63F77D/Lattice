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
    public void Scanner_rejects_malformed_format_strings(string value)
    {
        Assert.NotNull(ResxCatalog.ScanPlaceholders(value).Error);
    }

    private static IReadOnlySet<string> ReferencedNames()
    {
        // Matches both dialects at once: C# `Strings.ColProject` and XAML
        // `{x:Static loc:Strings.ColProject}`.
        Regex reference = new(@"\bStrings\.(\w+)", RegexOptions.CultureInvariant);
        return ResxCatalog.AppSourceFiles()
            .SelectMany(path => reference.Matches(File.ReadAllText(path)))
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
