using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    /// indexes it consumes, plus whatever <see cref="string.Format(string, object?[])"/>
    /// would reject about it.
    /// <para>
    /// The set — not the order — is what must agree across languages: CJK word order
    /// legitimately reorders arguments, while a missing index is a runtime
    /// <see cref="FormatException"/> under exactly one culture.
    /// </para>
    /// <para>
    /// Validity is delegated to <see cref="CompositeFormat"/>, i.e. to the very parser
    /// <c>string.Format</c> runs on. Owning the grammar here would mean re-deriving it:
    /// an index-only scanner happily accepts <c>{0,abc}</c> and <c>{0,}</c>, which throw
    /// at runtime — a well-formedness check that green-lights a crash is worse than none.
    /// Index extraction below stays local because the framework exposes an argument
    /// COUNT, not the set of indexes; it runs only for its indexes, never as the verdict.
    /// </para>
    /// </summary>
    internal static PlaceholderScan ScanPlaceholders(string value)
    {
        string? error = null;
        try
        {
            CompositeFormat.Parse(value);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
        }

        return new PlaceholderScan(ExtractIndexes(value), error);
    }

    /// <summary>
    /// Argument indexes of every format item, treating <c>{{</c> / <c>}}</c> as literal
    /// braces and discarding alignment / format specifiers (<c>{0,-8:F2}</c> → 0). Only
    /// meaningful for a value <see cref="CompositeFormat"/> accepts; on a malformed one
    /// it returns what it could read and leaves the verdict to the parser above.
    /// </summary>
    private static IReadOnlySet<int> ExtractIndexes(string value)
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
                }

                continue;
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
                break;
            }

            string body = value[(i + 1)..close];
            int specifier = body.IndexOfAny([',', ':']);
            string digits = (specifier < 0 ? body : body[..specifier]).Trim();
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                indexes.Add(index);
            }

            i = close;
        }

        return indexes;
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

    /// <summary>
    /// Resource names a source file actually REFERENCES, read off the parsed syntax
    /// rather than the file's text.
    /// <para>
    /// Text scanning cannot answer this question, and two review rounds on this file are
    /// the evidence: a comment naming a resource (this codebase discusses
    /// <c>Strings.ColX</c> in prose in <c>TasksView.axaml.cs</c>) and a string literal
    /// containing <c>"Strings.Foo"</c> both read as references, each keeping a dead key
    /// alive. Patching a hand-rolled scanner for one hazard at a time just reopens the
    /// same finding under a new costume — comments, verbatim strings, raw strings,
    /// interpolation holes. So the question moves to the parsers that own it: in C# a
    /// comment is trivia and a literal is a token, so neither can ever be a
    /// <see cref="MemberAccessExpressionSyntax"/>; in XAML an XML comment is not part of
    /// the element tree at all. Both exclusions hold by construction, not by vigilance.
    /// </para>
    /// <para>
    /// Syntax only — no compilation, no semantic model. That means the match is on the
    /// NAME <c>Strings</c>, not on a resolved symbol; the app has exactly one such type,
    /// pinned by <c>LocalizationParityTests.Resource_names_are_never_constructed_dynamically</c>'s
    /// neighbourhood and by the compiler itself (a wrong <c>Strings.Foo</c> would not build).
    /// </para>
    /// </summary>
    internal static IEnumerable<string> ReferencedNames(string path)
    {
        string text = File.ReadAllText(path);
        return path.EndsWith(".axaml", StringComparison.Ordinal)
            ? XamlReferences(text)
            : CSharpReferences(text);
    }

    /// <summary>
    /// <c>Strings.Foo</c> as a member access anywhere in the tree — including inside an
    /// interpolation hole, which is an expression like any other. Trivia (comments, doc
    /// comments, disabled <c>#if</c> regions) and literal tokens are not expressions and
    /// so never appear here.
    /// </summary>
    private static IEnumerable<string> CSharpReferences(string text) =>
        CSharpSyntaxTree.ParseText(text).GetRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(access => NamesStringsType(access.Expression))
            .Select(access => access.Name.Identifier.ValueText);

    /// <summary>True for <c>Strings</c> and for any qualified form ending in it.</summary>
    private static bool NamesStringsType(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "Strings",
        MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText == "Strings",
        _ => false,
    };

    /// <summary>
    /// The XAML binding form, <c>{x:Static loc:Strings.Foo}</c>, read from attribute
    /// values and element text of the parsed document — so XML comments, which are not
    /// in the element tree, are structurally out of reach.
    /// <para>
    /// Two XAML rules decide the rest, and both are the language's, not heuristics of
    /// ours. First, a value is markup only if it BEGINS with <c>{</c>: prose such as
    /// <c>Text="use {x:Static loc:Strings.Foo} like this"</c> is literal text end to end,
    /// which is also why <c>{}</c> exists to escape a leading brace. Second, a reference
    /// must be a complete brace-delimited <c>{x:Static …}</c> item, not the phrase
    /// inside one — matching the interior would count prose that merely spells it out.
    /// Nesting still works (<c>{Binding …, Converter={x:Static …}}</c>) because the
    /// opening brace an inner extension needs is exactly what the pattern requires.
    /// </para>
    /// </summary>
    private static IEnumerable<string> XamlReferences(string text)
    {
        XDocument doc = XDocument.Parse(text);
        IEnumerable<string> values = doc.Descendants()
            .SelectMany(element => element.Attributes().Select(attribute => attribute.Value)
                .Concat(element.Nodes().OfType<XText>().Select(node => node.Value)));

        return values.Where(IsMarkup)
            .SelectMany(value => StaticReference.Matches(value))
            .Select(match => match.Groups["name"].Value);
    }

    /// <summary>A value that opens with <c>{</c>, excluding the <c>{}</c> literal escape.</summary>
    private static bool IsMarkup(string value)
    {
        string trimmed = value.TrimStart();
        return trimmed.StartsWith('{') && !trimmed.StartsWith("{}", StringComparison.Ordinal);
    }

    private static readonly Regex StaticReference = new(
        @"\{\s*x:Static\s+(?:\w+:)?Strings\.(?<name>\w+)\s*\}", RegexOptions.CultureInvariant);

    private static bool IsBuildOutput(string path, string sourceRoot)
    {
        string relative = Path.GetRelativePath(sourceRoot, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "obj" or "bin");
    }
}
