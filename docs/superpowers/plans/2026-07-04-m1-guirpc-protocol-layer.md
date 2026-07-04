# M1 `Lattice.Boinc.GuiRpc` Protocol Layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A standalone MIT-licensed .NET library implementing the BOINC GUI RPC protocol for a single host (connect, frame, auth, 5 read-only RPCs), with fixture-based tests and a console smoke test.

**Architecture:** Three internal layers — `RpcConnection` (TCP + `\x03` framing), `BoincGuiRpcClient` (auth state + one async method per RPC), and immutable record models each with a hand-written lenient `Parse(XElement)`. Every reply passes through a central `XmlSanitizer` before `XElement.Parse`.

**Tech Stack:** .NET 10 (`net10.0` single target), xUnit, no runtime dependencies.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-04-m1-guirpc-protocol-layer-design.md` — read it before starting any task.
- Target framework `net10.0` everywhere. Library has ZERO package dependencies.
- License MIT. Package ID `Lattice.Boinc.GuiRpc`. Version `0.1.0`.
- Request XML is built by string concatenation, NEVER via XElement serialization. Self-closing tags must have no space before the slash: `<auth1/>`, never `<auth1 />`.
- All numeric parsing uses `CultureInfo.InvariantCulture`.
- Never branch on error message text — only on structural tags (`<error>`, `<unauthorized/>`, `<authorized/>`).
- Missing XML fields never throw; use type defaults. Unknown elements are ignored. Unknown enum integers are preserved by direct cast.
- Unit tests must not touch the network or require a BOINC daemon.
- Conventional commit messages. Run the full test suite before every commit: `dotnet test` from repo root, expect all green.

---

### Task 1: Solution scaffolding and package metadata

**Files:**
- Create: `Lattice.sln`, `src/Lattice.Boinc.GuiRpc/Lattice.Boinc.GuiRpc.csproj`, `tests/Lattice.Tests/Lattice.Tests.csproj`, `tools/Lattice.SmokeTest/Lattice.SmokeTest.csproj`, `.gitignore`

**Interfaces:**
- Produces: buildable solution; library project with `InternalsVisibleTo("Lattice.Tests")`; test project referencing the library with a `fixtures/` content folder.

- [ ] **Step 1: Verify NuGet ID availability**

Run: `curl -s -o /dev/null -w "%{http_code}" https://api.nuget.org/v3-flatcontainer/lattice.boinc.guirpc/index.json`
Expected: `404` (ID unclaimed). If `200`, STOP and report — the package ID needs renaming, which is a spec change.

- [ ] **Step 2: Create solution and projects**

```bash
dotnet new gitignore
dotnet new sln -n Lattice
dotnet new classlib -o src/Lattice.Boinc.GuiRpc -f net10.0
dotnet new xunit -o tests/Lattice.Tests -f net10.0
dotnet new console -o tools/Lattice.SmokeTest -f net10.0
dotnet sln add src/Lattice.Boinc.GuiRpc tests/Lattice.Tests tools/Lattice.SmokeTest
rm src/Lattice.Boinc.GuiRpc/Class1.cs tests/Lattice.Tests/UnitTest1.cs
```

- [ ] **Step 3: Replace `src/Lattice.Boinc.GuiRpc/Lattice.Boinc.GuiRpc.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Lattice.Boinc.GuiRpc</RootNamespace>
    <PackageId>Lattice.Boinc.GuiRpc</PackageId>
    <Version>0.1.0</Version>
    <Description>Async .NET client for the BOINC GUI RPC protocol. Single-host semantics: connect, authenticate, and read client state over TCP.</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <RepositoryUrl>https://github.com/0x8A63F77D/Lattice</RepositoryUrl>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="/" />
    <InternalsVisibleTo Include="Lattice.Tests" />
    <PackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" PrivateAssets="All" />
  </ItemGroup>
</Project>
```

Create `src/Lattice.Boinc.GuiRpc/README.md`:

```markdown
# Lattice.Boinc.GuiRpc

Async .NET client for the BOINC GUI RPC protocol (the protocol BOINC Manager
uses to talk to the `boinc` core client on TCP port 31416).

Part of [Lattice](https://github.com/0x8A63F77D/Lattice), a multi-host BOINC
dashboard. This package is single-host: one client, one connection, typed
models. Polling, reconnect, and multi-host aggregation live upstream.

API is 0.x and unstable.
```

- [ ] **Step 4: Replace `tests/Lattice.Tests/Lattice.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.0" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Lattice.Boinc.GuiRpc/Lattice.Boinc.GuiRpc.csproj" />
    <Content Include="fixtures/**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

Create empty dir marker `tests/Lattice.Tests/fixtures/.gitkeep`. In `tools/Lattice.SmokeTest/Lattice.SmokeTest.csproj` add inside its `<Project>`:

```xml
  <ItemGroup>
    <ProjectReference Include="../../src/Lattice.Boinc.GuiRpc/Lattice.Boinc.GuiRpc.csproj" />
  </ItemGroup>
