using System.Xml.Linq;
using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// The pure half of start-at-login (issue #187): which executable a login record should point
/// at, where the record lives, and what it contains. Every platform's table is asserted here
/// regardless of the OS running the suite — that portability is the whole reason the policy
/// takes its inputs explicitly instead of reading the environment.
/// </summary>
public class LoginItemPolicyTests
{
    // ---- ResolveTarget -----------------------------------------------------

    [Fact]
    public void AppImage_path_wins_over_the_in_mount_apphost()
    {
        // Inside a running AppImage the process path is the mount that disappears on exit;
        // $APPIMAGE is the outer file that survives. Same precedence as App.PlanRelaunch.
        Assert.Equal("/home/u/Apps/Lattice.AppImage", LoginItemPolicy.ResolveTarget(
            "/home/u/Apps/Lattice.AppImage", "/tmp/.mount_Lattice/usr/bin/Lattice"));
    }

    [Fact]
    public void Plain_apphost_launch_registers_the_process_path()
    {
        Assert.Equal("/Applications/Lattice.app/Contents/MacOS/Lattice", LoginItemPolicy.ResolveTarget(
            null, "/Applications/Lattice.app/Contents/MacOS/Lattice"));
        Assert.Equal(@"C:\Program Files\Lattice\Lattice.exe", LoginItemPolicy.ResolveTarget(
            null, @"C:\Program Files\Lattice\Lattice.exe"));
    }

