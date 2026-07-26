using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// The I/O half of start-at-login (issue #187). The file-backed registration serves both macOS
/// and Linux, so it is exercised against a real temp directory — the honest test for a writer
/// whose whole job is "is the right file at the right path with the right bytes".
/// </summary>
public class StartupRegistrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}");

    private string RecordPath => Path.Combine(_dir, "nested", "io.github.0x8a63f77d.lattice.plist");

    private FileStartupRegistration Make(string? target = "/Applications/Lattice.app/Contents/MacOS/Lattice") =>
        new(RecordPath, target, LoginItemPolicy.LaunchAgentPlist);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Enabling_creates_the_record_with_the_rendered_content()
    {
        var reg = Make();
        Assert.True(reg.IsSupported);
        Assert.False(reg.IsRegistered);

        Assert.True(reg.Apply(enabled: true, startMinimized: false));

        Assert.True(reg.IsRegistered);
        // The parent directory did not exist — creating it is part of registering.
        Assert.Equal(
            LoginItemPolicy.LaunchAgentPlist("/Applications/Lattice.app/Contents/MacOS/Lattice", false),
            File.ReadAllText(RecordPath));
    }

    [Fact]
    public void Re_applying_an_unchanged_record_does_not_rewrite_the_file()
    {
        // This is the guard that keeps the launch-time self-heal from re-firing macOS's
        // "background item added" notification on every boot. Asserted on the write time
        // rather than the clock: back-date the file, re-apply, and it must not move.
        var reg = Make();
        Assert.True(reg.Apply(enabled: true, startMinimized: true));
        var backdated = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(RecordPath, backdated);

        Assert.True(reg.Apply(enabled: true, startMinimized: true));

        Assert.Equal(backdated, File.GetLastWriteTimeUtc(RecordPath));
    }

    [Fact]
    public void Changing_the_minimized_flag_rewrites_the_record()
    {
        var reg = Make();
        reg.Apply(enabled: true, startMinimized: false);
        Assert.DoesNotContain("--minimized", File.ReadAllText(RecordPath));

        Assert.True(reg.Apply(enabled: true, startMinimized: true));

        Assert.Contains("--minimized", File.ReadAllText(RecordPath));
    }

    [Fact]
    public void A_stale_path_heals_on_the_next_apply()
    {
        // What a moved or updated app leaves behind: a record naming a binary that is gone.
        Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
        File.WriteAllText(RecordPath, LoginItemPolicy.LaunchAgentPlist("/old/gone/Lattice", false));

        Assert.True(Make().Apply(enabled: true, startMinimized: false));

        string healed = File.ReadAllText(RecordPath);
        Assert.Contains("/Applications/Lattice.app/Contents/MacOS/Lattice", healed);
        Assert.DoesNotContain("/old/gone/Lattice", healed);
    }

    [Fact]
    public void Disabling_removes_the_record_and_is_idempotent()
    {
        var reg = Make();
        reg.Apply(enabled: true, startMinimized: false);

        Assert.True(reg.Apply(enabled: false, startMinimized: false));
        Assert.False(reg.IsRegistered);
        Assert.False(File.Exists(RecordPath));

        // Nothing to remove is still success — the requested state holds.
        Assert.True(reg.Apply(enabled: false, startMinimized: false));
    }

    [Fact]
    public void Without_a_target_enabling_fails_and_writes_nothing()
    {
        var reg = Make(target: null);

        Assert.False(reg.IsSupported);
        Assert.False(reg.Apply(enabled: true, startMinimized: false));
        Assert.False(File.Exists(RecordPath));
        // Disabling still works: there is nothing to remove, which is the requested state.
        Assert.True(reg.Apply(enabled: false, startMinimized: false));
    }

    [Fact]
    public void An_unwritable_location_degrades_to_failure_instead_of_throwing()
    {
        // A file where the record's parent directory should be: Linux boxes with no config
        // dir, read-only homes and sandboxes all land here. Must never escape as an exception.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "nested"), "not a directory");

        Assert.False(Make().Apply(enabled: true, startMinimized: false));
    }

    // ---- Desktop-level opt-out (Codex P2, PR #188) -------------------------

    [Fact]
    public void An_entry_the_desktop_disabled_reads_as_not_registered()
    {
        // The Linux shape: the opt-out lives inside our own file, so presence alone is not
        // "registered". Reporting it as registered would make the toggle read on and let the
        // self-heal rewrite the record back to enabled.
        var reg = new FileStartupRegistration(
            RecordPath, "/opt/lattice/Lattice",
            (exe, minimized) => LoginItemPolicy.DesktopEntry(exe, minimized),
            LoginItemPolicy.IsAutostartDisabledByDesktop);
        reg.Apply(enabled: true, startMinimized: false);
        Assert.True(reg.IsRegistered);

        File.AppendAllText(RecordPath, "X-GNOME-Autostart-enabled=false\n");

        Assert.False(reg.IsRegistered);
    }

    [Fact]
    public void Without_a_reader_presence_alone_is_registration()
    {
        // macOS: Background Task Management records the opt-out in its own database and leaves
        // the plist byte-identical, so there is nothing in the file to consult.
        var reg = Make();
        reg.Apply(enabled: true, startMinimized: false);

        Assert.True(reg.IsRegistered);
    }

    // ---- Platform factory --------------------------------------------------

    [Fact]
    public void MacOS_registers_a_LaunchAgent_plist_in_the_users_home()
    {
        var reg = Assert.IsType<FileStartupRegistration>(StartupRegistration.Create(
            TrayPlatform.MacOS, appImagePath: null, processPath: "/Applications/Lattice.app/Contents/MacOS/Lattice",
            homeDirectory: "/Users/u", xdgConfigHome: null));

        Assert.Equal(LoginItemPolicy.LaunchAgentPath("/Users/u"), reg.Path);
        Assert.True(reg.IsSupported);
    }

    [Fact]
    public void Linux_registers_an_autostart_desktop_entry_under_the_config_home()
    {
        var reg = Assert.IsType<FileStartupRegistration>(StartupRegistration.Create(
            TrayPlatform.Linux, appImagePath: "/home/u/Lattice.AppImage", processPath: "/tmp/.mount_x/Lattice",
            homeDirectory: "/home/u", xdgConfigHome: "/home/u/cfg"));

        Assert.Equal(LoginItemPolicy.AutostartPath("/home/u/cfg"), reg.Path);
    }

    [Fact]
    public void The_linux_factory_wires_the_desktop_opt_out_reader()
    {
        // The reader is only useful if the factory actually attaches it — asserting it on a
        // hand-built registration alone would leave the production wiring unproven.
        var reg = (FileStartupRegistration)StartupRegistration.Create(
            TrayPlatform.Linux, appImagePath: null, processPath: "/opt/lattice/Lattice",
            homeDirectory: _dir, xdgConfigHome: _dir);
        Assert.True(reg.Apply(enabled: true, startMinimized: false));
        Assert.True(reg.IsRegistered);

        File.AppendAllText(reg.Path, "Hidden=true\n");

        Assert.False(reg.IsRegistered);
    }

    [Fact]
    public void The_macos_factory_reads_the_opt_out_from_launchd_not_from_the_file()
    {
        // macOS keeps the disable outside the plist, so the file content is irrelevant both
        // ways: a stray Linux-style line must not disable it, and launchd's answer must.
        var enabled = (FileStartupRegistration)StartupRegistration.Create(
            TrayPlatform.MacOS, appImagePath: null, processPath: "/Applications/Lattice.app/Contents/MacOS/Lattice",
            homeDirectory: _dir, xdgConfigHome: null, isDisabledInLaunchd: () => false);
        Assert.True(enabled.Apply(enabled: true, startMinimized: false));
        File.AppendAllText(enabled.Path, "Hidden=true\n");
        Assert.True(enabled.IsRegistered);

        var disabled = (FileStartupRegistration)StartupRegistration.Create(
            TrayPlatform.MacOS, appImagePath: null, processPath: "/Applications/Lattice.app/Contents/MacOS/Lattice",
            homeDirectory: _dir, xdgConfigHome: null, isDisabledInLaunchd: () => true);
        Assert.False(disabled.IsRegistered);
    }

    [Fact]
    public void A_test_that_supplies_no_launchd_reader_never_spawns_one()
    {
        // The default must be hermetic: presence alone is registration, so the suite can never
        // depend on what this machine happens to have in its launchd override database.
        var reg = (FileStartupRegistration)StartupRegistration.Create(
            TrayPlatform.MacOS, appImagePath: null, processPath: "/Applications/Lattice.app/Contents/MacOS/Lattice",
            homeDirectory: _dir, xdgConfigHome: null);
        Assert.True(reg.Apply(enabled: true, startMinimized: false));

        Assert.True(reg.IsRegistered);
    }

    [Fact]
    public void A_read_only_record_is_still_replaced()
    {
        // The observable signature of the write-then-rename: rename() needs write permission
        // on the DIRECTORY, not on the file, so a record left read-only (by a sync tool, a
        // restore, a cautious user) can still be healed — where a truncating WriteAllText
        // would fail outright. Unix-only semantics: Windows refuses to replace a read-only
        // file either way, so the Windows CI leg skips this and the atomic write is pinned
        // there only by the no-leftover-temp test below.
        if (OperatingSystem.IsWindows())
            return;

        var reg = Make();
        reg.Apply(enabled: true, startMinimized: false);
        File.SetUnixFileMode(RecordPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.True(reg.Apply(enabled: true, startMinimized: true));

        Assert.Contains("--minimized", File.ReadAllText(RecordPath));
    }

    [Fact]
    public void A_rewrite_leaves_no_temporary_file_behind()
    {
        // The write is now rename-based; the temp name must not linger where launchd and the
        // autostart scanner look, and must not be mistaken for a second registration.
        var reg = Make();
        reg.Apply(enabled: true, startMinimized: false);
        reg.Apply(enabled: true, startMinimized: true);

        Assert.False(File.Exists(RecordPath + ".tmp"));
        Assert.Contains("--minimized", File.ReadAllText(RecordPath));
    }

    [Fact]
    public void The_linux_factory_binds_this_launchs_appimage_extraction_mode()
    {
        // Launched by extraction (no FUSE evidence) ⇒ the record must force it too, or the
        // login entry silently never starts on that host (Codex P2, PR #188).
        var extracted = (FileStartupRegistration)StartupRegistration.Create(
            TrayPlatform.Linux, "/home/u/Lattice.AppImage", "/tmp/appimage_extracted_1/usr/bin/Lattice",
            _dir, xdgConfigHome: _dir);
        Assert.True(extracted.Apply(enabled: true, startMinimized: false));
        Assert.Contains("APPIMAGE_EXTRACT_AND_RUN=1", File.ReadAllText(extracted.Path));

        // Launched from a FUSE mount ⇒ plain Exec, no extraction cost at every boot.
        var mounted = (FileStartupRegistration)StartupRegistration.Create(
            TrayPlatform.Linux, "/home/u/Lattice.AppImage", "/tmp/.mount_LatticeAbc/usr/bin/Lattice",
            _dir, xdgConfigHome: _dir);
        Assert.True(mounted.Apply(enabled: true, startMinimized: false));
        Assert.DoesNotContain("APPIMAGE_EXTRACT_AND_RUN", File.ReadAllText(mounted.Path));
    }

    [Fact]
    public void A_dotnet_host_launch_yields_an_unsupported_registration_on_every_platform()
    {
        foreach (TrayPlatform platform in Enum.GetValues<TrayPlatform>())
        {
            IStartupRegistration reg = StartupRegistration.Create(
                platform, appImagePath: null, processPath: "/usr/local/share/dotnet/dotnet",
                homeDirectory: "/home/u", xdgConfigHome: null);
            Assert.False(reg.IsSupported);
        }
    }
}
