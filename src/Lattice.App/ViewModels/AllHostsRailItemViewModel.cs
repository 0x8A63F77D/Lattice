using CommunityToolkit.Mvvm.ComponentModel;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>The aggregate first entry of the hosts rail (design §App shell zone 2).</summary>
public sealed partial class AllHostsRailItemViewModel : ObservableObject
{
    public string Name => Strings.AllHosts;

    [ObservableProperty] private string _subtext = "";
    [ObservableProperty] private string? _tooltip;

    /// <summary>connected/total drive both the subtext and the partial styling.</summary>
    public void Update(int connected, int total)
    {
        Subtext = connected == total
            ? string.Format(Strings.AllHostsCountFmt, total)
            : string.Format(Strings.AllHostsPartialFmt, connected, total);
        Tooltip = $"{Name} — {Subtext}";
    }
}