```

and `<IsPackable>false</IsPackable>` in its `<PropertyGroup>`.

- [ ] **Step 5: Build and commit**

Run: `dotnet build`
Expected: `Build succeeded` (0 warnings acceptable threshold: SourceLink may warn if remote missing — acceptable).

```bash
git add -A
git commit -m "chore: scaffold solution, library, tests, and smoke test projects"
```

---

### Task 2: Exception types and XML sanitizer

**Files:**
- Create: `src/Lattice.Boinc.GuiRpc/Exceptions.cs`, `src/Lattice.Boinc.GuiRpc/XmlSanitizer.cs`
- Test: `tests/Lattice.Tests/XmlSanitizerTests.cs`, `tests/Lattice.Tests/fixtures/malformed_iso_decl.xml`

**Interfaces:**
- Produces: `BoincConnectionException`, `BoincProtocolException(string message, string rawPayload, Exception? inner)` with `RawPayload` property, `BoincUnauthorizedException`, `BoincRpcException(string errorText)` with `ErrorText` property; `internal static string XmlSanitizer.Sanitize(string raw)`.

- [ ] **Step 1: Write failing tests**

`tests/Lattice.Tests/fixtures/malformed_iso_decl.xml` (verbatim, no trailing newline needed):

```xml
<boinc_gui_rpc_reply>
<?xml version="1.0" encoding="ISO-8859-1" ?>
<projects_and_account_managers/>
</boinc_gui_rpc_reply>
```

`tests/Lattice.Tests/XmlSanitizerTests.cs`:

```csharp
using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class XmlSanitizerTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void Strips_illegal_iso_declaration()
    {
        string sanitized = XmlSanitizer.Sanitize(Fixture("malformed_iso_decl.xml"));
        XElement parsed = XElement.Parse(sanitized); // must not throw
        Assert.Equal("boinc_gui_rpc_reply", parsed.Name.LocalName);
    }

    [Fact]
    public void Strips_control_characters_but_keeps_legal_whitespace()
    {
        string sanitized = XmlSanitizer.Sanitize("<a>\x01hi\x1F\t\r\n</a>");
        Assert.Equal("<a>hi\t\r\n</a>", sanitized);
    }

    [Fact]
    public void Escapes_bare_ampersand()
    {
        string sanitized = XmlSanitizer.Sanitize("<a>Miles & More</a>");
        Assert.Equal("<a>Miles &amp; More</a>", sanitized);
    }

    [Theory]
    [InlineData("<a>a &amp; b</a>")]
    [InlineData("<a>&lt;tag&gt;</a>")]
    [InlineData("<a>&#38;</a>")]
    [InlineData("<a>&#x26;</a>")]
    public void Leaves_valid_entities_untouched(string xml)
    {
        Assert.Equal(xml, XmlSanitizer.Sanitize(xml));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~XmlSanitizerTests"`
Expected: compile FAILURE — `XmlSanitizer` does not exist.

- [ ] **Step 3: Implement**

`src/Lattice.Boinc.GuiRpc/Exceptions.cs`:

```csharp
namespace Lattice.Boinc.GuiRpc;

/// <summary>The TCP connection failed or was closed. The connection is dead; reconnect.</summary>
public class BoincConnectionException : Exception
{
    public BoincConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A reply could not be parsed even after sanitizing. Carries the raw payload for diagnosis.</summary>
public class BoincProtocolException : Exception
{
    public string RawPayload { get; }

    public BoincProtocolException(string message, string rawPayload, Exception? inner = null)
        : base(message, inner) => RawPayload = rawPayload;
}

/// <summary>The daemon returned &lt;unauthorized/&gt;. Re-run AuthorizeAsync.</summary>
public class BoincUnauthorizedException : Exception
{
    public BoincUnauthorizedException() : base("The BOINC client rejected the request as unauthorized.") { }
}

/// <summary>The daemon returned an &lt;error&gt; tag. ErrorText is for display only — never branch on it.</summary>
public class BoincRpcException : Exception
{
    public string ErrorText { get; }

    public BoincRpcException(string errorText)
        : base($"The BOINC client returned an error: {errorText}") => ErrorText = errorText;
}
```

`src/Lattice.Boinc.GuiRpc/XmlSanitizer.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace Lattice.Boinc.GuiRpc;

internal static partial class XmlSanitizer
{
    internal static string Sanitize(string raw)
    {
        // Some RPCs emit an illegal mid-document encoding declaration (BOINC bug #1509).
        raw = raw.Replace("<?xml version=\"1.0\" encoding=\"ISO-8859-1\" ?>", string.Empty);

        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
            if (c == '\t' || c == '\n' || c == '\r' || c >= '\x20')
                sb.Append(c);

        return BareAmpersand().Replace(sb.ToString(), "&amp;");
    }

    [GeneratedRegex(@"&(?!(?:[A-Za-z][A-Za-z0-9]*|#[0-9]+|#x[0-9A-Fa-f]+);)")]
    private static partial Regex BareAmpersand();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~XmlSanitizerTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add exception types and lenient XML sanitizer"
```

---

### Task 3: RpcConnection — TCP framing

**Files:**
- Create: `src/Lattice.Boinc.GuiRpc/RpcConnection.cs`
- Test: `tests/Lattice.Tests/ScriptedStream.cs`, `tests/Lattice.Tests/RpcConnectionTests.cs`

**Interfaces:**
- Consumes: `BoincConnectionException` (Task 2).
- Produces: `internal sealed class RpcConnection : IAsyncDisposable` with `internal RpcConnection(Stream stream)` (test seam), `internal static Task<RpcConnection> ConnectAsync(string host, int port, CancellationToken ct)`, `internal Task<string> PerformRpcAsync(string requestBody, CancellationToken ct)` — wraps body in `<boinc_gui_rpc_request>` envelope + `\x03`, returns reply text without terminator. Also `internal sealed class ScriptedStream : Stream` in tests with `static ScriptedStream FromReplies(params string[] replies)` and `MemoryStream Written` — reused by Task 8.

- [ ] **Step 1: Write test helper and failing tests**

`tests/Lattice.Tests/ScriptedStream.cs`:

```csharp
using System.Text;

namespace Lattice.Tests;

/// <summary>In-memory duplex stream: Read serves scripted chunks in order; Write records into Written.</summary>
internal sealed class ScriptedStream : Stream
{
    private readonly Queue<byte[]> chunks;
    private byte[]? current;
    private int pos;

    public MemoryStream Written { get; } = new();

    public ScriptedStream(params byte[][] chunks) => this.chunks = new Queue<byte[]>(chunks);

    /// <summary>One chunk per reply, each with the 0x03 terminator appended.</summary>
    public static ScriptedStream FromReplies(params string[] replies) =>
        new(replies.Select(r => Encoding.UTF8.GetBytes(r + "\x03")).ToArray());

    public override int Read(Span<byte> buffer)
    {
        if (current is null || pos >= current.Length)
        {
            if (!chunks.TryDequeue(out current)) return 0;
            pos = 0;
        }
        int n = Math.Min(buffer.Length, current.Length - pos);
        current.AsSpan(pos, n).CopyTo(buffer);
        pos += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        new(Read(buffer.Span));

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) => Written.Write(buffer);
    public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    { Written.Write(buffer.Span); return ValueTask.CompletedTask; }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
```

`tests/Lattice.Tests/RpcConnectionTests.cs`:

```csharp
using System.Text;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class RpcConnectionTests
{
    [Fact]
    public async Task Wraps_request_in_envelope_with_terminator()
    {
        var stream = ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<a/>\n</boinc_gui_rpc_reply>");
        await using var conn = new RpcConnection(stream);

        await conn.PerformRpcAsync("<auth1/>", CancellationToken.None);

        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Equal("<boinc_gui_rpc_request>\n<auth1/>\n</boinc_gui_rpc_request>\n\x03", sent);
    }

    [Fact]
    public async Task Returns_reply_without_terminator()
    {
        var stream = ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<a/>\n</boinc_gui_rpc_reply>");
        await using var conn = new RpcConnection(stream);

        string reply = await conn.PerformRpcAsync("<auth1/>", CancellationToken.None);

        Assert.Equal("<boinc_gui_rpc_reply>\n<a/>\n</boinc_gui_rpc_reply>", reply);
        Assert.DoesNotContain('\x03', reply);
    }

    [Fact]
    public async Task Accumulates_fragmented_reply_until_terminator()
    {
        byte[] whole = Encoding.UTF8.GetBytes("<boinc_gui_rpc_reply><big>payload</big></boinc_gui_rpc_reply>\x03");
        var stream = new ScriptedStream(whole[..10], whole[10..25], whole[25..]);
        await using var conn = new RpcConnection(stream);

        string reply = await conn.PerformRpcAsync("<get_state/>", CancellationToken.None);

        Assert.Equal("<boinc_gui_rpc_reply><big>payload</big></boinc_gui_rpc_reply>", reply);
    }

    [Fact]
    public async Task Stream_ending_before_terminator_throws_connection_exception()
    {
        byte[] truncated = Encoding.UTF8.GetBytes("<boinc_gui_rpc_reply><never_terminated>");
        var stream = new ScriptedStream(truncated);
        await using var conn = new RpcConnection(stream);

        await Assert.ThrowsAsync<BoincConnectionException>(
            () => conn.PerformRpcAsync("<get_state/>", CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_utf8_bytes_do_not_throw()
    {
        byte[] reply = [.. Encoding.UTF8.GetBytes("<a>"), 0xFF, 0xFE, .. Encoding.UTF8.GetBytes("</a>"), 0x03];
        var stream = new ScriptedStream(reply);
        await using var conn = new RpcConnection(stream);

        string text = await conn.PerformRpcAsync("<x/>", CancellationToken.None);

        Assert.StartsWith("<a>", text);
        Assert.EndsWith("</a>", text);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RpcConnectionTests"`
Expected: compile FAILURE — `RpcConnection` does not exist.

- [ ] **Step 3: Implement `src/Lattice.Boinc.GuiRpc/RpcConnection.cs`**

```csharp
using System.Net.Sockets;
using System.Text;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Owns the socket and the \x03 framing. Knows no RPC semantics.</summary>
internal sealed class RpcConnection : IAsyncDisposable
{
    private const byte Terminator = 0x03;

    private static readonly Encoding LenientUtf8 = Encoding.GetEncoding(
        "utf-8", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);

    private readonly Stream stream;
    private readonly TcpClient? tcpClient;

    internal RpcConnection(Stream stream) => this.stream = stream;

    private RpcConnection(TcpClient tcpClient) : this(tcpClient.GetStream()) => this.tcpClient = tcpClient;

    internal static async Task<RpcConnection> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            client.Dispose();
            throw new BoincConnectionException($"Failed to connect to {host}:{port}.", ex);
        }
        return new RpcConnection(client);
    }

    internal async Task<string> PerformRpcAsync(string requestBody, CancellationToken ct)
    {
        string request = "<boinc_gui_rpc_request>\n" + requestBody + "\n</boinc_gui_rpc_request>\n\x03";
        byte[] sendBuffer = Encoding.ASCII.GetBytes(request);
        try
        {
            await stream.WriteAsync(sendBuffer, ct).ConfigureAwait(false);

            using var reply = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new BoincConnectionException("Connection closed before the reply terminator arrived.");

                int terminator = Array.IndexOf(buffer, Terminator, 0, read);
                if (terminator >= 0)
                {
                    reply.Write(buffer, 0, terminator);
                    break;
                }
                reply.Write(buffer, 0, read);
            }
            return LenientUtf8.GetString(reply.ToArray());
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new BoincConnectionException("Connection failed during RPC.", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        tcpClient?.Dispose();
        return stream.DisposeAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RpcConnectionTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add RpcConnection with 0x03 framing over TCP"
```

---

### Task 4: Parse helpers, reply parser, VersionInfo

**Files:**
- Create: `src/Lattice.Boinc.GuiRpc/ParseHelpers.cs`, `src/Lattice.Boinc.GuiRpc/RpcReplyParser.cs`, `src/Lattice.Boinc.GuiRpc/Models/VersionInfo.cs`
- Test: `tests/Lattice.Tests/ParseHelpersTests.cs`, `tests/Lattice.Tests/RpcReplyParserTests.cs`

**Interfaces:**
- Consumes: `XmlSanitizer.Sanitize`, exception types (Task 2).
- Produces:
  - `internal static class ParseHelpers`: `string GetString(XElement parent, string name)` (missing → `""`), `bool GetBool(XElement parent, string name)` (missing → false; empty tag / `1` / `true` → true; `0` / `false` → false), `int GetInt(XElement parent, string name, int defaultValue = 0)`, `double GetDouble(XElement parent, string name, double defaultValue = 0)`, `DateTimeOffset? GetTimestamp(XElement parent, string name)` (epoch-seconds double; missing/≤0 → null).
  - `internal static class RpcReplyParser`: `XElement Parse(string raw, bool throwOnUnauthorized = true)` — sanitize → `XElement.Parse` → throws `BoincProtocolException` (malformed), `BoincUnauthorizedException` (`<unauthorized/>` present, unless suppressed), `BoincRpcException` (`<error>` present). Returns the `<boinc_gui_rpc_reply>` element.
  - `public sealed record VersionInfo(int Major, int Minor, int Release)` with `internal static VersionInfo Parse(XElement e)` and `public override string ToString()` → `"8.0.4"`.

- [ ] **Step 1: Write failing tests**

`tests/Lattice.Tests/ParseHelpersTests.cs`:

```csharp
using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class ParseHelpersTests
{
    private static XElement El(string xml) => XElement.Parse(xml);

    [Fact]
    public void GetString_missing_returns_empty() =>
        Assert.Equal("", ParseHelpers.GetString(El("<r/>"), "name"));

    [Fact]
    public void GetString_present_returns_value() =>
        Assert.Equal("boinc", ParseHelpers.GetString(El("<r><name>boinc</name></r>"), "name"));

    [Theory]
    [InlineData("<r/>", false)]                       // missing
    [InlineData("<r><f/></r>", true)]                 // empty tag = true (BOINC dialect)
    [InlineData("<r><f>1</f></r>", true)]
    [InlineData("<r><f>true</f></r>", true)]
    [InlineData("<r><f>0</f></r>", false)]
    [InlineData("<r><f>false</f></r>", false)]
    public void GetBool_covers_boinc_dialect(string xml, bool expected) =>
        Assert.Equal(expected, ParseHelpers.GetBool(El(xml), "f"));

    [Fact]
    public void GetInt_missing_returns_default() =>
        Assert.Equal(7, ParseHelpers.GetInt(El("<r/>"), "n", 7));

    [Fact]
    public void GetDouble_ignores_culture() =>
        Assert.Equal(1.5, ParseHelpers.GetDouble(El("<r><n>1.5</n></r>"), "n"));

    [Fact]
    public void GetTimestamp_converts_epoch_seconds()
    {
        DateTimeOffset? t = ParseHelpers.GetTimestamp(El("<r><time>1751600000.0</time></r>"), "time");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1751600000), t);
    }

    [Theory]
    [InlineData("<r/>")]
    [InlineData("<r><time>0</time></r>")]
    public void GetTimestamp_missing_or_zero_returns_null(string xml) =>
        Assert.Null(ParseHelpers.GetTimestamp(El(xml), "time"));
}
```

`tests/Lattice.Tests/RpcReplyParserTests.cs`:

```csharp
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class RpcReplyParserTests
{
    [Fact]
    public void Returns_reply_element_for_valid_reply()
    {
        var reply = RpcReplyParser.Parse("<boinc_gui_rpc_reply>\n<nonce>123</nonce>\n</boinc_gui_rpc_reply>");
        Assert.Equal("123", ParseHelpers.GetString(reply, "nonce"));
    }

    [Fact]
    public void Malformed_reply_throws_protocol_exception_with_payload()
    {
        var ex = Assert.Throws<BoincProtocolException>(() => RpcReplyParser.Parse("<boinc_gui_rpc_reply><broken"));
        Assert.Contains("<broken", ex.RawPayload);
    }

    [Fact]
    public void Unauthorized_throws_by_default()
    {
        Assert.Throws<BoincUnauthorizedException>(
            () => RpcReplyParser.Parse("<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>"));
    }

    [Fact]
    public void Unauthorized_suppressed_for_auth_flow()
    {
        var reply = RpcReplyParser.Parse(
            "<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>", throwOnUnauthorized: false);
        Assert.NotNull(reply.Element("unauthorized"));
    }

    [Fact]
    public void Error_tag_throws_rpc_exception_with_text()
    {
        var ex = Assert.Throws<BoincRpcException>(
            () => RpcReplyParser.Parse("<boinc_gui_rpc_reply>\n<error>unrecognized op</error>\n</boinc_gui_rpc_reply>"));
        Assert.Equal("unrecognized op", ex.ErrorText);
    }

    [Fact]
    public void Reply_with_unescaped_ampersand_still_parses()
    {
        var reply = RpcReplyParser.Parse("<boinc_gui_rpc_reply>\n<name>Miles & More</name>\n</boinc_gui_rpc_reply>");
        Assert.Equal("Miles & More", ParseHelpers.GetString(reply, "name"));
    }
}
```

Also add to `tests/Lattice.Tests/ParseHelpersTests.cs` (bottom of namespace) a `VersionInfo` test class:

```csharp
public class VersionInfoTests
{
    [Fact]
    public void Parses_and_formats()
    {
        var e = XElement.Parse("<server_version><major>8</major><minor>0</minor><release>4</release></server_version>");
        var v = VersionInfo.Parse(e);
        Assert.Equal(new VersionInfo(8, 0, 4), v);
        Assert.Equal("8.0.4", v.ToString());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ParseHelpersTests|FullyQualifiedName~RpcReplyParserTests|FullyQualifiedName~VersionInfoTests"`
Expected: compile FAILURE.

- [ ] **Step 3: Implement**

`src/Lattice.Boinc.GuiRpc/ParseHelpers.cs`:

```csharp
using System.Globalization;
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

internal static class ParseHelpers
{
    internal static string GetString(XElement parent, string name) =>
        (string?)parent.Element(name) ?? string.Empty;

    internal static bool GetBool(XElement parent, string name)
    {
        XElement? e = parent.Element(name);
        if (e is null) return false;
        string v = ((string)e).Trim();
        return v.Length == 0 || v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    internal static int GetInt(XElement parent, string name, int defaultValue = 0) =>
        int.TryParse((string?)parent.Element(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : defaultValue;

    internal static double GetDouble(XElement parent, string name, double defaultValue = 0) =>
        double.TryParse((string?)parent.Element(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : defaultValue;

    internal static DateTimeOffset? GetTimestamp(XElement parent, string name)
    {
        double seconds = GetDouble(parent, name, double.NaN);
        if (double.IsNaN(seconds) || seconds <= 0) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
    }
}
```

`src/Lattice.Boinc.GuiRpc/RpcReplyParser.cs`:

```csharp
using System.Xml;
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

internal static class RpcReplyParser
{
    internal static XElement Parse(string raw, bool throwOnUnauthorized = true)
    {
        XElement reply;
        try
        {
            reply = XElement.Parse(XmlSanitizer.Sanitize(raw), LoadOptions.None);
        }
        catch (XmlException ex)
        {
            string snippet = raw.Length <= 2000 ? raw : raw[..2000];
            throw new BoincProtocolException("RPC reply is not parseable XML.", snippet, ex);
        }

        if (throwOnUnauthorized && reply.Element("unauthorized") is not null)
            throw new BoincUnauthorizedException();
        if (reply.Element("error") is XElement error)
            throw new BoincRpcException(((string)error).Trim());

        return reply;
    }
}
```

`src/Lattice.Boinc.GuiRpc/Models/VersionInfo.cs`:

```csharp
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>BOINC core client version, from exchange_versions or get_state.</summary>
public sealed record VersionInfo(int Major, int Minor, int Release)
{
    internal static VersionInfo Parse(XElement e) => new(
        ParseHelpers.GetInt(e, "major"),
        ParseHelpers.GetInt(e, "minor"),
        ParseHelpers.GetInt(e, "release"));

    public override string ToString() => $"{Major}.{Minor}.{Release}";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ParseHelpersTests|FullyQualifiedName~RpcReplyParserTests|FullyQualifiedName~VersionInfoTests"`
Expected: PASS (16 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add parse helpers, reply parser, and VersionInfo model"
```

---

### Task 5: Enums, CcStatus, Message models

**Files:**
- Create: `src/Lattice.Boinc.GuiRpc/Models/Enums.cs`, `src/Lattice.Boinc.GuiRpc/Models/CcStatus.cs`, `src/Lattice.Boinc.GuiRpc/Models/Message.cs`
- Test: `tests/Lattice.Tests/CcStatusTests.cs`, `tests/Lattice.Tests/MessageTests.cs`, `tests/Lattice.Tests/fixtures/get_cc_status.xml`, `tests/Lattice.Tests/fixtures/get_messages.xml`

**Interfaces:**
- Consumes: `ParseHelpers` (Task 4).
- Produces:
  - Enums (integer values from BOINC `lib/common_defs.h`): `RunMode { Always = 1, Auto = 2, Never = 3, Restore = 4 }`, `SuspendReason { NotSuspended = 0, Batteries = 1, UserActive = 2, UserRequest = 4, TimeOfDay = 8, Benchmarks = 16, DiskSize = 32, CpuThrottle = 64, NoRecentInput = 128, InitialDelay = 256, ExclusiveAppRunning = 512, CpuUsage = 1024, NetworkQuotaExceeded = 2048, Os = 4096, WifiState = 4097, BatteryCharging = 4098, BatteryOverheated = 4099, NoGuiKeepalive = 4100 }`, `MessagePriority { Info = 1, UserAlert = 2, InternalError = 3 }`, `ResultState { New = 0, FilesDownloading = 1, FilesDownloaded = 2, ComputeError = 3, FilesUploading = 4, FilesUploaded = 5, Aborted = 6, UploadFailed = 7 }`.
  - `public sealed record CcStatus(RunMode TaskMode, RunMode GpuMode, RunMode NetworkMode, SuspendReason TaskSuspendReason, SuspendReason GpuSuspendReason, SuspendReason NetworkSuspendReason)` + `internal static CcStatus Parse(XElement e)` taking the `<cc_status>` element.
  - `public sealed record Message(string Project, MessagePriority Priority, int Seqno, DateTimeOffset? Timestamp, string Body)` + `internal static Message Parse(XElement e)` taking a `<msg>` element.

- [ ] **Step 1: Write fixtures and failing tests**

`tests/Lattice.Tests/fixtures/get_cc_status.xml`:

```xml
<boinc_gui_rpc_reply>
<cc_status>
   <network_status>0</network_status>
   <ams_password_error>0</ams_password_error>
   <task_suspend_reason>4</task_suspend_reason>
   <task_mode>2</task_mode>
   <task_mode_perm>2</task_mode_perm>
   <task_mode_delay>0.000000</task_mode_delay>
   <gpu_suspend_reason>0</gpu_suspend_reason>
   <gpu_mode>1</gpu_mode>
   <gpu_mode_perm>1</gpu_mode_perm>
   <gpu_mode_delay>0.000000</gpu_mode_delay>
   <network_suspend_reason>0</network_suspend_reason>
   <network_mode>3</network_mode>
   <network_mode_perm>2</network_mode_perm>
   <network_mode_delay>0.000000</network_mode_delay>
   <disallow_attach>0</disallow_attach>
   <simple_gui_only>0</simple_gui_only>
   <max_event_log_lines>2000</max_event_log_lines>
</cc_status>
</boinc_gui_rpc_reply>
```

`tests/Lattice.Tests/fixtures/get_messages.xml`:

```xml
<boinc_gui_rpc_reply>
<msgs>
<msg>
 <project></project>
 <pri>1</pri>
 <seqno>1</seqno>
 <body>
Starting BOINC client version 8.0.4 for x86_64-pc-linux-gnu
</body>
 <time>1751600000.000000</time>
</msg>
<msg>
 <project>Einstein@Home</project>
 <pri>2</pri>
 <seqno>2</seqno>
 <body>
Task h1_0437.60 exited with zero status but no 'finished' file
</body>
 <time>1751600100.000000</time>
</msg>
</msgs>
</boinc_gui_rpc_reply>
```

`tests/Lattice.Tests/CcStatusTests.cs`:

```csharp
using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class CcStatusTests
{
    private static XElement Reply(string name) => XElement.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));

    [Fact]
    public void Parses_modes_and_suspend_reasons()
    {
        var status = CcStatus.Parse(Reply("get_cc_status.xml").Element("cc_status")!);

        Assert.Equal(RunMode.Auto, status.TaskMode);
        Assert.Equal(RunMode.Always, status.GpuMode);
        Assert.Equal(RunMode.Never, status.NetworkMode);
        Assert.Equal(SuspendReason.UserRequest, status.TaskSuspendReason);
        Assert.Equal(SuspendReason.NotSuspended, status.GpuSuspendReason);
        Assert.Equal(SuspendReason.NotSuspended, status.NetworkSuspendReason);
    }

    [Fact]
    public void Unknown_enum_integer_is_preserved()
    {
        var e = XElement.Parse("<cc_status><task_mode>99</task_mode></cc_status>");
        var status = CcStatus.Parse(e);
        Assert.Equal(99, (int)status.TaskMode);
    }

    [Fact]
    public void Missing_fields_default_to_zero_values()
    {
        var status = CcStatus.Parse(XElement.Parse("<cc_status/>"));
        Assert.Equal((RunMode)0, status.TaskMode);
        Assert.Equal(SuspendReason.NotSuspended, status.TaskSuspendReason);
    }
}
```

`tests/Lattice.Tests/MessageTests.cs`:

```csharp
using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class MessageTests
{
    [Fact]
    public void Parses_message_list_fixture()
    {
        var reply = XElement.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "get_messages.xml")));
        var messages = reply.Element("msgs")!.Elements("msg").Select(Message.Parse).ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal("", messages[0].Project);
        Assert.Equal(MessagePriority.Info, messages[0].Priority);
        Assert.Equal(1, messages[0].Seqno);
        Assert.Contains("Starting BOINC client", messages[0].Body);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1751600000), messages[0].Timestamp);

        Assert.Equal("Einstein@Home", messages[1].Project);
        Assert.Equal(MessagePriority.UserAlert, messages[1].Priority);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CcStatusTests|FullyQualifiedName~MessageTests"`
