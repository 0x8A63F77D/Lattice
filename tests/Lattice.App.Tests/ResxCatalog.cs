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

    /// <summary>
    /// Every C# and XAML source file of the APP, obj/bin excluded.
    /// <para>
    /// Scoped to the one project that owns the resource type, not to <c>src/</c> at large:
    /// the scan matches on the NAME <c>Strings</c>, so an unrelated <c>Strings.Foo</c> in
    /// Core or GuiRpc would count as a use of an app translation and keep a genuinely dead
    /// key green. Those projects cannot reach <c>Lattice.App.Localization.Strings</c> — the
    /// dependency runs the other way — so nothing live is lost by ignoring them.
    /// </para>
    /// </summary>
    internal static IEnumerable<string> AppSourceFiles()
    {
        string src = Path.Combine(RepositoryRoot, "src", "Lattice.App");
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
        ReferencedNames(path, GlobalImports.Value);

    /// <summary>
    /// As above, with the repository-wide <c>global using</c> imports supplied explicitly.
    /// </summary>
    internal static IEnumerable<string> ReferencedNames(string path, ResourceImports globals)
    {
        string text = File.ReadAllText(path);
        return path.EndsWith(".axaml", StringComparison.Ordinal)
            ? XamlReferences(text)
            : CSharpReferences(text, globals);
    }

    /// <summary>
    /// Ways a file can reach the resource type without naming it: aliases it may use, and
    /// whether the resources are in scope as bare identifiers.
    /// </summary>
    internal readonly record struct ResourceImports(IReadOnlySet<string> Aliases, bool Static)
    {
        internal static ResourceImports None { get; } =
            new(new HashSet<string>(StringComparer.Ordinal), false);
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
    private static IEnumerable<string> CSharpReferences(string text, ResourceImports globals)
    {
        // Imports are collected across ALL branches before any of them is scanned. A
        // `using Text = …Strings;` at the top of the file is invisible from inside a
        // disabled #if fragment (they are parsed separately), and one declared inside a
        // fragment is invisible everywhere else — either way an aliased read would be
        // declared dead. The alias belongs to the FILE, so it is resolved per file.
        IReadOnlyList<SyntaxNode> branches = ParseEveryBranch(text);
        var names = new HashSet<string>(StringComparer.Ordinal) { "Strings" };
        names.UnionWith(globals.Aliases);
        foreach (SyntaxNode branch in branches)
        {
            names.UnionWith(AliasesIn(branch));
        }

        bool staticImport = globals.Static || branches.Any(ImportsResourcesStatically);
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (SyntaxNode branch in branches)
        {
            references.UnionWith(ReferencesIn(branch, names, staticImport));
        }

        return references;
    }

    /// <summary>
    /// The file's syntax, plus the syntax of every region some other configuration would
    /// compile — disabled text re-parsed, recursively, until nothing conditional is left.
    /// </summary>
    private static IReadOnlyList<SyntaxNode> ParseEveryBranch(string text)
    {
        List<SyntaxNode> roots = [];
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
            roots.Add(root);
            foreach (SyntaxTrivia trivia in root.DescendantTrivia()
                .Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
            {
                pending.Enqueue(trivia.ToFullString());
            }
        }

        return roots;
    }

    private static IEnumerable<string> ReferencesIn(
        SyntaxNode root, IReadOnlySet<string> resourceTypeNames, bool staticImport)
    {
        IEnumerable<string> qualified = root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(access => NamesStringsType(access.Expression, resourceTypeNames))
            .Where(access => !IsNameofOperand(access))
            .Select(access => access.Name.Identifier.ValueText);

        if (!staticImport)
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

    private static IEnumerable<string> AliasesIn(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Where(directive => RightmostIdentifier(directive.NamespaceOrType) == "Strings")
            .Select(directive => directive.Alias?.Name.Identifier.ValueText)
            .OfType<string>();

    /// <summary>
    /// <c>global using</c> imports of the resource type, which apply to every file in the
    /// compilation but are DECLARED in one. Files are parsed independently here, so without
    /// this pre-pass a consuming file knows only the literal spelling and both an aliased
    /// read and a bare-identifier read elsewhere would be declared dead.
    /// <para>
    /// A global <c>using static</c> puts every resource in scope everywhere, so it turns
    /// the whole inventory into an over-approximation — the dead-key check would stop
    /// finding anything. That is the safe direction and nothing in this repo does it, but
    /// it IS the price, and it is paid here rather than by declaring live keys dead.
    /// </para>
    /// </summary>
    private static readonly Lazy<ResourceImports> GlobalImports = new(() =>
    {
        UsingDirectiveSyntax[] directives = AppSourceFiles()
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            // Through every branch, exactly as ordinary references are read: a global using
            // under #if DEBUG is disabled trivia in a symbol-free parse, and the file that
            // consumes the alias would then be scanned without it.
            .SelectMany(path => ParseEveryBranch(File.ReadAllText(path))
                .SelectMany(root => root.DescendantNodes().OfType<UsingDirectiveSyntax>()))
            .Where(directive => directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
            .Where(directive => RightmostIdentifier(directive.NamespaceOrType) == "Strings")
            .ToArray();

        return new ResourceImports(
            directives.Select(directive => directive.Alias?.Name.Identifier.ValueText)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal),
            directives.Any(directive => directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)));
    });

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
        return doc.Descendants().SelectMany(element =>
            element.Attributes().Select(attribute => attribute.Value)
                .Concat(element.Nodes().OfType<XText>().Select(node => node.Value))
                .SelectMany(value => MarkupReferences(value, element)));
    }

    private const string XamlLanguageNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Resolves a <c>prefix:Name</c> the way XAML does — through the namespace declarations
    /// in scope at <paramref name="element"/> — and reports whether the prefix denotes
    /// <paramref name="target"/>.
    /// <para>
    /// Prefixes are arbitrary on both halves of a binding, so neither half can be matched
    /// as a spelling. <c>x</c> is only conventional for the XAML language namespace, and on
    /// the member side <c>{x:Static other:Strings.Foo}</c> with <c>other</c> bound to some
    /// library is a different <c>Strings</c> entirely — counting it would keep a dead app
    /// translation green.
    /// </para>
    /// </summary>
    private static bool ResolvesTo(XElement element, string? prefix, string target)
    {
        XNamespace? resolved = prefix is null
            ? element.GetDefaultNamespace()
            : element.GetNamespaceOfPrefix(prefix);
        return resolved is not null && DenotesNamespace(resolved.NamespaceName, target);
    }

    /// <summary>
    /// True when a XAML namespace declaration denotes the given CLR namespace, in either
    /// spelling Avalonia accepts (<c>using:</c>, or <c>clr-namespace:</c> with an optional
    /// assembly), or when it IS the given URI.
    /// </summary>
    private static bool DenotesNamespace(string declared, string target)
    {
        if (declared == target)
        {
            return true;
        }

        string body = declared.StartsWith("using:", StringComparison.Ordinal)
            ? declared["using:".Length..]
            : declared.StartsWith("clr-namespace:", StringComparison.Ordinal)
                ? declared["clr-namespace:".Length..]
                : string.Empty;

        int assembly = body.IndexOf(';');
        return (assembly < 0 ? body : body[..assembly]) == target;
    }

    private static (string? Prefix, string Name) SplitPrefix(string qualified)
    {
        int colon = qualified.IndexOf(':');
        return colon < 0 ? (null, qualified) : (qualified[..colon], qualified[(colon + 1)..]);
    }

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
    internal static IEnumerable<string> MarkupReferences(string value, XElement element)
    {
        string trimmed = value.TrimStart();
        if (!trimmed.StartsWith('{') || trimmed.StartsWith("{}", StringComparison.Ordinal))
        {
            return [];
        }

        IReadOnlyList<MarkupToken> tokens = Tokenize(trimmed);
        List<string> names = [];
        int index = 0;
        if (tokens.Count > 0 && tokens[0].Kind == MarkupTokenKind.Open)
        {
            ParseExtension(tokens, ref index, names, element);
        }

        return names;
    }

    /// <summary>
    /// Splits a markup value into the grammar's atoms — braces, commas, equals signs,
    /// quoted literals and bare text — discarding whitespace.
    /// <para>
    /// Lexing first is what makes the parser whitespace-insensitive by construction.
    /// Deciding "is the next character an equals sign?" straight off the raw string got
    /// this wrong twice: <c>{Binding Converter = {…}}</c> and then
    /// <c>{x:Static Member = loc:Strings.Foo}</c>, where the space before <c>=</c> made a
    /// property name look like a positional argument and lost the reference. Whitespace is
    /// meaningless between atoms here, so it stops existing before the grammar is applied.
    /// </para>
    /// </summary>
    private static IReadOnlyList<MarkupToken> Tokenize(string value)
    {
        List<MarkupToken> tokens = [];
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            switch (c)
            {
                case '{':
                    tokens.Add(new MarkupToken(MarkupTokenKind.Open, "{"));
                    continue;
                case '}':
                    tokens.Add(new MarkupToken(MarkupTokenKind.Close, "}"));
                    continue;
                case ',':
                    tokens.Add(new MarkupToken(MarkupTokenKind.Comma, ","));
                    continue;
                case '=':
                    tokens.Add(new MarkupToken(MarkupTokenKind.Equals, "="));
                    continue;
                case '\'' or '"':
                    tokens.Add(new MarkupToken(MarkupTokenKind.Quoted, ReadQuoted(value, ref i)));
                    i--; // the loop's own increment steps past the closing quote
                    continue;
                default:
                    tokens.Add(new MarkupToken(MarkupTokenKind.Text, ReadToken(value, ref i)));
                    i--; // ReadToken already stopped ON the delimiter
                    continue;
            }
        }

        return tokens;
    }

    private enum MarkupTokenKind
    {
        Open,
        Close,
        Comma,
        Equals,
        Quoted,
        Text,
    }

    private readonly record struct MarkupToken(MarkupTokenKind Kind, string Text);

    /// <summary>
    /// Consumes one <c>{Name arg, Prop=value}</c> item, recursing into nested extensions
    /// and ignoring quoted literals, and records the member of every <c>Static</c>
    /// extension it meets that names a resource.
    /// </summary>
    private static void ParseExtension(
        IReadOnlyList<MarkupToken> tokens, ref int index, ICollection<string> names, XElement element)
    {
        index++; // the opening brace
        string extension = index < tokens.Count && tokens[index].Kind == MarkupTokenKind.Text
            ? tokens[index++].Text
            : string.Empty;
        string? member = null;
        string? property = null;

        while (index < tokens.Count && tokens[index].Kind != MarkupTokenKind.Close)
        {
            MarkupToken token = tokens[index];
            switch (token.Kind)
            {
                case MarkupTokenKind.Open:
                    ParseExtension(tokens, ref index, names, element);
                    property = null;
                    continue;
                case MarkupTokenKind.Comma:
                    property = null;
                    index++;
                    continue;
                case MarkupTokenKind.Equals:
                    index++;
                    continue;
                // Quoted contents are a VALUE — `{x:Static Member='loc:Strings.Foo'}` binds
                // what the bare form binds — but never markup: the anchored member pattern
                // is what keeps `FallbackValue='use {x:Static loc:Strings.Foo} here'` out,
                // since prose cannot match a whole member name end to end.
                case MarkupTokenKind.Quoted or MarkupTokenKind.Text:
                    break;
                case MarkupTokenKind.Close:
                    continue;
            }

            if (index + 1 < tokens.Count && tokens[index + 1].Kind == MarkupTokenKind.Equals)
            {
                property = token.Text;
                index += 2;
                continue;
            }

            // A positional argument, or the value of Member= — a Static extension's
            // positional argument IS its Member property, so `{x:Static Member=loc:Strings.Foo}`
            // binds exactly what `{x:Static loc:Strings.Foo}` does. Any other property's
            // value is not the member.
            if (property is null || property == "Member")
            {
                member ??= token.Text;
            }

            property = null;
            index++;
        }

        if (index < tokens.Count)
        {
            index++; // the closing brace
        }

        if (member is null)
        {
            return;
        }

        (string? extensionPrefix, string extensionName) = SplitPrefix(extension);
        if (extensionName != "Static" || !ResolvesTo(element, extensionPrefix, XamlLanguageNamespace))
        {
            return;
        }

        Match match = StaticMember.Match(member);
        if (match.Success
            && ResolvesTo(element, match.Groups["prefix"].Success ? match.Groups["prefix"].Value : null,
                "Lattice.App.Localization"))
        {
            names.Add(match.Groups["name"].Value);
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

    /// <summary>The contents of a quoted argument, without its quotes.</summary>
    private static string ReadQuoted(string value, ref int index)
    {
        char quote = value[index++];
        int start = index;
        while (index < value.Length && value[index] != quote)
        {
            index++;
        }

        string contents = value[start..index];
        if (index < value.Length)
        {
            index++;
        }

        return contents;
    }

    private static readonly Regex StaticMember = new(
        @"^(?:(?<prefix>[\w.-]+):)?Strings\.(?<name>\w+)$", RegexOptions.CultureInvariant);

    private static bool IsBuildOutput(string path, string sourceRoot)
    {
        string relative = Path.GetRelativePath(sourceRoot, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "obj" or "bin");
    }
}
