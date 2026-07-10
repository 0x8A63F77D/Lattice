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
    public void Default_path_uses_lattice_config_directory()
    {
        var store = new UiStateStore();
        var expected = Path.Combine(
            Path.GetDirectoryName(Lattice.Core.LatticeConfig.DefaultPath)!,
            "ui-state.json"
        );
        Assert.Equal(expected, store.Path);
    }
}