Expected: compile FAILURE.

- [ ] **Step 3: Implement**

`src/Lattice.Boinc.GuiRpc/Models/Enums.cs`:

```csharp
namespace Lattice.Boinc.GuiRpc;

// Integer values mirror BOINC lib/common_defs.h. Unknown values from newer
// daemons are preserved by direct cast — never assume these lists are complete.

public enum RunMode { Always = 1, Auto = 2, Never = 3, Restore = 4 }

public enum SuspendReason
{
    NotSuspended = 0, Batteries = 1, UserActive = 2, UserRequest = 4, TimeOfDay = 8,
    Benchmarks = 16, DiskSize = 32, CpuThrottle = 64, NoRecentInput = 128,
    InitialDelay = 256, ExclusiveAppRunning = 512, CpuUsage = 1024,
    NetworkQuotaExceeded = 2048, Os = 4096, WifiState = 4097,
    BatteryCharging = 4098, BatteryOverheated = 4099, NoGuiKeepalive = 4100,
}

public enum MessagePriority { Info = 1, UserAlert = 2, InternalError = 3 }

public enum ResultState
{
    New = 0, FilesDownloading = 1, FilesDownloaded = 2, ComputeError = 3,
    FilesUploading = 4, FilesUploaded = 5, Aborted = 6, UploadFailed = 7,
}
```

