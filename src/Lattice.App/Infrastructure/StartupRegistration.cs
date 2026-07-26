using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Lattice.App.Infrastructure;

/// <summary>
/// The start-at-login seam (issue #187): the ONLY place that touches the real filesystem
/// or registry on behalf of the login-item feature. Everything it writes is decided by the
/// pure <see cref="LoginItemPolicy"/>, so this interface carries no policy of its own —
/// which is what lets the ViewModel layer be tested against a fake.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>False when this launch cannot be registered at all: an unsupported OS, or a
    /// process with no recordable executable (a framework-dependent <c>dotnet Lattice.dll</c>
    /// run — see <see cref="LoginItemPolicy.ResolveTarget"/>). The Settings toggle disables
    /// itself rather than pretending to register.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Whether Lattice is registered to start at login RIGHT NOW: a record exists and has not
    /// been switched off through the OS's own startup settings. Read from the OS on every
    /// call, never cached, and it is the single source of truth — the UI toggle reads this
    /// rather than a persisted copy, so the two can never disagree (Codex P2, PR #188).
    /// </summary>
    bool IsRegistered { get; }

    /// <summary>
    /// The user asked for this, so it is authoritative: create or remove the record, and
    /// CLEAR any OS-level disable when enabling — otherwise turning the toggle back on after
    /// switching the item off in the OS's own settings would write a byte-identical record,
    /// report success and change nothing (Codex P2, PR #188).
    ///
    /// <para>Writing stays idempotent: an unchanged record is left alone, because macOS's
    /// Background Task Management notifies the user whenever a login item's record appears or
    /// changes. Disabling always succeeds when there is nothing to remove.</para>
    /// </summary>
    bool Apply(bool enabled, bool startMinimized);

    /// <summary>
    /// Launch-time repair of an EXISTING registration's content — the stale path #187 req 3
    /// exists for. Deliberately weaker than <see cref="Apply"/> in both directions: it never
    /// creates a record, and it never clears an OS-level disable. Both restrictions protect
    /// the same thing — a choice the user made outside Lattice — and they live here, in the
    /// type, rather than in a caller's discipline, because two review rounds found bugs on
    /// exactly this axis. A registration that does not exist, or that the OS has switched
    /// off, is nothing to heal: the call succeeds having done nothing.
    /// </summary>
    bool Heal(bool startMinimized);
}

/// <summary>File-backed registration: macOS (<c>~/Library/LaunchAgents/*.plist</c>) and Linux
/// (<c>$XDG_CONFIG_HOME/autostart/*.desktop</c>) differ only in path and rendered content, so
/// one implementation serves both rather than two near-identical copies.</summary>
public sealed class FileStartupRegistration : IStartupRegistration
{
    private readonly string _path;
    private readonly string? _target;
    private readonly Func<string, bool, string> _render;
    private readonly Func<string, bool>? _isDisabledByOs;
    private readonly Func<bool>? _clearOsDisable;

    /// <param name="path">Absolute path of the record file.</param>
    /// <param name="target">Executable to launch, or null when this launch has none.</param>
    /// <param name="render">Content renderer from <see cref="LoginItemPolicy"/>.</param>
    /// <param name="isDisabledByOs">Given an existing record's content, reports whether the
    /// OS's own startup settings switched it off. Linux carries that state INSIDE the file
    /// (<see cref="LoginItemPolicy.IsAutostartDisabledByDesktop"/>); macOS keeps it outside
    /// the plist, so its reader ignores the content it is handed and asks launchd
    /// (<see cref="LaunchdOverrides"/>). Null means "nothing can switch it off but us".</param>
    /// <param name="clearOsDisable">Undoes such an OS-level disable, for an explicit
    /// <see cref="Apply"/>. Only platforms that keep the state OUTSIDE the record need one:
    /// on Linux the flag lives in the file, so rewriting the file already clears it.</param>
    public FileStartupRegistration(
        string path, string? target, Func<string, bool, string> render,
        Func<string, bool>? isDisabledByOs = null, Func<bool>? clearOsDisable = null)
    {
        _path = path;
        _target = target;
        _render = render;
        _isDisabledByOs = isDisabledByOs;
        _clearOsDisable = clearOsDisable;
    }

    /// <summary>Exposed so a test can assert WHERE the platform factory decided to write.</summary>
    public string Path => _path;

    public bool IsSupported => _target is not null;

