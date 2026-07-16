using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Collections;
using Lattice.App.Aggregation;
using Lattice.App.ViewModels;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Avalonia DataGrid's sort adapter over the pure F# order (issue #57). The grid
/// never computes its own order: <see cref="Comparer"/> reads each item's
/// <see cref="ProjectRowViewModel.SortKey"/> and defers entirely to
/// <c>ProjectRows.compareRows</c>, so the rendered order always matches the VM's
/// <see cref="ProjectSort"/> state. <see cref="For"/> is the only construction
/// path — the base class's own constructor is protected.
/// </summary>
public sealed class ProjectSortDescription : DataGridSortDescription
{
    private readonly ProjectSort _sort;

    private ProjectSortDescription(ProjectSort sort) => _sort = sort;

    public static ProjectSortDescription For(ProjectSort sort) => new(sort);

    /// <summary>
    /// DefaultSort carries no column identity (it lights no header arrow); a
    /// ColumnSort's path is the column's token, matched against the column
    /// SortMemberPath. The token mapping lives in F# (ProjectRows.columnToken)
    /// where the match is compile-time total — a C# switch over the DU's int
    /// Tag would need a default arm and could only fail at runtime (Codex P2).
    /// </summary>
    public override string? PropertyPath =>
        _sort is ProjectSort.ColumnSort cs ? ProjectRows.columnToken(cs.column) : null;

    public override ListSortDirection Direction =>
        _sort is ProjectSort.ColumnSort { direction.IsDescending: true }
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

    /// <summary>The abstract member — base's OrderBy/ThenBy route through this, so
    /// they stay unoverridden.</summary>
    public override IComparer<object> Comparer =>
        Comparer<object>.Create((x, y) => ProjectRows.compareRows(_sort, KeyOf(x), KeyOf(y)));

    // Direction flip lives in F# (total match) for the same reason as the token
    // mapping above — no runtime-only DU switch in this adapter.
    public override DataGridSortDescription SwitchSortDirection() =>
        _sort is ProjectSort.ColumnSort cs
            ? For(ProjectSort.NewColumnSort(cs.column, ProjectRows.flipDirection(cs.direction)))
            : this;

    // Fail-fast: this DataGridSortDescription is only ever installed on the
    // Projects grid's SortDescriptions, whose ItemsSource is exclusively
    // ProjectRow — a foreign item type means something wired this comparer to
    // the wrong collection.
    private static RowSortKey KeyOf(object item) =>
        item is ProjectRow row
            ? row.Data.SortKey
            : throw new InvalidOperationException(
                $"ProjectSortDescription only orders ProjectRow items, got {item.GetType()}");
}