`src/Lattice.Boinc.GuiRpc/Models/CcStatus.cs`:

```csharp
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Cheap steady-state poll: run modes and suspend reasons (get_cc_status).</summary>
public sealed record CcStatus(
    RunMode TaskMode,
    RunMode GpuMode,
    RunMode NetworkMode,
    SuspendReason TaskSuspendReason,
    SuspendReason GpuSuspendReason,
    SuspendReason NetworkSuspendReason)
{
    internal static CcStatus Parse(XElement e) => new(
        (RunMode)ParseHelpers.GetInt(e, "task_mode"),
        (RunMode)ParseHelpers.GetInt(e, "gpu_mode"),
        (RunMode)ParseHelpers.GetInt(e, "network_mode"),
        (SuspendReason)ParseHelpers.GetInt(e, "task_suspend_reason"),
        (SuspendReason)ParseHelpers.GetInt(e, "gpu_suspend_reason"),
        (SuspendReason)ParseHelpers.GetInt(e, "network_suspend_reason"));
}
```

`src/Lattice.Boinc.GuiRpc/Models/Message.cs`:

```csharp
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>One event-log message (get_messages). Seqno is monotonic per daemon.</summary>
public sealed record Message(
    string Project,
    MessagePriority Priority,
    int Seqno,
    DateTimeOffset? Timestamp,
    string Body)
{
    internal static Message Parse(XElement e) => new(
        ParseHelpers.GetString(e, "project"),
        (MessagePriority)ParseHelpers.GetInt(e, "pri"),
        ParseHelpers.GetInt(e, "seqno"),
        ParseHelpers.GetTimestamp(e, "time"),
        ParseHelpers.GetString(e, "body").Trim());
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CcStatusTests|FullyQualifiedName~MessageTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add enums, CcStatus, and Message models with fixtures"
```

