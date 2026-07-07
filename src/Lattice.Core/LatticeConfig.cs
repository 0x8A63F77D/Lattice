using System.Text.Json;

namespace Lattice.Core;

/// <summary>
/// The persisted application configuration: polling cadence plus the host list.
/// Stored as camelCase JSON; writes are atomic (tmp file + rename).
/// </summary>
public sealed record LatticeConfig(int PollingIntervalSeconds, IReadOnlyList<HostConfig> Hosts)
{
    /// <summary>Factory default: 5-second polling, no hosts.</summary>
    public static LatticeConfig Default { get; } = new(5, []);

    /// <summary>The polling intervals the Settings UI offers (seconds).</summary>
    public static readonly IReadOnlyList<int> AllowedPollingIntervals = [2, 5, 10, 30, 60];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Per-user config path: &lt;ApplicationData&gt;/Lattice/config.json.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lattice", "config.json");

    /// <summary>
    /// Loads the config from <paramref name="path"/>. A missing file yields
    /// <see cref="Default"/>; a corrupt file throws <see cref="JsonException"/> —
    /// the caller decides how to surface that. The result is normalized: this is a
    /// hand-editable file, so a missing or out-of-range <c>pollingIntervalSeconds</c>
    /// falls back to the 5-second default instead of propagating (e.g. the
    /// System.Text.Json default of 0 for a missing int, which would otherwise make
    /// <c>HostMonitor</c> poll in a tight loop), and a missing <c>hosts</c> falls back
    /// to an empty list instead of null. Individual host entries are not validated or
    /// dropped here — that is out of scope for Load.
    /// </summary>
    public static LatticeConfig Load(string path)
    {
        if (!File.Exists(path))
            return Default;
        using FileStream stream = File.OpenRead(path);
        LatticeConfig config = JsonSerializer.Deserialize<LatticeConfig>(stream, JsonOptions)
            ?? throw new JsonException($"Config file deserialized to null: {path}");

        int interval = AllowedPollingIntervals.Contains(config.PollingIntervalSeconds)
            ? config.PollingIntervalSeconds
            : Default.PollingIntervalSeconds;
        IReadOnlyList<HostConfig> hosts = config.Hosts ?? [];
        return config with { PollingIntervalSeconds = interval, Hosts = hosts };
    }

    /// <summary>
    /// Saves atomically: writes path.tmp, then renames over the target. On Unix the
    /// tmp file (and a freshly created parent directory) are created with user-only
    /// permissions, since the config contains RPC passwords — a plain file create
    /// would otherwise leave the file world-readable under a permissive umask, and
    /// the rename preserves whatever mode the file was created with. A stale tmp file
    /// (e.g. orphaned by an earlier serialize failure) is deleted first: UnixCreateMode
    /// only applies when the file is actually created, and FileMode.Create on an
    /// existing file truncates and reuses it without changing its mode — so a stale
    /// tmp could otherwise carry looser permissions than intended into the rename.
    /// </summary>
    public void Save(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(dir);
        else
            Directory.CreateDirectory(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string tmp = path + ".tmp";
        File.Delete(tmp); // no-op if missing; race-free under the registry's single-threaded mutation contract
        using (FileStream stream = OperatingSystem.IsWindows()
            ? File.Create(tmp)
            : new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            JsonSerializer.Serialize(stream, this, JsonOptions);
        File.Move(tmp, path, overwrite: true);
    }
}
