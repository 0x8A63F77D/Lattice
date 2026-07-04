using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>A work unit, from get_state.</summary>
public sealed record Workunit(string Name, string AppName, double RscFpopsEst)
{
    internal static Workunit Parse(XElement e) => new(
        ParseHelpers.GetString(e, "name"),
        ParseHelpers.GetString(e, "app_name"),
        ParseHelpers.GetDouble(e, "rsc_fpops_est"));
}