---

### Task 6: Result and ActiveTask models

**Files:**
- Create: `src/Lattice.Boinc.GuiRpc/Models/Result.cs`
- Test: `tests/Lattice.Tests/ResultTests.cs`, `tests/Lattice.Tests/fixtures/get_results.xml`

**Interfaces:**
- Consumes: `ParseHelpers`, `ResultState` (Tasks 4–5).
- Produces: `public sealed record ActiveTask(int ActiveTaskState, double FractionDone, double CurrentCpuTime, double ElapsedTime)`; `public sealed record Result(string Name, string WorkunitName, string ProjectUrl, ResultState State, DateTimeOffset? ReportDeadline, bool ReadyToReport, bool SuspendedViaGui, double FinalCpuTime, int ExitStatus, ActiveTask? ActiveTask)` — both with `internal static ... Parse(XElement e)`.

- [ ] **Step 1: Write fixture and failing tests**

`tests/Lattice.Tests/fixtures/get_results.xml`:

```xml
<boinc_gui_rpc_reply>
<results>
<result>
    <name>h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19_1</name>
    <wu_name>h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19</wu_name>
    <platform>x86_64-pc-linux-gnu</platform>
    <version_num>218</version_num>
    <project_url>https://einsteinathome.org/</project_url>
    <final_cpu_time>0.000000</final_cpu_time>
    <final_elapsed_time>0.000000</final_elapsed_time>
    <exit_status>0</exit_status>
    <state>2</state>
    <report_deadline>1752800000.000000</report_deadline>
    <received_time>1751590000.000000</received_time>
    <estimated_cpu_time_remaining>19000.000000</estimated_cpu_time_remaining>
    <active_task>
        <active_task_state>1</active_task_state>
        <app_version_num>218</app_version_num>
        <slot>0</slot>
        <pid>12345</pid>
        <scheduler_state>2</scheduler_state>
        <checkpoint_cpu_time>3600.000000</checkpoint_cpu_time>
        <fraction_done>0.421000</fraction_done>
        <current_cpu_time>3700.000000</current_cpu_time>
        <elapsed_time>3800.000000</elapsed_time>
        <swap_size>250000000.000000</swap_size>
        <working_set_size>200000000.000000</working_set_size>
    </active_task>
</result>
<result>
    <name>wu_finished_1</name>
    <wu_name>wu_finished</wu_name>
    <project_url>https://www.worldcommunitygrid.org/</project_url>
    <final_cpu_time>12000.500000</final_cpu_time>
    <exit_status>0</exit_status>
    <state>5</state>
    <report_deadline>1752900000.000000</report_deadline>
    <ready_to_report/>
    <suspended_via_gui/>
</result>
</results>
</boinc_gui_rpc_reply>
```

`tests/Lattice.Tests/ResultTests.cs`:

```csharp
using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class ResultTests
{
    private static List<Result> Load()
    {
        var reply = XElement.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "get_results.xml")));
        return reply.Element("results")!.Elements("result").Select(Result.Parse).ToList();
    }

    [Fact]
    public void Parses_running_task_with_active_task()
    {
        Result r = Load()[0];
        Assert.Equal("h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19_1", r.Name);
        Assert.Equal("h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19", r.WorkunitName);
        Assert.Equal("https://einsteinathome.org/", r.ProjectUrl);
        Assert.Equal(ResultState.FilesDownloaded, r.State);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1752800000), r.ReportDeadline);
        Assert.False(r.ReadyToReport);
        Assert.False(r.SuspendedViaGui);
        Assert.NotNull(r.ActiveTask);
        Assert.Equal(0.421, r.ActiveTask!.FractionDone, precision: 6);
        Assert.Equal(3800.0, r.ActiveTask.ElapsedTime);
    }

    [Fact]
    public void Parses_finished_task_without_active_task()
    {
        Result r = Load()[1];
        Assert.Equal(ResultState.FilesUploaded, r.State);
        Assert.True(r.ReadyToReport);
        Assert.True(r.SuspendedViaGui);
        Assert.Null(r.ActiveTask);
        Assert.Equal(12000.5, r.FinalCpuTime);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ResultTests"`
Expected: compile FAILURE.

- [ ] **Step 3: Implement `src/Lattice.Boinc.GuiRpc/Models/Result.cs`**

```csharp
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Live execution state of a running task, nested inside a result.</summary>
public sealed record ActiveTask(
    int ActiveTaskState,
    double FractionDone,
    double CurrentCpuTime,
    double ElapsedTime)
{
    internal static ActiveTask Parse(XElement e) => new(
        ParseHelpers.GetInt(e, "active_task_state"),
        ParseHelpers.GetDouble(e, "fraction_done"),
        ParseHelpers.GetDouble(e, "current_cpu_time"),
        ParseHelpers.GetDouble(e, "elapsed_time"));
}

/// <summary>A task instance ("result" in BOINC vocabulary), from get_results or get_state.</summary>
public sealed record Result(
    string Name,
    string WorkunitName,
    string ProjectUrl,
    ResultState State,
    DateTimeOffset? ReportDeadline,
    bool ReadyToReport,
    bool SuspendedViaGui,
    double FinalCpuTime,
    int ExitStatus,
    ActiveTask? ActiveTask)
{
    internal static Result Parse(XElement e) => new(
        ParseHelpers.GetString(e, "name"),
        ParseHelpers.GetString(e, "wu_name"),
        ParseHelpers.GetString(e, "project_url"),
        (ResultState)ParseHelpers.GetInt(e, "state"),
        ParseHelpers.GetTimestamp(e, "report_deadline"),
        ParseHelpers.GetBool(e, "ready_to_report"),
        ParseHelpers.GetBool(e, "suspended_via_gui"),
        ParseHelpers.GetDouble(e, "final_cpu_time"),
        ParseHelpers.GetInt(e, "exit_status"),
        e.Element("active_task") is XElement at ? ActiveTask.Parse(at) : null);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ResultTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add Result and ActiveTask models with fixture"
```

---

### Task 7: Project, App, AppVersion, Workunit, HostInfo, CcState models

**Files:**
- Create: `src/Lattice.Boinc.GuiRpc/Models/Project.cs`, `src/Lattice.Boinc.GuiRpc/Models/App.cs`, `src/Lattice.Boinc.GuiRpc/Models/Workunit.cs`, `src/Lattice.Boinc.GuiRpc/Models/HostInfo.cs`, `src/Lattice.Boinc.GuiRpc/Models/CcState.cs`
- Test: `tests/Lattice.Tests/CcStateTests.cs`, `tests/Lattice.Tests/fixtures/get_state.xml`

**Interfaces:**
- Consumes: `ParseHelpers`, `Result`, `VersionInfo` (Tasks 4–6).
- Produces (all with `internal static ... Parse(XElement e)`):
  - `public sealed record Project(string MasterUrl, string ProjectName, double UserTotalCredit, double UserExpavgCredit, double HostTotalCredit, double HostExpavgCredit, bool SuspendedViaGui, bool DontRequestMoreWork)`
  - `public sealed record App(string Name, string UserFriendlyName)`
  - `public sealed record AppVersion(string AppName, int VersionNum, string Platform, string PlanClass)` (in `App.cs`)
  - `public sealed record Workunit(string Name, string AppName, double RscFpopsEst)`
  - `public sealed record HostInfo(string DomainName, string OsName, string OsVersion, int NCpus, string PModel)`
  - `public sealed record CcState(VersionInfo CoreClientVersion, HostInfo? HostInfo, IReadOnlyList<Project> Projects, IReadOnlyList<App> Apps, IReadOnlyList<AppVersion> AppVersions, IReadOnlyList<Workunit> Workunits, IReadOnlyList<Result> Results)` — `Parse` takes the `<client_state>` element.

- [ ] **Step 1: Write fixture and failing tests**

`tests/Lattice.Tests/fixtures/get_state.xml`:

