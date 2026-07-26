using System.Runtime.Versioning;
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
    /// Brings the OS record in line with the requested state and returns whether it
    /// succeeded. Writing is idempotent: an unchanged record is left ALONE, because macOS's
    /// Background Task Management notifies the user whenever a login item's record appears or
    /// changes, and the launch-time self-heal would otherwise fire that notification on every
    /// boot. Disabling always succeeds when there is nothing to remove.
    /// </summary>
    bool Apply(bool enabled, bool startMinimized);
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

    /// <param name="path">Absolute path of the record file.</param>
    /// <param name="target">Executable to launch, or null when this launch has none.</param>
    /// <param name="render">Content renderer from <see cref="LoginItemPolicy"/>.</param>
    /// <param name="isDisabledByOs">Reads an existing record and reports whether the OS's own
    /// startup settings switched it off. Linux records carry that state INSIDE the file
    /// (<see cref="LoginItemPolicy.IsAutostartDisabledByDesktop"/>); macOS keeps it in the
    /// Background Task Management database instead, so the macOS registration passes null.</param>
    public FileStartupRegistration(
        string path, string? target, Func<string, bool, string> render,
        Func<string, bool>? isDisabledByOs = null)
    {
        _path = path;
        _target = target;
        _render = render;
        _isDisabledByOs = isDisabledByOs;
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

            if (_target is null)
                return false;

            string desired = _render(_target, startMinimized);
            if (File.Exists(_path) && File.ReadAllText(_path) == desired)
                return true; // already correct — do not touch it (see IStartupRegistration.Apply)

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            // Plain write, not the tmp+rename UiStateStore uses: a torn record simply fails
            // the content comparison above on the next launch and is rewritten, so the
            // self-heal already covers the only failure a rename would prevent.
            File.WriteAllText(_path, desired);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A no-DE Linux box with no config dir, a read-only home, a sandbox: degrade to
            // "the toggle did not take" and let the caller surface it, never throw.
            return false;
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
}

/// <summary>No mechanism available. Enabling fails (and the toggle is disabled via
/// <see cref="IsSupported"/>); disabling trivially succeeds — there is nothing to remove.</summary>
public sealed class UnsupportedStartupRegistration : IStartupRegistration
{
    public bool IsSupported => false;
    public bool IsRegistered => false;
    public bool Apply(bool enabled, bool startMinimized) => !enabled;
}

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
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));

    /// <summary>Internal seam for tests: same decision, explicit inputs.</summary>
    internal static IStartupRegistration Create(
        TrayPlatform platform, string? appImagePath, string? processPath,
        string homeDirectory, string? xdgConfigHome)
    {
        string? target = LoginItemPolicy.ResolveTarget(appImagePath, processPath);
#pragma warning disable CS8524 // Domain enum: CS8509 must stay live so a new TrayPlatform
        // member forces this mapping to be revisited. Same pattern as TrayResidencyDefaults.
        return platform switch
        {
            // macOS: no isDisabledByOs reader — Background Task Management records the user's
            // opt-out in its own database, leaving the plist byte-identical, so there is
            // nothing in the file to consult.
            TrayPlatform.MacOS => new FileStartupRegistration(
                LoginItemPolicy.LaunchAgentPath(homeDirectory), target, LoginItemPolicy.LaunchAgentPlist),
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
