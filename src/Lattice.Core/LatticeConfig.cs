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
    /// the caller decides how to surface that.
    /// </summary>
    public static LatticeConfig Load(string path)
    {
        if (!File.Exists(path))
            return Default;
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<LatticeConfig>(stream, JsonOptions)
            ?? throw new JsonException($"Config file deserialized to null: {path}");
    }

    /// <summary>
    /// Saves atomically: writes path.tmp, then renames over the target. On Unix the
    /// tmp file (and a freshly created parent directory) are created with user-only
    /// permissions, since the config contains RPC passwords — a plain file create
    /// would otherwise leave the file world-readable under a permissive umask, and
    /// the rename preserves whatever mode the file was created with.
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