    public bool IsRegistered
    {
        get
        {
            try
            {
                if (!File.Exists(_path))
                    return false;
                return _isDisabledByOs is null || !_isDisabledByOs(File.ReadAllText(_path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A record we cannot read is one we cannot honour: report it as not
                // registered rather than let an exception escape a property getter the UI binds.
                return false;
            }
        }
    }

    public bool Apply(bool enabled, bool startMinimized)
    {
        try
        {
            if (!enabled)
            {
                // Exists-guarded: File.Delete is a no-op for a missing FILE but throws
                // DirectoryNotFoundException when the parent directory is absent too — and
                // "there is nothing to remove" is exactly the requested state, not a failure.
                if (File.Exists(_path))
                    File.Delete(_path);
                return true;
            }

            // Before the content check, never after: when the OS holds the disable outside
            // the record, the content is already identical and the write below would be
            // skipped, leaving the user unable to re-enable from Lattice at all.
            if (_clearOsDisable is not null && !_clearOsDisable())
                return false;

            return Write(startMinimized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A no-DE Linux box with no config dir, a read-only home, a sandbox: degrade to
            // "the toggle did not take" and let the caller surface it, never throw.
            return false;
        }
    }

    /// <summary>Repairs content only — see <see cref="IStartupRegistration.Heal"/>. Both the
    /// "no record" and the "OS switched it off" cases are folded into
    /// <see cref="IsRegistered"/>, so this cannot resurrect or re-enable anything.</summary>
    public bool Heal(bool startMinimized)
    {
        try
        {
            return !IsRegistered || Write(startMinimized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private bool Write(bool startMinimized)
    {
        {
            if (_target is null)
                return false;

            string desired = _render(_target, startMinimized);
            if (File.Exists(_path) && File.ReadAllText(_path) == desired)
                return true; // already correct — do not touch it (see IStartupRegistration.Apply)

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            // Write-then-rename, the same doctrine UiStateStore uses (Codex P2, PR #188). A
            // plain WriteAllText truncates first, so a full disk or a killed process leaves a
            // previously VALID registration empty — and the launch-time self-heal only repairs
            // it on the next MANUAL launch, which is precisely the launch that was supposed to
            // be automatic. rename() is atomic, so the live record is either the old one or
            // the new one, never a stub. The temp name keeps its own extension out of the way:
            // launchd scans for *.plist and autostart for *.desktop, so a stray *.tmp is
            // ignored by both.
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, desired);
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
    }
}

/// <summary>Windows registration: one string value under the per-user Run key. Marked
/// windows-only so the platform-compatibility analyzer keeps every call site guarded.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string? _target;

    public WindowsStartupRegistration(string? target) => _target = target;

    public bool IsSupported => _target is not null;

    public bool IsRegistered
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(LoginItemPolicy.WindowsRunValueName) is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    public bool Apply(bool enabled, bool startMinimized)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (!enabled)
            {
                key.DeleteValue(LoginItemPolicy.WindowsRunValueName, throwOnMissingValue: false);
                return true;
            }

            if (_target is null)
                return false;

            string desired = LoginItemPolicy.WindowsRunValue(_target, startMinimized);
            if (key.GetValue(LoginItemPolicy.WindowsRunValueName) as string == desired)
                return true; // unchanged — leave the record alone
            key.SetValue(LoginItemPolicy.WindowsRunValueName, desired, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Nothing to clear on this platform — Task Manager's approval state is a
    /// separate key we deliberately do not touch (see the #116 checklist) — so healing is
    /// just a content rewrite of a value that already exists.</summary>
    public bool Heal(bool startMinimized) => !IsRegistered || Apply(true, startMinimized);
}

/// <summary>No mechanism available. Enabling fails (and the toggle is disabled via
/// <see cref="IsSupported"/>); disabling trivially succeeds — there is nothing to remove.</summary>
public sealed class UnsupportedStartupRegistration : IStartupRegistration
{
    public bool IsSupported => false;
    public bool IsRegistered => false;
    public bool Apply(bool enabled, bool startMinimized) => !enabled;
    public bool Heal(bool startMinimized) => true; // nothing exists to repair
}

/// <summary>
/// Asks launchd whether our job is switched off (Codex P2, PR #188). macOS records that
/// choice outside the plist, so the file cannot answer it; <c>launchctl print-disabled</c>
/// can, needs no root, and — unlike inferring from a UI's behaviour — answers the question
/// that actually matters: will launchd run this at login.
///
/// <para>Every failure degrades to "not disabled", which is exactly the behaviour we had
/// before this reader existed, so a launchctl that is missing, slow, or reformatted by a
/// future macOS can only cost accuracy, never correctness.</para>
/// </summary>
internal static class LaunchdOverrides
{
    /// <summary>Hard ceiling on any launchctl call. This runs synchronously from the
    /// composition root and from a UI-bound getter, so it must be bounded no matter how
    /// launchd is behaving.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    // DllImport, not the newer LibraryImport: the source-generated variant demands
    // AllowUnsafeBlocks project-wide, which is far too big a lever for one uid lookup.
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint getuid();

    /// <summary>Whether launchd has been told to skip our job.</summary>
    public static bool IsDisabled(string label) =>
        Run(out string output, "print-disabled", $"gui/{getuid()}")
        && LoginItemPolicy.IsDisabledInLaunchdOverrides(output, label);

    /// <summary>Clears a disabled override, so an explicit "on" in Lattice's own settings can
    /// undo one made in the OS's settings. Without this, re-enabling would write a plist that
    /// is already byte-identical, report success, and leave the job disabled (Codex P2).</summary>
    public static bool Enable(string label) =>
        Run(out _, "enable", $"gui/{getuid()}/{label}");

    /// <summary>
    /// Runs launchctl and returns whether it exited 0 within <see cref="Timeout"/>.
    ///
    /// <para>Output is drained through the ASYNCHRONOUS events rather than
    /// <c>ReadToEnd()</c> (Codex P2, PR #188): a synchronous read blocks until the child
    /// closes stdout, so a wedged launchctl would hang before the timed wait was ever
    /// reached — freezing app startup or the Settings page despite the bound this method
    /// claims. stderr is drained too, or a chatty child could fill that pipe and block
    /// itself. The parameterless <c>WaitForExit</c> after a successful timed wait is the
    /// documented way to be sure the async handlers have flushed.</para>
    /// </summary>
    private static bool Run(out string output, params string[] arguments)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo("/bin/launchctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in arguments)
                psi.ArgumentList.Add(argument);

            using Process? process = Process.Start(psi);
            if (process is null)
                return false;

            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    lock (stdout) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            process.WaitForExit(); // flush the async handlers
            lock (stdout) output = stdout.ToString();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }
}

/// <summary>The two launchd operations the macOS registration needs, bundled so tests can
/// substitute both without spawning anything.</summary>
internal sealed record LaunchdControl(Func<bool> IsDisabled, Func<bool> Enable);

/// <summary>Platform factory. The environment is read HERE and nowhere else, so
/// <see cref="Create"/> stays a pure-input decision a test can drive for any platform.</summary>
public static class StartupRegistration
{
    /// <summary>Builds the registration for the running process.</summary>
    public static IStartupRegistration ForCurrentPlatform() => Create(
        TrayResidencyDefaults.Current,
        Environment.GetEnvironmentVariable("APPIMAGE"),
        Environment.ProcessPath,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        new LaunchdControl(
            () => LaunchdOverrides.IsDisabled(LoginItemPolicy.LaunchAgentLabel),
            () => LaunchdOverrides.Enable(LoginItemPolicy.LaunchAgentLabel)));

    /// <summary>Internal seam for tests: same decision, explicit inputs.</summary>
    /// <param name="launchd">macOS only. Null — the default — means "never disabled, nothing
    /// to clear", so a test never spawns launchctl and never depends on what this machine
    /// happens to have in its override database. The composition root passes the real pair.</param>
    internal static IStartupRegistration Create(
        TrayPlatform platform, string? appImagePath, string? processPath,
        string homeDirectory, string? xdgConfigHome, LaunchdControl? launchd = null)
    {
        string? target = LoginItemPolicy.ResolveTarget(appImagePath, processPath);
#pragma warning disable CS8524 // Domain enum: CS8509 must stay live so a new TrayPlatform
        // member forces this mapping to be revisited. Same pattern as TrayResidencyDefaults.
        return platform switch
        {
            // macOS: the opt-out is not in the file — Background Task Management leaves the
            // plist byte-identical — so the reader ignores the content it is handed and asks
            // launchd instead. The content parameter is part of the seam's shape, not of this
            // platform's answer.
            TrayPlatform.MacOS => new FileStartupRegistration(
                LoginItemPolicy.LaunchAgentPath(homeDirectory), target, LoginItemPolicy.LaunchAgentPlist,
                launchd is null ? null : _ => launchd.IsDisabled(),
                launchd?.Enable),
            TrayPlatform.Linux => new FileStartupRegistration(
                LoginItemPolicy.AutostartPath(LoginItemPolicy.ConfigHome(xdgConfigHome, homeDirectory)),
                target,
                // extract-and-run is a property of THIS launch, so it is bound once here
                // rather than threaded through every render call.
                (exe, minimized) => LoginItemPolicy.DesktopEntry(
                    exe, minimized, LoginItemPolicy.NeedsExtractAndRun(appImagePath, processPath)),
                LoginItemPolicy.IsAutostartDisabledByDesktop),
            // The runtime guard is what satisfies the platform-compatibility analyzer: the
            // enum arm alone does not prove to it that we are on Windows.
            TrayPlatform.Windows => OperatingSystem.IsWindows()
                ? new WindowsStartupRegistration(target)
                : new UnsupportedStartupRegistration(),
        };
#pragma warning restore CS8524
    }
}
