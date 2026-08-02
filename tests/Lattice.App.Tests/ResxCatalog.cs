using System.Xml.Linq;

namespace Lattice.App.Tests;

/// <summary>
/// Reads the shipped .resx files as raw XML, deliberately NOT through the generated
/// <c>Strings</c> class or its <see cref="System.Resources.ResourceManager"/>.
/// <para>
/// The generator only surfaces what it decides to emit, and the resource manager
/// silently falls back from a satellite culture to the neutral table — both would
/// hide exactly the drift the localization gate exists to catch (a key present in
/// one file only). Parsing <c>&lt;data name="…"&gt;</c> entries straight from disk is
/// the only view that sees each file for what it actually contains.
/// </para>
/// </summary>
internal static class ResxCatalog
{
    internal const string NeutralFile = "Strings.resx";
    internal const string ChineseFile = "Strings.zh-CN.resx";

    /// <summary>A single <c>&lt;data&gt;</c> entry, in file order.</summary>
    internal readonly record struct Entry(string Name, string Value);

    /// <summary>
    /// Placeholder indexes found in a composite format string, plus the first
    /// malformedness encountered (null when the string is well formed).
    /// </summary>
    internal readonly record struct PlaceholderScan(IReadOnlySet<int> Indexes, string? Error);

    /// <summary>
    /// Repository root, found by walking up from the test binary until the solution
    /// file appears. Works from a worktree, a CI checkout, and any build configuration.
    /// </summary>
    internal static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string LocalizationDirectory =>
        Path.Combine(RepositoryRoot, "src", "Lattice.App", "Localization");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            $"Could not locate Lattice.sln above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>
    /// All <c>&lt;data&gt;</c> entries of a resx file, in document order and including
    /// any duplicate names (the uniqueness check is a test, not a parse-time throw).
    /// </summary>
    internal static IReadOnlyList<Entry> LoadEntries(string fileName)
    {
        XDocument doc = XDocument.Load(Path.Combine(LocalizationDirectory, fileName));
        return doc.Root!.Elements("data")
            .Select(data => new Entry(
                (string?)data.Attribute("name")
                    ?? throw new InvalidOperationException($"{fileName}: <data> entry without a name attribute."),
                data.Element("value")?.Value ?? string.Empty))
            .ToList();
    }

    /// <summary>Name → value map; on a duplicate name the first entry wins.</summary>
    internal static IReadOnlyDictionary<string, string> Load(string fileName)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Entry entry in LoadEntries(fileName))
        {
            map.TryAdd(entry.Name, entry.Value);
        }

        return map;
    }

    /// <summary>
    /// Scans a value as a .NET composite format string and returns the SET of argument
    /// indexes it consumes. <c>{{</c> and <c>}}</c> are literal braces, not placeholders;
    /// alignment and format specifiers (<c>{0,-8:F2}</c>) are stripped down to the index.
    /// The set — not the order — is what must agree across languages: CJK word order
    /// legitimately reorders arguments, while a missing index is a runtime
    /// <see cref="FormatException"/> under exactly one culture.
    /// </summary>
    internal static PlaceholderScan ScanPlaceholders(string value)
    {
        var indexes = new SortedSet<int>();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '}')
            {
                if (i + 1 < value.Length && value[i + 1] == '}')
                {
                    i++;
                    continue;
                }

                return new PlaceholderScan(indexes, $"unescaped '}}' at index {i}");
            }

            if (c != '{')
            {
                continue;
            }

            if (i + 1 < value.Length && value[i + 1] == '{')
            {
                i++;
                continue;
            }

            int close = value.IndexOf('}', i + 1);
            if (close < 0)
            {
                return new PlaceholderScan(indexes, $"unterminated format item at index {i}");
            }

            string body = value[(i + 1)..close];
            int specifier = body.IndexOfAny([',', ':']);
            string digits = specifier < 0 ? body : body[..specifier];
            if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || !int.TryParse(digits, out int index))
            {
                return new PlaceholderScan(indexes,
                    $"format item '{{{body}}}' at index {i} has no numeric argument index");
            }

            indexes.Add(index);
            i = close;
        }

        return new PlaceholderScan(indexes, null);
    }

    /// <summary>Every C# and XAML source file under <c>src/</c>, obj/bin excluded.</summary>
    internal static IEnumerable<string> AppSourceFiles()
    {
        string src = Path.Combine(RepositoryRoot, "src");
        return Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(path => !IsBuildOutput(path, src));
    }

    private static bool IsBuildOutput(string path, string sourceRoot)
    {
        string relative = Path.GetRelativePath(sourceRoot, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "obj" or "bin");
    }
}