```xml
<boinc_gui_rpc_reply>
<client_state>
<core_client_major_version>8</core_client_major_version>
<core_client_minor_version>0</core_client_minor_version>
<core_client_release>4</core_client_release>
<host_info>
    <domain_name>crunchbox</domain_name>
    <p_ncpus>16</p_ncpus>
    <p_model>AMD Ryzen 7 5800X 8-Core Processor</p_model>
    <os_name>Linux Ubuntu</os_name>
    <os_version>24.04.2 LTS</os_version>
</host_info>
<project>
    <master_url>https://einsteinathome.org/</master_url>
    <project_name>Einstein@Home</project_name>
    <user_total_credit>1234567.890000</user_total_credit>
    <user_expavg_credit>4321.098765</user_expavg_credit>
    <host_total_credit>234567.890000</host_total_credit>
    <host_expavg_credit>1234.567890</host_expavg_credit>
</project>
<project>
    <master_url>https://www.worldcommunitygrid.org/</master_url>
    <project_name>World Community Grid</project_name>
    <user_total_credit>99999.000000</user_total_credit>
    <user_expavg_credit>100.000000</user_expavg_credit>
    <host_total_credit>88888.000000</host_total_credit>
    <host_expavg_credit>90.000000</host_expavg_credit>
    <suspended_via_gui/>
    <dont_request_more_work/>
</project>
<app>
    <name>einstein_O3MDF</name>
    <user_friendly_name>Gravitational Wave search O3 MDF</user_friendly_name>
</app>
<app_version>
    <app_name>einstein_O3MDF</app_name>
    <version_num>218</version_num>
    <platform>x86_64-pc-linux-gnu</platform>
    <plan_class>avx</plan_class>
</app_version>
<workunit>
    <name>h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19</name>
    <app_name>einstein_O3MDF</app_name>
    <rsc_fpops_est>105000000000000.000000</rsc_fpops_est>
</workunit>
<result>
    <name>h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19_1</name>
    <wu_name>h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19</wu_name>
    <project_url>https://einsteinathome.org/</project_url>
    <state>2</state>
    <report_deadline>1752800000.000000</report_deadline>
</result>
</client_state>
</boinc_gui_rpc_reply>
```

`tests/Lattice.Tests/CcStateTests.cs`:

```csharp
using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class CcStateTests
{
    private static CcState Load()
    {
        var reply = XElement.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "get_state.xml")));
        return CcState.Parse(reply.Element("client_state")!);
    }

    [Fact]
    public void Parses_core_client_version()
    {
        Assert.Equal(new VersionInfo(8, 0, 4), Load().CoreClientVersion);
    }

    [Fact]
    public void Parses_host_info()
    {
        HostInfo? host = Load().HostInfo;
        Assert.NotNull(host);
        Assert.Equal("crunchbox", host!.DomainName);
        Assert.Equal(16, host.NCpus);
        Assert.Equal("Linux Ubuntu", host.OsName);
    }

    [Fact]
    public void Parses_projects_with_flags()
    {
        var projects = Load().Projects;
        Assert.Equal(2, projects.Count);
        Assert.Equal("Einstein@Home", projects[0].ProjectName);
        Assert.Equal(1234567.89, projects[0].UserTotalCredit, precision: 2);
        Assert.False(projects[0].SuspendedViaGui);
        Assert.True(projects[1].SuspendedViaGui);
        Assert.True(projects[1].DontRequestMoreWork);
    }

    [Fact]
    public void Parses_apps_app_versions_workunits_results()
    {
        CcState state = Load();
        Assert.Single(state.Apps);
        Assert.Equal("Gravitational Wave search O3 MDF", state.Apps[0].UserFriendlyName);
        Assert.Single(state.AppVersions);
        Assert.Equal(218, state.AppVersions[0].VersionNum);
        Assert.Single(state.Workunits);
        Assert.Equal("einstein_O3MDF", state.Workunits[0].AppName);
        Assert.Single(state.Results);
        Assert.Equal(ResultState.FilesDownloaded, state.Results[0].State);
    }

    [Fact]
    public void Empty_client_state_yields_empty_lists_not_throws()
    {
        CcState state = CcState.Parse(XElement.Parse("<client_state/>"));
        Assert.Empty(state.Projects);
        Assert.Empty(state.Results);
        Assert.Null(state.HostInfo);
        Assert.Equal(new VersionInfo(0, 0, 0), state.CoreClientVersion);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CcStateTests"`
Expected: compile FAILURE.

- [ ] **Step 3: Implement**

`src/Lattice.Boinc.GuiRpc/Models/Project.cs`:

```csharp
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>An attached project, from get_state. MasterUrl is the stable identity key.</summary>
public sealed record Project(
    string MasterUrl,
    string ProjectName,
    double UserTotalCredit,
    double UserExpavgCredit,
    double HostTotalCredit,
    double HostExpavgCredit,
    bool SuspendedViaGui,
    bool DontRequestMoreWork)
{
    internal static Project Parse(XElement e) => new(
        ParseHelpers.GetString(e, "master_url"),
        ParseHelpers.GetString(e, "project_name"),
        ParseHelpers.GetDouble(e, "user_total_credit"),
        ParseHelpers.GetDouble(e, "user_expavg_credit"),
        ParseHelpers.GetDouble(e, "host_total_credit"),
        ParseHelpers.GetDouble(e, "host_expavg_credit"),
        ParseHelpers.GetBool(e, "suspended_via_gui"),
        ParseHelpers.GetBool(e, "dont_request_more_work"));
}
```

`src/Lattice.Boinc.GuiRpc/Models/App.cs`:

```csharp
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

public sealed record App(string Name, string UserFriendlyName)
{
    internal static App Parse(XElement e) => new(
        ParseHelpers.GetString(e, "name"),
        ParseHelpers.GetString(e, "user_friendly_name"));
}

public sealed record AppVersion(string AppName, int VersionNum, string Platform, string PlanClass)
{
    internal static AppVersion Parse(XElement e) => new(
        ParseHelpers.GetString(e, "app_name"),
        ParseHelpers.GetInt(e, "version_num"),
        ParseHelpers.GetString(e, "platform"),
        ParseHelpers.GetString(e, "plan_class"));
}
```

`src/Lattice.Boinc.GuiRpc/Models/Workunit.cs`:

```csharp
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

public sealed record Workunit(string Name, string AppName, double RscFpopsEst)
{
    internal static Workunit Parse(XElement e) => new(
        ParseHelpers.GetString(e, "name"),
        ParseHelpers.GetString(e, "app_name"),
        ParseHelpers.GetDouble(e, "rsc_fpops_est"));
}
```

`src/Lattice.Boinc.GuiRpc/Models/HostInfo.cs`:

```csharp
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

public sealed record HostInfo(string DomainName, string OsName, string OsVersion, int NCpus, string PModel)
{
    internal static HostInfo Parse(XElement e) => new(
        ParseHelpers.GetString(e, "domain_name"),
        ParseHelpers.GetString(e, "os_name"),
        ParseHelpers.GetString(e, "os_version"),
        ParseHelpers.GetInt(e, "p_ncpus"),
        ParseHelpers.GetString(e, "p_model"));
}
```

`src/Lattice.Boinc.GuiRpc/Models/CcState.cs`:

```csharp
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Full client state snapshot (get_state). Several MB on busy hosts — fetch once per connection, then poll deltas.</summary>
public sealed record CcState(
    VersionInfo CoreClientVersion,
    HostInfo? HostInfo,
    IReadOnlyList<Project> Projects,
    IReadOnlyList<App> Apps,
    IReadOnlyList<AppVersion> AppVersions,
    IReadOnlyList<Workunit> Workunits,
    IReadOnlyList<Result> Results)
{
    internal static CcState Parse(XElement e) => new(
        new VersionInfo(
            ParseHelpers.GetInt(e, "core_client_major_version"),
            ParseHelpers.GetInt(e, "core_client_minor_version"),
            ParseHelpers.GetInt(e, "core_client_release")),
        e.Element("host_info") is XElement host ? HostInfo.Parse(host) : null,
        [.. e.Elements("project").Select(Project.Parse)],
        [.. e.Elements("app").Select(App.Parse)],
        [.. e.Elements("app_version").Select(AppVersion.Parse)],
        [.. e.Elements("workunit").Select(Workunit.Parse)],
        [.. e.Elements("result").Select(Result.Parse)]);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CcStateTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add Project, App, AppVersion, Workunit, HostInfo, CcState models"
```

---

### Task 8: BoincGuiRpcClient — connect, authorize, exchange_versions

**Files:**
- Create: `src/Lattice.Boinc.GuiRpc/BoincGuiRpcClient.cs`, `src/Lattice.Boinc.GuiRpc/ConnectionState.cs`
- Test: `tests/Lattice.Tests/BoincGuiRpcClientAuthTests.cs`

