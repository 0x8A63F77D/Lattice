using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// UiStateStore persists per-user UI preferences (density, column visibility/widths).
/// Corrupt or missing files → safe defaults. Write failures never throw; Load always succeeds.
/// </summary>
public class UiStateStoreTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Missing_file_loads_defaults()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        try
        {
            var store = new UiStateStore(path);
            var state = store.Load();

            Assert.False(state.CompactDensity);
            Assert.Empty(state.ColumnVisibility);
            Assert.Empty(state.ColumnWidths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Default_loads_are_isolated_from_earlier_mutations()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        try
        {
            var store = new UiStateStore(path);

            // Load-mutate is the documented usage; a shared Default singleton
            // would leak this mutation into every later default load.
            var first = store.Load();
            first.ColumnVisibility["deadline"] = false;
            first.ColumnWidths["name"] = 240;

            var second = store.Load();
            Assert.Empty(second.ColumnVisibility);
            Assert.Empty(second.ColumnWidths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_json_loads_defaults()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            var store = new UiStateStore(path);
            var state = store.Load();

            Assert.False(state.CompactDensity);
            Assert.Empty(state.ColumnVisibility);
            Assert.Empty(state.ColumnWidths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Null_dictionaries_in_valid_json_load_as_empty()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        // Syntactically valid JSON with null dicts (hand-edited or older-version file):
        // must normalize to empty, never surface null to consumers.
        File.WriteAllText(path, """{"compactDensity":true,"columnVisibility":null,"columnWidths":null}""");
        try
        {
            var store = new UiStateStore(path);
            var state = store.Load();

            Assert.True(state.CompactDensity);
            Assert.NotNull(state.ColumnVisibility);
            Assert.Empty(state.ColumnVisibility);
            Assert.NotNull(state.ColumnWidths);
            Assert.Empty(state.ColumnWidths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Pre_92_json_without_exitOnClose_loads_as_null()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        // A pre-#92 ui-state.json has no "exitOnClose" key: it must deserialize to
        // null (= "platform default", resolved later by TrayResidencyDefaults), never
        // a hard-coded bool that would override the per-platform default silently.
        File.WriteAllText(path, """{"compactDensity":false,"columnVisibility":{},"columnWidths":{}}""");
        try
        {
            var state = new UiStateStore(path).Load();
            Assert.Null(state.ExitOnClose);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExitOnClose_round_trips_when_set()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        try
        {
            var store = new UiStateStore(path);
            store.Save(UiState.Default with { ExitOnClose = true });
            Assert.True(store.Load().ExitOnClose);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Valid_json_round_trips()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        try
        {
            var store = new UiStateStore(path);
            var original = new UiState(
                CompactDensity: true,
                ColumnVisibility: new Dictionary<string, bool> { { "Name", true }, { "Status", false } },
                ColumnWidths: new Dictionary<string, double> { { "Name", 150.0 }, { "Status", 100.0 } }
            );

            bool saved = store.Save(original);
            Assert.True(saved);
            Assert.True(File.Exists(path));

            var loaded = store.Load();
            Assert.Equal(original.CompactDensity, loaded.CompactDensity);
            Assert.Equal(original.ColumnVisibility, loaded.ColumnVisibility);
            Assert.Equal(original.ColumnWidths, loaded.ColumnWidths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_failure_returns_false_and_never_throws()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        const string original = """{"compactDensity":false,"columnVisibility":{},"columnWidths":{}}""";
        File.WriteAllText(path, original);

        // Make the file unwritable, per platform.
        FileStream? windowsLock = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                windowsLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.None);
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            var store = new UiStateStore(path);
            var state = new UiState(true, new Dictionary<string, bool>(), new Dictionary<string, double>());

            bool result = store.Save(state);
            Assert.False(result);
        }
        finally
        {
            windowsLock?.Dispose();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            // Original content unchanged (check after restoring permissions).
            Assert.Equal(original, File.ReadAllText(path));
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Update_applies_the_mutation_to_a_freshly_loaded_state_not_a_stale_argument()
    {
        // Codex P2 (PR #45): Update must load fresh from disk before applying
        // the mutation, so a write that landed on disk after some other holder
        // cached a UiState snapshot is preserved rather than clobbered.
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        try
        {
            var store = new UiStateStore(path);

            // A "foreign" write lands directly on disk, as if a second
            // consumer of this store already saved a preference.
            store.Save(new UiState(
                CompactDensity: false,
                ColumnVisibility: new Dictionary<string, bool> { ["Elapsed"] = false },
                ColumnWidths: new Dictionary<string, double>()));

            var updated = store.Update(s => s with { CompactDensity = true });

            Assert.True(updated.CompactDensity);
            Assert.True(updated.ColumnVisibility.TryGetValue("Elapsed", out var elapsedVisible));
            Assert.False(elapsedVisible);

            var reloaded = store.Load();
            Assert.True(reloaded.CompactDensity);
            Assert.False(reloaded.ColumnVisibility["Elapsed"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Default_path_uses_lattice_config_directory()
    {
        var store = new UiStateStore();
        var expected = Path.Combine(
            Path.GetDirectoryName(Lattice.Core.LatticeConfig.DefaultPath)!,
            "ui-state.json"
        );
        Assert.Equal(expected, store.Path);
    }

    [Fact]
    public void Rail_and_theme_preferences_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-ui-{Guid.NewGuid():N}.json");
        var store = new UiStateStore(path);
        var scoped = Guid.NewGuid();
        store.Save(UiState.Default with
        {
            RailGrouping = RailGroupingMode.Grouped,
            RailHealthyExpanded = true,
            Theme = AppTheme.Dark,
            ScopeHostId = scoped,
        });

        var loaded = new UiStateStore(path).Load();
        Assert.Equal(RailGroupingMode.Grouped, loaded.RailGrouping);
        Assert.True(loaded.RailHealthyExpanded);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(scoped, loaded.ScopeHostId);
        File.Delete(path);
    }

    [Fact]
    public void Numeric_rail_grouping_token_loads_defaults()
    {
        // Codex P2 (round 3): a corrupt/hand-edited file with a numeric enum
        // token (e.g. an old/foreign build's raw enum value) must never
        // materialize as an out-of-range RailGroupingMode — the exhaustive
        // switches in ShellViewModel.MapOverride have no fallback arm and
        // would throw SwitchExpressionException at startup. The converter
        // must reject integer tokens so Load's catch falls back to defaults.
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        File.WriteAllText(path, """{"compactDensity":false,"columnVisibility":{},"columnWidths":{},"railGrouping":999}""");
        try
        {
            var store = new UiStateStore(path);
            var state = store.Load();

            Assert.Equal(RailGroupingMode.Auto, state.RailGrouping);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Numeric_theme_token_loads_defaults()
    {
        // Same corruption class as above, for AppTheme (ThemePreference.Apply /
        // ThemeLabelConverter are the exhaustive switches at risk).
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        File.WriteAllText(path, """{"compactDensity":false,"columnVisibility":{},"columnWidths":{},"theme":999}""");
        try
        {
            var store = new UiStateStore(path);
            var state = store.Load();

            Assert.Equal(AppTheme.System, state.Theme);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Non_numeric_column_width_token_loads_defaults()
    {
        // Column-width persistence (#120): a hand-edited width that is not even a
        // number is an invalid TOKEN — System.Text.Json rejects it loudly (throws
        // JsonException), which Load's catch turns into a safe fallback to
        // defaults rather than a startup crash. (Semantically-invalid but
        // syntactically-valid values like -50 are a separate concern, ignored
        // per-column at restore by ColumnWidthPolicy so they never reach here.)
        var dir = TempDir();
        var path = Path.Combine(dir, "ui-state.json");
        File.WriteAllText(path,
            """{"compactDensity":false,"columnVisibility":{},"columnWidths":{"tasks/Project":"wide"}}""");
        try
        {
            var state = new UiStateStore(path).Load();
            Assert.Empty(state.ColumnWidths);
            Assert.False(state.CompactDensity);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Legacy_state_file_without_new_fields_loads_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-ui-{Guid.NewGuid():N}.json");
        // A pre-rework file: only the original three members present.
        File.WriteAllText(path, """{"compactDensity":true,"columnVisibility":{},"columnWidths":{}}""");
        var loaded = new UiStateStore(path).Load();
        Assert.True(loaded.CompactDensity);
        Assert.Equal(RailGroupingMode.Auto, loaded.RailGrouping);
        Assert.Equal(AppTheme.System, loaded.Theme);
        Assert.Null(loaded.ScopeHostId);   // absent field => All hosts
        File.Delete(path);
    }
}