    [Fact]
    public void A_build_tree_apphost_is_still_a_valid_target()
    {
        // Deliberate: it IS the binary the user launched, and SyncOnLaunch rewrites the record
        // whenever that path changes. Only the dotnet-host case below has nothing to record.
        Assert.Equal("/repo/src/Lattice.App/bin/Debug/net10.0/Lattice", LoginItemPolicy.ResolveTarget(
            null, "/repo/src/Lattice.App/bin/Debug/net10.0/Lattice"));
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData("/usr/bin/DOTNET")]
    public void A_dotnet_host_launch_has_no_registrable_target(string processPath)
    {
        // `dotnet Lattice.dll`: registering this would schedule the bare SDK host at login.
        Assert.Null(LoginItemPolicy.ResolveTarget(null, processPath));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    public void No_paths_at_all_yields_no_target(string? appImage, string? processPath)
    {
        Assert.Null(LoginItemPolicy.ResolveTarget(appImage, processPath));
    }

    // ---- StartsHidden ------------------------------------------------------

    [Fact]
    public void Startup_is_hidden_only_on_an_exact_minimized_argument()
    {
        Assert.True(LoginItemPolicy.StartsHidden(["--minimized"]));
        Assert.True(LoginItemPolicy.StartsHidden(["--other", "--minimized"]));
        Assert.False(LoginItemPolicy.StartsHidden(null));
        Assert.False(LoginItemPolicy.StartsHidden([]));
        Assert.False(LoginItemPolicy.StartsHidden(["--minimize"]));
        // A prefix is a different, unknown switch — not this one.
        Assert.False(LoginItemPolicy.StartsHidden(["--minimized-later"]));
        Assert.False(LoginItemPolicy.StartsHidden(["--MINIMIZED"]));
    }

    // ---- Paths -------------------------------------------------------------

    [Fact]
    public void LaunchAgent_lives_in_the_users_LaunchAgents_directory_named_for_the_label()
    {
        Assert.Equal(
            Path.Combine("/Users/u", "Library", "LaunchAgents", "io.github.0x8a63f77d.lattice.plist"),
            LoginItemPolicy.LaunchAgentPath("/Users/u"));
    }

    [Fact]
    public void Autostart_lives_under_the_resolved_config_home()
    {
        Assert.Equal(
            Path.Combine("/home/u/.config", "autostart", "lattice.desktop"),
            LoginItemPolicy.AutostartPath(LoginItemPolicy.ConfigHome(null, "/home/u")));
    }

    [Fact]
    public void Config_home_honours_an_absolute_XDG_value_and_ignores_a_relative_one()
    {
        Assert.Equal("/opt/cfg", LoginItemPolicy.ConfigHome("/opt/cfg", "/home/u"));
        // The Base Directory spec says a relative XDG_CONFIG_HOME is invalid and must be ignored.
        Assert.Equal(Path.Combine("/home/u", ".config"), LoginItemPolicy.ConfigHome("cfg", "/home/u"));
        Assert.Equal(Path.Combine("/home/u", ".config"), LoginItemPolicy.ConfigHome("", "/home/u"));
        Assert.Equal(Path.Combine("/home/u", ".config"), LoginItemPolicy.ConfigHome(null, "/home/u"));
    }

    // ---- macOS LaunchAgent plist -------------------------------------------

    [Fact]
    public void LaunchAgent_plist_is_well_formed_and_carries_the_label_and_program()
    {
        string plist = LoginItemPolicy.LaunchAgentPlist("/Applications/Lattice.app/Contents/MacOS/Lattice", false);

        // Parses as XML at all — a malformed plist is silently ignored by launchd.
        XElement dict = XDocument.Parse(plist).Root!.Element("dict")!;
        Assert.Equal("io.github.0x8a63f77d.lattice", ValueFor(dict, "Label")!.Value);
        Assert.Equal(
            ["/Applications/Lattice.app/Contents/MacOS/Lattice"],
            ValueFor(dict, "ProgramArguments")!.Elements("string").Select(e => e.Value));
        Assert.Equal("true", ValueFor(dict, "RunAtLoad")!.Name.LocalName);
        Assert.Equal("Interactive", ValueFor(dict, "ProcessType")!.Value);
    }

    [Fact]
    public void LaunchAgent_plist_adds_the_minimized_argument_only_when_asked()
    {
        XElement args = ValueFor(
            XDocument.Parse(LoginItemPolicy.LaunchAgentPlist("/bin/Lattice", true)).Root!.Element("dict")!,
            "ProgramArguments")!;
        Assert.Equal(["/bin/Lattice", "--minimized"], args.Elements("string").Select(e => e.Value));
    }

    [Fact]
    public void LaunchAgent_plist_never_sets_KeepAlive()
    {
        // KeepAlive would relaunch Lattice every time the user quits it — that is a daemon,
        // not a login item. RunAtLoad alone is the whole "start at login" contract.
        Assert.DoesNotContain("KeepAlive", LoginItemPolicy.LaunchAgentPlist("/bin/Lattice", true));
    }

    [Fact]
    public void LaunchAgent_plist_escapes_xml_in_the_executable_path()
    {
        string plist = LoginItemPolicy.LaunchAgentPlist("/Apps/A&B<C>/Lattice", false);
        // Round-trips through a real parser: the escaping is correct, not merely present.
        Assert.Equal(
            ["/Apps/A&B<C>/Lattice"],
            ValueFor(XDocument.Parse(plist).Root!.Element("dict")!, "ProgramArguments")!
                .Elements("string").Select(e => e.Value));
    }

    /// <summary>plist dicts are a flat key/value SEQUENCE, so a value is the element after
    /// its &lt;key&gt;.</summary>
    private static XElement? ValueFor(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    // ---- Linux desktop entry -----------------------------------------------

    [Fact]
    public void Desktop_entry_declares_an_absolute_exec_and_the_autostart_toggle()
    {
        var lines = LoginItemPolicy.DesktopEntry("/opt/lattice/Lattice", false)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("[Desktop Entry]", lines[0]);
        Assert.Contains("Type=Application", lines);
        Assert.Contains("Exec=/opt/lattice/Lattice", lines);
        Assert.Contains("Terminal=false", lines);
        // GNOME's own autostart UI writes this key; setting it explicitly stops a stale
        // "false" left by session tooling from silently winning over our file.
        Assert.Contains("X-GNOME-Autostart-enabled=true", lines);
        Assert.Contains("StartupWMClass=Lattice", lines);
    }

    [Fact]
    public void Desktop_entry_appends_the_minimized_argument()
    {
        Assert.Contains(
            "Exec=/opt/lattice/Lattice --minimized",
            LoginItemPolicy.DesktopEntry("/opt/lattice/Lattice", true).Split('\n'));
    }

    [Fact]
    public void Desktop_entry_quotes_an_exec_path_that_needs_it()
    {
        // A bare path with a space would be parsed as two arguments.
        Assert.Contains(
            "Exec=\"/opt/my apps/Lattice\" --minimized",
            LoginItemPolicy.DesktopEntry("/opt/my apps/Lattice", true).Split('\n'));
    }

    [Fact]
    public void Desktop_entry_double_escapes_reserved_characters_inside_a_quoted_exec()
    {
        // Two layers: Exec-level \" plus the file format's own \\ -> \ unescape, so a literal
        // quote is written \\" and a literal backslash \\\\.
        Assert.Contains(
            @"Exec=""/opt/a\\""b/Lattice""",
            LoginItemPolicy.DesktopEntry(@"/opt/a""b/Lattice", false).Split('\n'));
        Assert.Contains(
            @"Exec=""/opt/a\\\\b/Lattice""",
            LoginItemPolicy.DesktopEntry(@"/opt/a\b/Lattice", false).Split('\n'));
    }

    // ---- Windows Run value -------------------------------------------------

    [Fact]
    public void Windows_run_value_always_quotes_the_executable()
    {
        Assert.Equal(
            "\"C:\\Program Files\\Lattice\\Lattice.exe\"",
            LoginItemPolicy.WindowsRunValue(@"C:\Program Files\Lattice\Lattice.exe", false));
        Assert.Equal(
            "\"C:\\Program Files\\Lattice\\Lattice.exe\" --minimized",
            LoginItemPolicy.WindowsRunValue(@"C:\Program Files\Lattice\Lattice.exe", true));
    }
}
