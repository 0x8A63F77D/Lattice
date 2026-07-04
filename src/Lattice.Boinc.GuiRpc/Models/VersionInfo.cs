using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>BOINC core client version, from exchange_versions or get_state.</summary>
public sealed record VersionInfo(int Major, int Minor, int Release)
{
    internal static VersionInfo Parse(XElement e) => new(
        ParseHelpers.GetInt(e, "major"),
        ParseHelpers.GetInt(e, "minor"),
        ParseHelpers.GetInt(e, "release"));

    public override string ToString() => $"{Major}.{Minor}.{Release}";
}
