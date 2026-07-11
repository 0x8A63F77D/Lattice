using CommunityToolkit.Mvvm.ComponentModel;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Mutable identity wrapper around an immutable row record: the DataGrid binds
/// to holders, the reconciler swaps <see cref="Data"/> in place, so selection
/// (item identity) survives value-change polls. XAML cannot name generic types,
/// so each view binds a closed subclass (e.g. TaskRow).
/// </summary>
public partial class RowHolder<TKey, TRow> : ObservableObject
    where TKey : notnull
{
    public RowHolder(TKey key, TRow data)
    {
        Key = key;
        _data = data;
    }

    public TKey Key { get; }

    [ObservableProperty] private TRow _data;
}
