using Lattice.App.Localization;
using Xunit;

namespace Lattice.App.Tests;

public class LocalizationTests
{
    [Fact]
    public void Strings_class_is_generated_public_and_resolves_keys()
    {
        Assert.Equal("Lattice", Strings.AppTitle);
    }

    [Fact]
    public void Every_resx_key_resolves_to_a_nonempty_string()
    {
        // Drift guard: enumerate the embedded resource table and assert every
        // entry is a non-empty string. Key renames are caught at compile time
        // by the generated accessors; what this actually guards against is
        // entries with empty values (or non-string entries) slipping into the
        // table unnoticed.
        var rm = Strings.ResourceManager;
        var set = rm.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true)!;
        var count = 0;
        foreach (System.Collections.DictionaryEntry entry in set)
        {
            Assert.True(entry.Value is string,
                $"resx key '{entry.Key}' is not a string (actual type: {entry.Value?.GetType().FullName ?? "null"})");
            Assert.False(string.IsNullOrEmpty((string)entry.Value!),
                $"resx key '{entry.Key}' has an empty value");
            count++;
        }
        Assert.True(count >= 1);
    }
}