**Interfaces:**
- Consumes: `RpcConnection` (Task 3), `RpcReplyParser`, `ParseHelpers`, `VersionInfo` (Task 4), `ScriptedStream` (Task 3 tests).
- Produces: `public enum ConnectionState { Disconnected, Connected, Authorized }`; `public sealed class BoincGuiRpcClient : IAsyncDisposable` with public `BoincGuiRpcClient()`, `internal BoincGuiRpcClient(RpcConnection connection)` (test seam, sets State=Connected), `Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default)`, `Task<bool> AuthorizeAsync(string password, CancellationToken ct = default)`, `Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default)`, `ConnectionState State { get; }`, `VersionInfo? DaemonVersion { get; }`, and `private Task<XElement> PerformRpcAsync(string body, bool throwOnUnauthorized, CancellationToken ct)` used by Task 9's RPC methods.

- [ ] **Step 1: Write failing tests**

`tests/Lattice.Tests/BoincGuiRpcClientAuthTests.cs`:

```csharp
using System.Text;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class BoincGuiRpcClientAuthTests
{
    private static BoincGuiRpcClient ClientWith(ScriptedStream stream) =>
        new(new RpcConnection(stream));

    [Fact]
    public async Task Authorize_sends_md5_of_nonce_plus_password()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<nonce>1751600000.114370</nonce>\n</boinc_gui_rpc_reply>",
            "<boinc_gui_rpc_reply>\n<authorized/>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        bool ok = await client.AuthorizeAsync("s3cret");

        Assert.True(ok);
        Assert.Equal(ConnectionState.Authorized, client.State);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        // MD5("1751600000.114370s3cret") lowercase hex
        string expectedHash = Convert.ToHexStringLower(
            System.Security.Cryptography.MD5.HashData(Encoding.ASCII.GetBytes("1751600000.114370s3cret")));
        Assert.Contains("<auth1/>", sent);
        Assert.Contains($"<nonce_hash>{expectedHash}</nonce_hash>", sent);
        Assert.DoesNotContain("<auth1 />", sent); // no space before slash, ever
    }

    [Fact]
    public async Task Authorize_wrong_password_returns_false_without_throwing()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<nonce>42</nonce>\n</boinc_gui_rpc_reply>",
            "<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        bool ok = await client.AuthorizeAsync("wrong");

        Assert.False(ok);
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task ExchangeVersions_stores_daemon_version()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<server_version><major>8</major><minor>0</minor><release>4</release></server_version>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        VersionInfo v = await client.ExchangeVersionsAsync();

        Assert.Equal(new VersionInfo(8, 0, 4), v);
        Assert.Equal(v, client.DaemonVersion);
    }

    [Fact]
    public async Task Rpc_before_connect_throws_invalid_operation()
    {
        await using var client = new BoincGuiRpcClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExchangeVersionsAsync());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~BoincGuiRpcClientAuthTests"`
Expected: compile FAILURE.

- [ ] **Step 3: Implement**

`src/Lattice.Boinc.GuiRpc/ConnectionState.cs`:

```csharp
namespace Lattice.Boinc.GuiRpc;

public enum ConnectionState { Disconnected, Connected, Authorized }
```

`src/Lattice.Boinc.GuiRpc/BoincGuiRpcClient.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// Client for one BOINC core client over one GUI RPC connection.
/// All RPCs are serialized: the protocol is strictly request-reply, so
/// concurrent callers queue on an internal semaphore.
/// No reconnect/retry policy — when the connection dies, callers see
/// BoincConnectionException and must create a new client.
/// </summary>
public sealed class BoincGuiRpcClient : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RpcConnection? connection;

    public ConnectionState State { get; private set; }
    public VersionInfo? DaemonVersion { get; private set; }

    public BoincGuiRpcClient() { }

    internal BoincGuiRpcClient(RpcConnection connection)
    {
        this.connection = connection;
        State = ConnectionState.Connected;
    }

    public async Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default)
    {
        if (connection is not null)
            throw new InvalidOperationException("Already connected. Create a new client to reconnect.");
        connection = await RpcConnection.ConnectAsync(host, port, ct).ConfigureAwait(false);
        State = ConnectionState.Connected;
    }

    public async Task<bool> AuthorizeAsync(string password, CancellationToken ct = default)
    {
        XElement reply1 = await PerformRpcAsync("<auth1/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
        string nonce = ParseHelpers.GetString(reply1, "nonce");

        string hash = Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(nonce + password)));
        XElement reply2 = await PerformRpcAsync(
            $"<auth2>\n<nonce_hash>{hash}</nonce_hash>\n</auth2>", throwOnUnauthorized: false, ct).ConfigureAwait(false);

        bool authorized = reply2.Element("authorized") is not null;
        if (authorized)
            State = ConnectionState.Authorized;
        return authorized;
    }

    public async Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default)
    {
        XElement reply = await PerformRpcAsync("<exchange_versions/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
        XElement versionElement = reply.Element("server_version") ?? reply;
        VersionInfo version = VersionInfo.Parse(versionElement);
        DaemonVersion = version;
        return version;
    }

    private async Task<XElement> PerformRpcAsync(string body, bool throwOnUnauthorized, CancellationToken ct)
    {
        if (connection is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        await gate.WaitAsync(ct).ConfigureAwait(false);
        string raw;
        try
        {
            raw = await connection.PerformRpcAsync(body, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
        return RpcReplyParser.Parse(raw, throwOnUnauthorized);
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
        State = ConnectionState.Disconnected;
        gate.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~BoincGuiRpcClientAuthTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add BoincGuiRpcClient with connect, auth, and exchange_versions"
```

---

### Task 9: Client read RPCs — get_state, get_cc_status, get_results, get_messages

