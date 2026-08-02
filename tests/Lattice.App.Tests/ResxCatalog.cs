using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
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
    internal static IEnumerable<string> ReferencedNames(string path) =>
        ReferencedNames(path, GlobalResourceAliases.Value);

    /// <summary>
    /// As above, with the repository-wide <c>global using</c> aliases supplied explicitly.
    /// </summary>
    internal static IEnumerable<string> ReferencedNames(string path, IReadOnlySet<string> globalAliases)
    {
        string text = File.ReadAllText(path);
        return path.EndsWith(".axaml", StringComparison.Ordinal)
            ? XamlReferences(text)
            : CSharpReferences(text, globalAliases);
    }

    /// <summary>
    /// <c>Strings.Foo</c> as a member access anywhere in the tree — including inside an
    /// interpolation hole, which is an expression like any other. Trivia (comments and
    /// doc comments) and literal tokens are not expressions and so never appear here.
    /// <para>
    /// EVERY conditional branch is read, whichever symbol guards it. A branch excluded by
    /// the parse is disabled TRIVIA and therefore invisible, so a scan that fixes a symbol
    /// set decides which <c>#if</c>s exist: with none defined this repo's <c>#if DEBUG</c>
    /// blocks vanish, and with <c>DEBUG</c> alone an SDK symbol such as
    /// <c>NET10_0_OR_GREATER</c> still does. Enumerating symbols is a losing game — the
    /// SDK defines dozens and the list moves with the target framework — and every gap in
    /// it points the SAME dangerous way: a live key reported dead, whose implied remedy is
    /// deleting a translation that is in use. So the disabled text is re-parsed rather
    /// than guessed at. The cost is the opposite, harmless error: a key referenced only
    /// from a branch nothing compiles (<c>#if NEVER</c>) survives the inventory.
    /// </para>
    /// </summary>
    private static IEnumerable<string> CSharpReferences(string text, IReadOnlySet<string> globalAliases)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(text);

        while (pending.TryDequeue(out string? source))
        {
            if (!seen.Add(source))
            {
                continue;
            }

            SyntaxNode root = CSharpSyntaxTree.ParseText(source).GetRoot();
            names.UnionWith(ReferencesIn(root, globalAliases));
            foreach (SyntaxTrivia trivia in root.DescendantTrivia()
                .Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
            {
                pending.Enqueue(trivia.ToFullString());
            }
        }

        return names;
    }

    private static IEnumerable<string> ReferencesIn(SyntaxNode root, IReadOnlySet<string> globalAliases)
    {
        IReadOnlySet<string> resourceTypeNames = ResourceTypeNames(root, globalAliases);
        IEnumerable<string> qualified = root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(access => NamesStringsType(access.Expression, resourceTypeNames))
            .Where(access => !IsNameofOperand(access))
            .Select(access => access.Name.Identifier.ValueText);

        if (!ImportsResourcesStatically(root))
        {
            return qualified;
        }

        // `using static …Strings;` puts every resource in scope as a BARE identifier, and
        // syntax alone cannot tell `Live` the resource from `Live` the local. Counting all
        // identifiers over-approximates, which points the safe way: at worst a dead key
        // survives the inventory in this one file, where the alternative is recommending
        // the deletion of a translation that is in use.
        return qualified.Concat(root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(identifier => !IsNameofOperand(identifier))
            .Select(identifier => identifier.Identifier.ValueText));
    }

    /// <summary>
    /// The names under which this file can reach the generated resource type: its own, plus
    /// any <c>using Text = …Localization.Strings;</c> alias. Aliasing is an established
    /// idiom here (<c>ControlOpWire</c>, <c>ShellViewModel</c>), so treating the source
    /// spelling <c>Strings</c> as the only possibility would declare an aliased read dead.
    /// </summary>
    private static IReadOnlySet<string> ResourceTypeNames(SyntaxNode root, IReadOnlySet<string> globalAliases)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "Strings" };
        names.UnionWith(globalAliases);
        names.UnionWith(AliasesIn(root));
        return names;
    }

    private static IEnumerable<string> AliasesIn(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Where(directive => RightmostIdentifier(directive.NamespaceOrType) == "Strings")
            .Select(directive => directive.Alias?.Name.Identifier.ValueText)
            .OfType<string>();

    /// <summary>
    /// Aliases declared with <c>global using Text = …Strings;</c>, which apply to every file
    /// in the compilation but are DECLARED in one. Files are parsed independently here, so
    /// without this pre-pass the consuming file knows only the literal spelling and an
    /// aliased read elsewhere would be declared dead.
    /// </summary>
    private static readonly Lazy<IReadOnlySet<string>> GlobalResourceAliases = new(() =>
        AppSourceFiles()
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                .DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(directive => directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                .Where(directive => RightmostIdentifier(directive.NamespaceOrType) == "Strings")
                .Select(directive => directive.Alias?.Name.Identifier.ValueText)
                .OfType<string>())
            .ToHashSet(StringComparer.Ordinal));

    private static bool ImportsResourcesStatically(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Any(directive => directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                && RightmostIdentifier(directive.NamespaceOrType) == "Strings");

    private static string? RightmostIdentifier(SyntaxNode? node) => node switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => RightmostIdentifier(qualified.Right),
        AliasQualifiedNameSyntax aliasQualified => RightmostIdentifier(aliasQualified.Name),
        _ => null,
    };

    /// <summary>
    /// <c>nameof(Strings.Foo)</c> is a member access in the tree but evaluates no getter —
    /// it compiles to the literal "Foo". Diagnostics and telemetry spell resources that way,
    /// and such a mention keeps no translation alive: nothing ever reads the resource.
    /// </summary>
    private static bool IsNameofOperand(SyntaxNode node) =>
        node.Ancestors()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" });

    /// <summary>
    /// Whether a value carries an argument slot, i.e. whether it is a composite format
    /// string that <c>string.Format</c> will parse.
    /// <para>
    /// This replaces asking the CALL SITES which resources are used as formats. That
    /// question needs C# overload resolution, and re-deriving it syntactically produced
    /// the same finding three times over — argument 0 vs 1, the provider-first overload,
    /// then named arguments — each time as a risk of failing CI over a display string
    /// that was never formatted. The value itself answers it without any of that: a
    /// format string carries <c>{0}</c>, and a label that merely displays <c>Set {</c>
    /// does not, which is exactly the distinction the checks need.
    /// </para>
    /// <para>
    /// The test is a brace PAIR, not a valid argument index, because a malformed item is
    /// exactly what needs checking: <c>{name}</c> and <c>{0x}</c> yield no index, so keying
    /// on "has an index" would have excluded them from the grammar check and let a
    /// guaranteed runtime <see cref="FormatException"/> through a green gate. An unpaired
    /// brace stays out — that is the <c>Set {</c> label the scoping exists to protect.
    /// </para>
    /// <para>
    /// The residual gap is a value used as a format that carries no brace pair at all (say
    /// <c>Set {</c> passed to <c>string.Format</c>): unchecked here, and it throws at
    /// runtime. A format string with nothing to format is a contradiction in terms, and
    /// paying for it in false CI failures on ordinary labels is the trade this deliberately
    /// refuses.
    /// </para>
    /// </summary>
    internal static bool CarriesPlaceholder(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '}' && i + 1 < value.Length && value[i + 1] == '}')
            {
                i++;
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
                return false;
            }

            return true;
        }

        return false;
    }

    /// <summary>True for a name the file can use for the resource type, plain or qualified.</summary>
    private static bool NamesStringsType(ExpressionSyntax expression, IReadOnlySet<string> names) => expression switch
    {
        IdentifierNameSyntax identifier => names.Contains(identifier.Identifier.ValueText),
        MemberAccessExpressionSyntax qualified => names.Contains(qualified.Name.Identifier.ValueText),
        _ => false,
    };

    /// <summary>
    /// Resource names bound by <c>{x:Static loc:Strings.Foo}</c>, taken from the attribute
    /// values and element text of the parsed document — so XML comments, not being in the
    /// element tree, are structurally out of reach.
    /// </summary>
    private static IEnumerable<string> XamlReferences(string text)
    {
        XDocument doc = XDocument.Parse(text);
        IReadOnlySet<string> staticExtensions = StaticExtensionNames(doc);
        return doc.Descendants()
            .SelectMany(element => element.Attributes().Select(attribute => attribute.Value)
                .Concat(element.Nodes().OfType<XText>().Select(node => node.Value)))
            .SelectMany(value => MarkupReferences(value, staticExtensions));
    }

    /// <summary>
    /// Every spelling of the <c>Static</c> extension this document can use, derived from
    /// its own namespace declarations. <c>x</c> is a convention, not a rule: a view may
    /// bind the XAML language namespace to any prefix, and
    /// <c>{lang:Static loc:Strings.Foo}</c> is then a real binding. Hard-coding
    /// <c>x:Static</c> would report the key it names as DEAD — the dangerous direction,
    /// since the remedy the failure implies is deleting a translation that is in use.
    /// </summary>
    private static IReadOnlySet<string> StaticExtensionNames(XDocument doc) =>
        doc.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.IsNamespaceDeclaration
                && attribute.Value == "http://schemas.microsoft.com/winfx/2006/xaml")
            .Select(attribute => attribute.Name.LocalName == "xmlns"
                ? "Static"
                : $"{attribute.Name.LocalName}:Static")
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Resource names an attribute value or text node BINDS, by parsing it as a markup
    /// extension rather than searching it for a phrase.
    /// <para>
    /// Text matching kept producing the same finding in new costumes here — prose naming
    /// the member, prose spelling the whole <c>{x:Static …}</c> form, then the form
    /// sitting inside a quoted extension argument
    /// (<c>{Binding X, FallbackValue='use {x:Static loc:Strings.Foo} here'}</c>). They are
    /// one bug: a regex cannot tell a binding from a sentence that describes one. Parsing
    /// the extension grammar decides it structurally, so a quoted argument is a literal,
    /// a nested <c>{…}</c> is a real extension, and prose is not markup at all.
    /// </para>
    /// <para>
    /// Entry rule is XAML's own: a value is markup only if it BEGINS with <c>{</c>, which
    /// is why <c>{}</c> exists to escape a leading brace. Anything else is literal text,
    /// braces and all.
    /// </para>
    /// </summary>
    internal static IEnumerable<string> MarkupReferences(string value, IReadOnlySet<string> staticExtensions)
    {
        string trimmed = value.TrimStart();
        if (!trimmed.StartsWith('{') || trimmed.StartsWith("{}", StringComparison.Ordinal))
        {
            return [];
        }

        List<string> names = [];
        int index = 0;
        ParseExtension(trimmed, ref index, names, staticExtensions);
        return names;
    }

    /// <summary>
    /// Consumes one <c>{Name arg, Prop=value}</c> item starting at <paramref name="index"/>,
    /// recursing into nested extensions and skipping quoted arguments, and records the
    /// member of every <c>x:Static</c> it meets that names a resource.
    /// </summary>
    private static void ParseExtension(
        string value, ref int index, ICollection<string> names, IReadOnlySet<string> staticExtensions)
    {
        index++; // the opening brace
        string extension = ReadToken(value, ref index);
        string? member = null;
        bool expectingPropertyValue = false;

        while (index < value.Length && value[index] != '}')
        {
            char c = value[index];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                index++;
                expectingPropertyValue = expectingPropertyValue && c != ',';
                continue;
            }

            if (c == '{')
            {
                ParseExtension(value, ref index, names, staticExtensions);
                expectingPropertyValue = false;
                continue;
            }

            if (c is '\'' or '"')
            {
                SkipQuoted(value, ref index);
                expectingPropertyValue = false;
                continue;
            }

            string token = ReadToken(value, ref index);
            if (index < value.Length && value[index] == '=')
            {
                index++; // the token was a property name; its value comes next
                expectingPropertyValue = true;
                continue;
            }

            if (!expectingPropertyValue)
            {
                member ??= token;
            }

            expectingPropertyValue = false;
        }

        if (index < value.Length)
        {
            index++; // the closing brace
        }

        if (staticExtensions.Contains(extension) && member is not null)
        {
            Match match = StaticMember.Match(member);
            if (match.Success)
            {
                names.Add(match.Groups["name"].Value);
            }
        }
    }

    private static string ReadToken(string value, ref int index)
    {
        int start = index;
        while (index < value.Length && !char.IsWhiteSpace(value[index])
            && value[index] is not ('{' or '}' or ',' or '=' or '\'' or '"'))
        {
            index++;
        }

        return value[start..index];
    }

    private static void SkipQuoted(string value, ref int index)
    {
        char quote = value[index++];
        while (index < value.Length && value[index] != quote)
        {
            index++;
        }

        if (index < value.Length)
        {
            index++;
        }
    }

    // The prefix is an XML NCName, which admits '-' and '.' — `xmlns:app-loc` is a legal
    // spelling Avalonia resolves, and rejecting it would report the key it binds as dead.
    private static readonly Regex StaticMember = new(
        @"^(?:[\w.-]+:)?Strings\.(?<name>\w+)$", RegexOptions.CultureInvariant);

    private static bool IsBuildOutput(string path, string sourceRoot)
    {
        string relative = Path.GetRelativePath(sourceRoot, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "obj" or "bin");
    }
}
