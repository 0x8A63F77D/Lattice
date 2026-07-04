using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>An attached project, from get_state. MasterUrl is the stable identity key.</summary>
public sealed record Project(
    string MasterUrl,
    string ProjectName,
    double UserTotalCredit,
    double UserExpavgCredit,
    double HostTotalCredit,
    double HostExpavgCredit,
    bool SuspendedViaGui,
    bool DontRequestMoreWork)
{
    internal static Project Parse(XElement e) => new(
        ParseHelpers.GetString(e, "master_url"),
        ParseHelpers.GetString(e, "project_name"),
        ParseHelpers.GetDouble(e, "user_total_credit"),
        ParseHelpers.GetDouble(e, "user_expavg_credit"),
        ParseHelpers.GetDouble(e, "host_total_credit"),
        ParseHelpers.GetDouble(e, "host_expavg_credit"),
        ParseHelpers.GetBool(e, "suspended_via_gui"),
        ParseHelpers.GetBool(e, "dont_request_more_work"));
}