**Files:**
- Modify: `src/Lattice.Boinc.GuiRpc/BoincGuiRpcClient.cs` (add four methods before `PerformRpcAsync`)
- Test: `tests/Lattice.Tests/BoincGuiRpcClientRpcTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `Task<CcState> GetStateAsync(CancellationToken ct = default)`, `Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default)`, `Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default)`, `Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default)`.

- [ ] **Step 1: Write failing tests**

`tests/Lattice.Tests/BoincGuiRpcClientRpcTests.cs`:

```csharp
using System.Text;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class BoincGuiRpcClientRpcTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static BoincGuiRpcClient ClientWith(ScriptedStream stream) =>
        new(new RpcConnection(stream));

    [Fact]
    public async Task GetState_returns_typed_state()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_state.xml"));
        await using var client = ClientWith(stream);

        CcState state = await client.GetStateAsync();

        Assert.Equal(new VersionInfo(8, 0, 4), state.CoreClientVersion);
        Assert.Equal(2, state.Projects.Count);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Contains("<get_state/>", sent);
    }

    [Fact]
    public async Task GetCcStatus_returns_typed_status()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_cc_status.xml"));
        await using var client = ClientWith(stream);

        CcStatus status = await client.GetCcStatusAsync();

        Assert.Equal(RunMode.Auto, status.TaskMode);
    }

    [Fact]
    public async Task GetResults_sends_active_only_flag()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_results.xml"));
        await using var client = ClientWith(stream);

        IReadOnlyList<Result> results = await client.GetResultsAsync(activeOnly: true);

        Assert.Equal(2, results.Count);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Contains("<get_results>\n<active_only>1</active_only>\n</get_results>", sent);
    }

    [Fact]
    public async Task GetMessages_sends_seqno()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_messages.xml"));
        await using var client = ClientWith(stream);

        IReadOnlyList<Message> messages = await client.GetMessagesAsync(seqno: 5);

        Assert.Equal(2, messages.Count);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Contains("<get_messages>\n<seqno>5</seqno>\n</get_messages>", sent);
    }

    [Fact]
    public async Task Unauthorized_reply_on_any_rpc_throws()
    {
        var stream = ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        await Assert.ThrowsAsync<BoincUnauthorizedException>(() => client.GetCcStatusAsync());
    }

    [Fact]
    public async Task GetState_missing_client_state_element_throws_protocol_exception()
    {
        var stream = ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<something_else/>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        await Assert.ThrowsAsync<BoincProtocolException>(() => client.GetStateAsync());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~BoincGuiRpcClientRpcTests"`
Expected: compile FAILURE — methods missing.

- [ ] **Step 3: Add the four methods to `BoincGuiRpcClient` (insert after `ExchangeVersionsAsync`)**

```csharp
    /// <summary>Full state snapshot. Several MB on busy hosts — call once per connection, then poll deltas.</summary>
    public async Task<CcState> GetStateAsync(CancellationToken ct = default)
    {
        XElement reply = await PerformRpcAsync("<get_state/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
        if (reply.Element("client_state") is not XElement clientState)
            throw new BoincProtocolException("get_state reply is missing <client_state>.", reply.ToString());
        return CcState.Parse(clientState);
    }

    public async Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default)
    {
        XElement reply = await PerformRpcAsync("<get_cc_status/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
        return CcStatus.Parse(reply.Element("cc_status") ?? reply);
    }

    public async Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        string body = $"<get_results>\n<active_only>{(activeOnly ? 1 : 0)}</active_only>\n</get_results>";
        XElement reply = await PerformRpcAsync(body, throwOnUnauthorized: true, ct).ConfigureAwait(false);
        XElement container = reply.Element("results") ?? reply;
        return [.. container.Elements("result").Select(Result.Parse)];
    }

    /// <summary>Returns messages with seqno greater than the given value. Seqno is monotonic.</summary>
    public async Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default)
    {
        string body = $"<get_messages>\n<seqno>{seqno}</seqno>\n</get_messages>";
        XElement reply = await PerformRpcAsync(body, throwOnUnauthorized: true, ct).ConfigureAwait(false);
        XElement container = reply.Element("msgs") ?? reply;
        return [.. container.Elements("msg").Select(Message.Parse)];
    }
```

- [ ] **Step 4: Run full suite to verify pass**

Run: `dotnet test`
Expected: PASS, all tests (≈39).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add read RPCs to client (get_state, get_cc_status, get_results, get_messages)"
```

---

### Task 10: Smoke test console tool

**Files:**
- Modify: `tools/Lattice.SmokeTest/Program.cs` (replace generated content)

**Interfaces:**
- Consumes: the full public API of `BoincGuiRpcClient`.
- Produces: `Lattice.SmokeTest` executable. Usage: `dotnet run --project tools/Lattice.SmokeTest -- [host] [port] [--password=<pw>]`. Defaults: `localhost 31416`, password auto-read from the platform's `gui_rpc_auth.cfg`. Exit code 0 on full success; 1 with a diagnostic on any failure.

- [ ] **Step 1: Implement `tools/Lattice.SmokeTest/Program.cs`**

No unit tests — this IS the acceptance test; it requires a live daemon.

```csharp
using Lattice.Boinc.GuiRpc;

string host = "localhost";
int port = 31416;
string? password = null;

foreach (string arg in args)
{
    if (arg.StartsWith("--password=", StringComparison.Ordinal))
        password = arg["--password=".Length..];
    else if (int.TryParse(arg, out int p))
        port = p;
    else
        host = arg;
}

password ??= ReadPasswordFile();
if (password is null)
{
    Console.Error.WriteLine("No password given and no gui_rpc_auth.cfg found. Use --password=<pw>.");
    return 1;
}

try
{
    await using var client = new BoincGuiRpcClient();

    Console.WriteLine($"Connecting to {host}:{port} ...");
    await client.ConnectAsync(host, port);

    Console.WriteLine("Authorizing ...");
    if (!await client.AuthorizeAsync(password))
    {
        Console.Error.WriteLine("FAILED: daemon rejected the password.");
        return 1;
    }

    VersionInfo version = await client.ExchangeVersionsAsync();
    Console.WriteLine($"Daemon version: {version}");

    CcStatus status = await client.GetCcStatusAsync();
    Console.WriteLine($"Modes: tasks={status.TaskMode} gpu={status.GpuMode} network={status.NetworkMode}");
    Console.WriteLine($"Suspend reasons: tasks={status.TaskSuspendReason} network={status.NetworkSuspendReason}");

    CcState state = await client.GetStateAsync();
    Console.WriteLine($"Host: {state.HostInfo?.DomainName} ({state.HostInfo?.PModel}, {state.HostInfo?.NCpus} CPUs)");
    Console.WriteLine($"Projects ({state.Projects.Count}):");
    foreach (Project project in state.Projects)
        Console.WriteLine($"  {project.ProjectName,-30} user={project.UserTotalCredit:F0} host={project.HostTotalCredit:F0}");

    IReadOnlyList<Result> results = await client.GetResultsAsync();
    Console.WriteLine($"Tasks ({results.Count}):");
    foreach (Result result in results)
    {
        string progress = result.ActiveTask is { } at ? $"{at.FractionDone:P1}" : result.State.ToString();
        Console.WriteLine($"  {result.Name,-60} {progress}");
    }

    IReadOnlyList<Message> messages = await client.GetMessagesAsync();
    Console.WriteLine($"Messages: {messages.Count} total; last 3:");
    foreach (Message message in messages.TakeLast(3))
        Console.WriteLine($"  [{message.Timestamp:HH:mm:ss}] {message.Project}: {message.Body}");

    Console.WriteLine("SMOKE TEST PASSED");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
    if (ex is BoincProtocolException protocolEx)
        Console.Error.WriteLine($"Raw payload:\n{protocolEx.RawPayload}");
    return 1;
}

static string? ReadPasswordFile()
{
    string[] candidates = OperatingSystem.IsWindows()
        ? [@"C:\ProgramData\BOINC\gui_rpc_auth.cfg"]
        : OperatingSystem.IsMacOS()
            ? ["/Library/Application Support/BOINC Data/gui_rpc_auth.cfg"]
            : ["/var/lib/boinc-client/gui_rpc_auth.cfg", "/var/lib/boinc/gui_rpc_auth.cfg"];

    foreach (string path in candidates)
        if (File.Exists(path))
            return File.ReadAllText(path).Trim();
    return null;
}
```

- [ ] **Step 2: Verify it builds and fails gracefully without a daemon**

Run: `dotnet run --project tools/Lattice.SmokeTest -- --password=dummy`
Expected (no BOINC installed yet): `FAILED: BoincConnectionException: Failed to connect to localhost:31416.` and exit code 1. That IS the correct behavior for this step — graceful diagnostic, no stack-trace spew.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: add console smoke test tool"
```

- [ ] **Step 4: MANUAL GATE — install BOINC and run for real**

This step needs the human: install the BOINC client on this machine (https://boinc.berkeley.edu/download.php), attach a project (World Community Grid or Einstein@Home recommended for steady task supply), let it download at least one task. Then:

Run: `dotnet run --project tools/Lattice.SmokeTest`
Expected: full typed dump ending in `SMOKE TEST PASSED`, exit code 0.

If the daemon's replies reveal parsing gaps (fields we typed wrong, unexpected structure), capture the raw payload from the `BoincProtocolException` diagnostic, add it as a fixture, fix the parser, and re-run. Replace hand-built fixtures with real captured ones where they differ.

---

### Task 11: Package verification

**Files:**
- No new source files. Possibly fixes surfaced by packing.

- [ ] **Step 1: Full suite green**

Run: `dotnet test`
Expected: all tests pass.

- [ ] **Step 2: Pack**

Run: `dotnet pack src/Lattice.Boinc.GuiRpc -c Release -o artifacts`
Expected: `Successfully created package artifacts/Lattice.Boinc.GuiRpc.0.1.0.nupkg`. Warnings about SourceLink/repository info are acceptable; errors are not.

- [ ] **Step 3: Inspect package contents**

Run: `unzip -l artifacts/Lattice.Boinc.GuiRpc.0.1.0.nupkg`
Expected: contains `lib/net10.0/Lattice.Boinc.GuiRpc.dll`, `lib/net10.0/Lattice.Boinc.GuiRpc.xml` (docs), `README.md`, and the nuspec shows MIT license expression. Publishing to nuget.org is NOT part of this task — the user decides that separately.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: verify NuGet packaging for Lattice.Boinc.GuiRpc"
```

---

## Self-review notes

- Spec coverage: scaffolding (T1), sanitizer (T2), framing (T3), reply parsing + errors (T4), all models & conventions (T4–T7), auth + client (T8), 5 RPCs (T4 exchange_versions in T8; get_state/get_cc_status/get_results/get_messages in T9), smoke test + BOINC install prerequisite (T10), pack + ID availability (T1 step 1, T11). Auto-reconnect/polling explicitly absent per spec.
- Type consistency: `RpcConnection(Stream)` seam used by T3/T8/T9 tests; `ScriptedStream.FromReplies`/`Written` defined T3, consumed T8/T9; `ParseHelpers` signatures identical across T4–T7; `Convert.ToHexStringLower` used in both test (T8 step 1) and implementation (T8 step 3).
- BOINC field names in fixtures are best-effort from BOINC sources; T10 step 4 exists precisely to reconcile them against a real daemon and back-fill real fixtures.
