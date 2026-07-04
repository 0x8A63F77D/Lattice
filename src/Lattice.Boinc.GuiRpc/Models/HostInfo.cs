using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Host information, from get_state.</summary>
public sealed record HostInfo(string DomainName, string OsName, string OsVersion, int NCpus, string PModel)
{
    internal static HostInfo Parse(XElement e) => new(
        ParseHelpers.GetString(e, "domain_name"),
        ParseHelpers.GetString(e, "os_name"),
        ParseHelpers.GetString(e, "os_version"),
        ParseHelpers.GetInt(e, "p_ncpus"),
        ParseHelpers.GetString(e, "p_model"));
}
