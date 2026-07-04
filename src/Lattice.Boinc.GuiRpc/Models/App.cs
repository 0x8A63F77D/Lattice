using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>An application, from get_state.</summary>
public sealed record App(string Name, string UserFriendlyName)
{
    internal static App Parse(XElement e) => new(
        ParseHelpers.GetString(e, "name"),
        ParseHelpers.GetString(e, "user_friendly_name"));
}

/// <summary>An application version, from get_state.</summary>
public sealed record AppVersion(string AppName, int VersionNum, string Platform, string PlanClass)
{
    internal static AppVersion Parse(XElement e) => new(
        ParseHelpers.GetString(e, "app_name"),
        ParseHelpers.GetInt(e, "version_num"),
        ParseHelpers.GetString(e, "platform"),
        ParseHelpers.GetString(e, "plan_class"));
}
