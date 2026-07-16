using System.Globalization;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>Closed holder so XAML can use x:DataType (generics can't be named in XAML).</summary>
public sealed class ProjectRow(ProjectRowKey key, ProjectRowViewModel data)
    : RowHolder<ProjectRowKey, ProjectRowViewModel>(key, data);

/// <summary>
/// Immutable row projection for the Projects grid — one record type for both
/// hierarchy levels (parent aggregate / per-host child), discriminated by
/// <see cref="IsParent"/>; cell templates gate on it with IsVisible.
/// </summary>
public sealed record ProjectRowViewModel(
    ProjectRowKey Key,
    string MasterUrl,          // group URL on both levels (chevron CommandParameter)
    bool IsParent,
    bool IsExpanded,
    bool ShowChevron,
    string Name,               // parent: project display name; child: host name
    string HostsText,          // parent only ("2"); "" on children
    string ShareText,
    bool ShowShareBar,
    double ShareFraction,      // 0..1 of the group's max share, for the mini bar
    string AvgCreditText,
    string TotalCreditText,
    string TasksText,          // child rows: "3 tasks"; parent: ""
    ProjectStatusKind StatusKind,
    string StatusText,
    RowSortKey SortKey)
{
    public static ProjectRowViewModel Parent(ProjectGroup g, bool isAllHostsScope)
    {
        var (shareText, showBar, shareFraction) = g.Share switch
        {
            ShareSummary.UniformShare u => (Num(u.Item), true, u.Item > 0 ? 1.0 : 0.0),
            ShareSummary.VariesShare v => (
                string.Format(Strings.ProjectsShareVariesFmt, Num(v.min), Num(v.max)), false, 0.0),
            _ => throw new InvalidOperationException("unreachable: closed DU"),
        };
        return new(
            Key: ProjectRowKey.NewParentKey(g.MasterUrl),
            MasterUrl: g.MasterUrl,
            IsParent: true,
            IsExpanded: false, // set by the VM after expansion lookup
            ShowChevron: isAllHostsScope && g.Attachments.Length > 0,
            Name: g.DisplayName,
            HostsText: g.Attachments.Length.ToString(CultureInfo.InvariantCulture),
            ShareText: shareText,
            ShowShareBar: showBar,
            ShareFraction: shareFraction,
            AvgCreditText: Num(g.AvgCredit),
            TotalCreditText: Num(g.TotalCredit),
            TasksText: "",
            StatusKind: StatusKindOf(g.Status),
            StatusText: StatusTextOf(g.Status, isAllHostsScope),
            SortKey: ProjectRows.parentKey(g));
    }

    public static ProjectRowViewModel Child(ProjectGroup g, ProjectAttachment a)
    {
        var maxShare = g.Attachments.Max(x => x.ResourceShare);
        var status = ProjectRows.status(a);
        return new(
            Key: ProjectRowKey.NewChildKey(g.MasterUrl, a.HostId),
            MasterUrl: g.MasterUrl,
            IsParent: false,
            IsExpanded: false,
            ShowChevron: false,
            Name: a.HostName,
            HostsText: "",
            ShareText: Num(a.ResourceShare),
            ShowShareBar: true,
            ShareFraction: maxShare > 0 ? a.ResourceShare / maxShare : 0.0,
            AvgCreditText: Num(a.AvgCredit),
            TotalCreditText: Num(a.TotalCredit),
            TasksText: string.Format(Strings.ProjectsTaskCountFmt, a.TaskCount),
            StatusKind: KindOf(status),
            StatusText: TextOf(status),
            SortKey: ProjectRows.childKey(g, a));
    }

    // Credits and shares render as whole numbers (design 2a mock);
    // invariant culture per the repo culture rule.
    private static string Num(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);

    private static ProjectStatusKind KindOf(AttachmentStatus s) =>
        s.Tag switch
        {
            AttachmentStatus.Tags.Active => ProjectStatusKind.Active,
            AttachmentStatus.Tags.Suspended => ProjectStatusKind.Suspended,
            AttachmentStatus.Tags.NoNewTasks => ProjectStatusKind.NoNewTasks,
            _ => throw new InvalidOperationException("unreachable: closed DU"),
        };

    private static string TextOf(AttachmentStatus s) =>
        KindOf(s) switch
        {
            ProjectStatusKind.Active => Strings.ProjectsStatusActive,
            ProjectStatusKind.Suspended => Strings.ProjectsStatusSuspended,
            ProjectStatusKind.NoNewTasks => Strings.ProjectsStatusNoNewTasks,
            ProjectStatusKind.Mixed => throw new InvalidOperationException("per-host status is never Mixed"),
            _ => throw new InvalidOperationException("unreachable"),
        };

    private static ProjectStatusKind StatusKindOf(StatusSummary s) => s switch
    {
        StatusSummary.AllSame a => KindOf(a.Item),
        StatusSummary.OneDeviation d => KindOf(d.status),
        StatusSummary.MixedStatus => ProjectStatusKind.Mixed,
        _ => throw new InvalidOperationException("unreachable: closed DU"),
    };

    private static string StatusTextOf(StatusSummary s, bool isAllHostsScope) => s switch
    {
        // Single-host scope: the group holds only the selected host's
        // attachment — "on all hosts" would claim more than the scope shows.
        // OneDeviation/Mixed can't occur there (one attachment), so their
        // aggregate wording stays and the match stays total.
        StatusSummary.AllSame a when !isAllHostsScope => TextOf(a.Item),
        StatusSummary.AllSame a when KindOf(a.Item) == ProjectStatusKind.Active =>
            Strings.ProjectsStatusActiveAll,
        StatusSummary.AllSame a =>
            string.Format(Strings.ProjectsStatusAllFmt, TextOf(a.Item)),
        StatusSummary.OneDeviation d =>
            string.Format(Strings.ProjectsStatusDeviationFmt, TextOf(d.status), d.deviants, d.total),
        StatusSummary.MixedStatus m =>
            string.Format(Strings.ProjectsStatusMixedFmt, m.suspended, m.noNewTasks),
        _ => throw new InvalidOperationException("unreachable: closed DU"),
    };
}

/// <summary>Drives the status icon choice in XAML (icon+text, no pills — design 2a).</summary>
public enum ProjectStatusKind
{
    Active,
    Suspended,
    NoNewTasks,
    Mixed,
}
