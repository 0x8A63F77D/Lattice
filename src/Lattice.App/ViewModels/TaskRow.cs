using Lattice.App.Infrastructure;

namespace Lattice.App.ViewModels;

/// <summary>
/// Closed holder for Tasks-view rows: XAML cannot name generic types, so the
/// DataGrid binds TaskRow (x:DataType) while the reconciler works against the
/// RowHolder base. Data swaps in place on value-change polls; the instance —
/// and therefore DataGrid selection — survives.
/// </summary>
public sealed class TaskRow(TaskRowKey key, TaskRowViewModel data)
    : RowHolder<TaskRowKey, TaskRowViewModel>(key, data);
