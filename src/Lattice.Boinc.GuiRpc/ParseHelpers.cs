using System.Globalization;
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

internal static class ParseHelpers
{
    internal static string GetString(XElement parent, string name) =>
        (string?)parent.Element(name) ?? string.Empty;

    internal static bool GetBool(XElement parent, string name)
    {
        XElement? e = parent.Element(name);
        if (e is null) return false;
        string v = ((string)e).Trim();
        return v.Length == 0 || v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    internal static int GetInt(XElement parent, string name, int defaultValue = 0) =>
        int.TryParse((string?)parent.Element(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : defaultValue;

    internal static double GetDouble(XElement parent, string name, double defaultValue = 0) =>
        double.TryParse((string?)parent.Element(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : defaultValue;

    internal static DateTimeOffset? GetTimestamp(XElement parent, string name)
    {
        double seconds = GetDouble(parent, name, double.NaN);
        if (double.IsNaN(seconds) || seconds <= 0) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
    }
}
