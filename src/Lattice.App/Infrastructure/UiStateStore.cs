using System.Text.Json;
using Lattice.Core;

namespace Lattice.App.Infrastructure;

/// <summary>Persisted rail list/group override (maps to F# RailOverride in the shell).</summary>
public enum RailGroupingMode { Auto, Flat, Grouped }

/// <summary>Persisted app theme (design 2d/1f). System follows the OS.</summary>
public enum AppTheme { Light, Dark, System }

/// <summary>
/// Persists per-user UI preferences (density, column visibility/widths) in JSON.
/// Defaults and disposal on error: safe fallback for UI state (unlike the host registry).
/// Load always succeeds; Save returns false on I/O failures, never throws.
/// </summary>
public sealed class UiStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public string Path { get; }

    /// <summary>Uses default path: &lt;ApplicationData&gt;/Lattice/ui-state.json.</summary>
    public UiStateStore()
        : this(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(LatticeConfig.DefaultPath)!, "ui-state.json"))
    {
    }

    /// <summary>Accepts custom path for testing.</summary>
    public UiStateStore(string path)
    {
        Path = path;
    }

    /// <summary>Loads UI state. Missing or corrupt file yields defaults.</summary>
    public UiState Load()
    {
        try
        {
            if (!File.Exists(Path))
                return UiState.Default;

            using FileStream stream = File.OpenRead(Path);
            UiState state = JsonSerializer.Deserialize<UiState>(stream, JsonOptions)
                ?? throw new JsonException($"State file deserialized to null: {Path}");
            // Normalize: syntactically valid JSON may carry null dicts (hand-edited or
            // older-version file) — same pattern as LatticeConfig.Load for Hosts.
            return state with
            {
                ColumnVisibility = state.ColumnVisibility ?? new(),
                ColumnWidths = state.ColumnWidths ?? new(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt or unreadable: safe fallback to defaults.
            return UiState.Default;
        }
    }

    /// <summary>Read-modify-write: loads the current state fresh, applies
    /// <paramref name="mutate"/>, saves the result, and returns the state it
    /// attempted to save. The fresh load is the point: a caller that instead
    /// mutates its own cached snapshot and calls <see cref="Save"/> can clobber
    /// a preference another consumer saved since that snapshot was loaded — the
    /// second writer's stale copy of the first writer's fields wins (Codex P2,
    /// PR #45). Every consumer with more than one write site must funnel
    /// through here rather than cache-mutate-Save.</summary>
    public UiState Update(Func<UiState, UiState> mutate)
    {
        UiState updated = mutate(Load());
        Save(updated); // best-effort: a failed save costs only persistence
        return updated;
    }

    /// <summary>Saves UI state. Returns false on write failure, never throws.</summary>
    public bool Save(UiState state)
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path)!;
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(dir);
            else
                Directory.CreateDirectory(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            string tmp = Path + ".tmp";
            File.Delete(tmp); // no-op if missing; race-free under the single-threaded contract
            using (FileStream stream = OperatingSystem.IsWindows()
                ? File.Create(tmp)
                : new FileStream(tmp, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }))
                JsonSerializer.Serialize(stream, state, JsonOptions);
            File.Move(tmp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Per-user UI state: density mode, column visibility, and column widths.
/// Load-mutate-save DTO — not thread-safe; UI-thread use only.
/// </summary>
public sealed record UiState(
    bool CompactDensity,
    Dictionary<string, bool> ColumnVisibility,
    Dictionary<string, double> ColumnWidths,
    RailGroupingMode RailGrouping = RailGroupingMode.Auto,
    bool RailHealthyExpanded = false,
    AppTheme Theme = AppTheme.System,
    Guid? ScopeHostId = null)
{
    /// <summary>Factory default: standard density, all columns visible, auto widths.
    /// Fresh instance per call — the dictionaries are mutable, so a shared
    /// singleton would leak load-mutate edits into later default loads.</summary>
    public static UiState Default => new(false, [], []);
}
