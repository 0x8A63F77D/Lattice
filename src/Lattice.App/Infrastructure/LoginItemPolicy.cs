using System.Text;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Pure "what record, and where" for start-at-login (issue #187).
///
/// Every platform's mechanism is the same shape — write ONE record to a well-known
/// per-user location — so this module owns the whole decision: which executable the
/// record should launch, where the record lives, and what it contains. The actual
/// filesystem/registry write sits behind <see cref="IStartupRegistration"/>.
///
/// Nothing here reads the environment: the caller passes the process path, the home /
/// config directories and the flags. That is what makes the entire table unit-testable
/// on any OS — the macOS plist and the Linux desktop entry are asserted on a Mac CI
/// runner exactly as the Windows Run value is.
/// </summary>
public static class LoginItemPolicy
{
    /// <summary>launchd job label and plist file base name. Matches
    /// <c>CFBundleIdentifier</c> in <c>packaging/macos/Info.plist</c> so the entry
    /// System Settings shows under Login Items is attributable to this app.</summary>
    public const string LaunchAgentLabel = "io.github.0x8a63f77d.lattice";

    /// <summary>XDG autostart file name, matching <c>packaging/linux/lattice.desktop</c>.</summary>
    public const string AutostartFileName = "lattice.desktop";

    /// <summary>Value name under <c>HKCU\...\CurrentVersion\Run</c>. This is what the
    /// user sees in Task Manager → Startup apps, so it is the display name, not the id.</summary>
    public const string WindowsRunValueName = "Lattice";

    /// <summary>The argument the login registration passes so the launched instance stays
    /// in the tray instead of throwing a window at the user on every boot. Consumed by the
    /// composition root via <see cref="StartsHidden"/>; it is deliberately NOT a persisted
    /// preference, so a hand launch always shows the window.</summary>
    public const string MinimizedArgument = "--minimized";

    /// <summary>
    /// The executable a login registration should point at, or <c>null</c> when this launch
    /// has none worth recording.
    ///
    /// Prefers <c>$APPIMAGE</c> — the AppImage runtime's path to the outer <c>.AppImage</c> —
    /// over <see cref="Environment.ProcessPath"/>, which inside a running AppImage is the
    /// in-mount apphost that disappears on exit. Same precedence as
    /// <see cref="App.PlanRelaunch"/>; the two answer the same question.
    ///
    /// The one rejection is a framework-dependent <c>dotnet Lattice.dll</c> run: there
    /// <see cref="Environment.ProcessPath"/> is the SDK host, and registering it would
    /// schedule bare <c>dotnet</c> at login. Everything else — including an apphost inside a
    /// build tree — is registered as-is: it IS the binary the user launched, and the
    /// launch-time self-heal rewrites the record whenever that path changes.
    /// </summary>
    public static string? ResolveTarget(string? appImagePath, string? processPath)
    {
        if (!string.IsNullOrWhiteSpace(appImagePath))
            return appImagePath;
        if (string.IsNullOrWhiteSpace(processPath) || IsDotnetHost(processPath))
            return null;
        return processPath;
    }

    /// <summary>Splits on BOTH separators rather than <see cref="Path.GetFileName(string)"/>,
    /// which only honours the running OS's: a Windows process path must be recognised while the
    /// test suite runs on macOS, and this module's whole contract is that its table is decidable
    /// off explicit inputs on any host.</summary>
    private static bool IsDotnetHost(string processPath)
    {
        int slash = processPath.LastIndexOfAny(['/', '\\']);
        string fileName = slash < 0 ? processPath : processPath[(slash + 1)..];
        int dot = fileName.LastIndexOf('.');
        if (dot > 0)
            fileName = fileName[..dot];
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether this launch should stay in the tray: exact-match on
    /// <see cref="MinimizedArgument"/> anywhere in the command line. Ordinal and exact —
    /// a prefix such as <c>--minimized-foo</c> is a different (unknown) switch, not this one.</summary>
    public static bool StartsHidden(IReadOnlyList<string>? args)
    {
        if (args is null)
            return false;
        for (int i = 0; i < args.Count; i++)
            if (string.Equals(args[i], MinimizedArgument, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Per-user LaunchAgent plist path. launchd scans this directory at login,
    /// so writing the file is the whole registration — no <c>launchctl</c> call needed.</summary>
    public static string LaunchAgentPath(string homeDirectory) =>
        Path.Combine(homeDirectory, "Library", "LaunchAgents", LaunchAgentLabel + ".plist");

    /// <summary>XDG autostart file path under the resolved config home.</summary>
    public static string AutostartPath(string configHome) =>
        Path.Combine(configHome, "autostart", AutostartFileName);

    /// <summary>Resolves <c>$XDG_CONFIG_HOME</c> per the Base Directory spec: a RELATIVE
    /// value is invalid and must be ignored, and an unset/empty one falls back to
    /// <c>$HOME/.config</c>.</summary>
    public static string ConfigHome(string? xdgConfigHome, string homeDirectory) =>
        !string.IsNullOrWhiteSpace(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome)
            ? xdgConfigHome
            : Path.Combine(homeDirectory, ".config");

    /// <summary>
    /// The LaunchAgent record. <c>RunAtLoad</c> alone is the "start at login" contract;
    /// <c>KeepAlive</c> is deliberately absent — with it, quitting Lattice would relaunch it,
    /// which is a daemon, not a login item. <c>ProcessType Interactive</c> keeps launchd from
    /// throttling a foreground GUI app.
    /// </summary>
    public static string LaunchAgentPlist(string executable, bool startMinimized)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        sb.Append("<dict>\n");
        sb.Append("\t<key>Label</key>\n");
        sb.Append("\t<string>").Append(XmlEscape(LaunchAgentLabel)).Append("</string>\n");
        sb.Append("\t<key>ProgramArguments</key>\n");
        sb.Append("\t<array>\n");
        sb.Append("\t\t<string>").Append(XmlEscape(executable)).Append("</string>\n");
        if (startMinimized)
            sb.Append("\t\t<string>").Append(XmlEscape(MinimizedArgument)).Append("</string>\n");
        sb.Append("\t</array>\n");
        sb.Append("\t<key>RunAtLoad</key>\n");
        sb.Append("\t<true/>\n");
        sb.Append("\t<key>ProcessType</key>\n");
        sb.Append("\t<string>Interactive</string>\n");
        sb.Append("</dict>\n");
        sb.Append("</plist>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Whether the login record must force AppImage extract-and-run (Codex P2, PR #188).
    /// An AppImage needs FUSE on the host; without it, it only starts via extract-and-run
    /// (README's Linux install notes), so a plain <c>Exec</c> would leave a login entry that
    /// silently never starts.
    ///
    /// The default is the SAFE direction — force it — and we downgrade only on direct positive
    /// evidence that FUSE works: this very process running out of a FUSE mount, which the
    /// AppImage runtime names <c>/tmp/.mount_*</c> (the same convention
    /// <see cref="App.PlanRelaunch"/> already documents). A misread can then only cost an
    /// extraction at boot, never a dead login item.
    /// </summary>
    public static bool NeedsExtractAndRun(string? appImagePath, string? processPath) =>
        !string.IsNullOrWhiteSpace(appImagePath)
        && (processPath is null || !processPath.Contains("/.mount_", StringComparison.Ordinal));

    /// <summary>
    /// Whether launchd has been told not to run our job, parsed from
    /// <c>launchctl print-disabled gui/&lt;uid&gt;</c> (Codex P2, PR #188). macOS keeps this
    /// state OUTSIDE the plist — Background Task Management leaves the file byte-identical
    /// when the user switches the item off — so file content can never answer the question
    /// "will this actually start at login". launchd can, and this parses its answer.
    ///
    /// <para>Lines look like <c>"com.example.job" =&gt; disabled</c>. An absent label means no
    /// override, i.e. enabled. Pure, so the shape is pinned by tests rather than by whatever
    /// the local machine happens to have in its override database.</para>
    /// </summary>
    public static bool IsDisabledInLaunchdOverrides(string printDisabledOutput, string label)
    {
        string needle = '"' + label + '"';
        foreach (string rawLine in printDisabledOutput.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(needle, StringComparison.Ordinal))
                continue;
            // Labels are unique in the override list, so the first match settles it.
            return line[needle.Length..].Trim() == "=> disabled";
        }
        return false;
    }

    /// <summary>
    /// Whether an EXISTING autostart entry has been switched off through the desktop's own
    /// startup-app settings (Codex P2, PR #188). GNOME's session tooling writes
    /// <c>X-GNOME-Autostart-enabled=false</c> and other desktops write <c>Hidden=true</c>,
    /// both INSIDE our file — so unlike macOS, where Background Task Management records the
    /// opt-out outside the plist, an unchanged-content check cannot protect the user's choice
    /// here. The launch-time self-heal consults this so it never flips a user's explicit
    /// OS-level "off" back on.
    /// </summary>
    public static bool IsAutostartDisabledByDesktop(string content)
    {
        foreach (ReadOnlySpan<char> raw in content.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            // Keys are case-sensitive per the Desktop Entry spec; values are read leniently
            // because hand-edited files and older tooling are not consistent about case.
            ReadOnlySpan<char> key = line[..eq].Trim();
            ReadOnlySpan<char> value = line[(eq + 1)..].Trim();
            if (key.SequenceEqual("X-GNOME-Autostart-enabled") && value.Equals("false", StringComparison.OrdinalIgnoreCase))
                return true;
            if (key.SequenceEqual("Hidden") && value.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The XDG autostart record, derived from <c>packaging/linux/lattice.desktop</c> with an
    /// ABSOLUTE <c>Exec</c> (the packaged file relies on <c>$PATH</c>; an autostart entry
    /// cannot). <c>X-GNOME-Autostart-enabled</c> is the toggle GNOME's own UI writes — set it
    /// explicitly, which is safe precisely because
    /// <see cref="IsAutostartDisabledByDesktop"/> keeps the self-heal from ever rewriting a
    /// record the user disabled there.
    /// </summary>
    public static string DesktopEntry(string executable, bool startMinimized, bool extractAndRun = false)
    {
        string exec = QuoteExecArgument(executable);
        if (extractAndRun)
            // Same mechanism as App.RestartApp's relaunch: set the runtime's env var rather
            // than pass --appimage-extract-and-run, so the flag can never reach our own argv.
            exec = "/usr/bin/env APPIMAGE_EXTRACT_AND_RUN=1 " + exec;
        if (startMinimized)
            exec += " " + MinimizedArgument;
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=Lattice\n");
        sb.Append("Comment=Multi-host BOINC monitoring dashboard\n");
        sb.Append("Icon=lattice\n");
        sb.Append("Exec=").Append(exec).Append('\n');
        sb.Append("Terminal=false\n");
        sb.Append("Categories=Utility;Monitor;Network;\n");
        sb.Append("StartupWMClass=Lattice\n");
        sb.Append("X-GNOME-Autostart-enabled=true\n");
        return sb.ToString();
    }

    /// <summary>The <c>HKCU\...\Run</c> value: the executable always quoted (a path with a
    /// space is otherwise split by CreateProcess), plus the flag.</summary>
    public static string WindowsRunValue(string executable, bool startMinimized) =>
        startMinimized
            ? $"\"{executable}\" {MinimizedArgument}"
            : $"\"{executable}\"";

    /// <summary>
    /// Desktop Entry spec quoting for one <c>Exec</c> argument. Reserved characters force
    /// double quotes; inside them <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> take a preceding
    /// backslash, and the file-format layer then doubles every literal backslash. A plain
    /// path (the overwhelmingly common case) is emitted bare so naive parsers see the
    /// familiar unquoted line.
    /// </summary>
    private static string QuoteExecArgument(string argument)
    {
        // Field codes (%f, %U, …) are expanded by the launcher EVEN INSIDE QUOTES, so a
        // literal percent must be doubled — a path under a "100%" directory would otherwise
        // expand to a different command or be rejected outright (Codex P2, PR #188). Done
        // first, and deliberately not added to `reserved`: %% needs no quoting of its own.
        argument = argument.Replace("%", "%%", StringComparison.Ordinal);

        const string reserved = " \t\n\"'\\><~|&;$*?#()`";
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(reserved) < 0)
            return argument;

        var sb = new StringBuilder("\"");
        foreach (char c in argument)
        {
            // Two layers. Exec-level: a literal " ` $ \ needs a preceding backslash in the
            // PARSED value. File-format level: the parsed value comes from unescaping \\ -> \,
            // so each of those backslashes is written doubled on the line. Hence " -> \\" and
            // a literal \ -> \\\\ (parsed \\, which Exec then reads as one backslash).
            switch (c)
            {
                case '\\': sb.Append(@"\\\\"); break;
                case '"' or '`' or '$': sb.Append(@"\\").Append(c); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
