using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

public partial class EventLogView : UserControl
{
    private readonly RowClassBinder<EventLogRow> _rowBinder;
    private EventLogViewModel? _boundVm;
    private ScrollBar? _verticalScrollBar;

    // Set while our own ScrollIntoView is repositioning the grid to the newest
    // row. The vertical scrollbar's Value change that this produces must NOT be
    // read as the user scrolling away from the bottom (which pauses Following) —
    // that would be a feedback loop: Following → auto-scroll → offset change →
    // clears Following. The flag is cleared on a Background-priority post, which
    // runs strictly after the layout pass that applies the pending scroll, so the
    // auto-scroll's own offset change is always seen under the guard.
    private bool _autoScrolling;

    // Test seam (fake's-observed-calls canon): headless row realization can't be
    // relied on to prove ScrollIntoView reached the last row, so tests substitute
    // this to observe the requested target instead of pixel-probing.
    internal Action<object>? ScrollRowIntoViewOverride;

    public EventLogView()
    {
        InitializeComponent();

        // Row-severity tints track the holder (design 2c). Post-reconcile an
        // in-place Data swap does not re-fire LoadingRow, so classes must follow
        // the holder's PropertyChanged — RowClassBinder owns that whole
        // lifecycle (load / re-apply / recycle / unload / detach-drain).
        _rowBinder = RowClassBinder<EventLogRow>.Attach(Grid, static (row, holder) =>
        {
            row.Classes.Set("warning", holder.Data.Priority == EventLogPriority.Warning);
            row.Classes.Set("error", holder.Data.Priority == EventLogPriority.Error);
        });

        Grid.TemplateApplied += OnGridTemplateApplied;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Test seam (InternalsVisibleTo): live row-class subscriptions.
    /// The teardown-drain regression test pins this to 0 after detach.</summary>
    internal int RowSubscriptionCount => _rowBinder.Count;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnhookViewModel();
        _boundVm = DataContext as EventLogViewModel;
        if (_boundVm is not null)
        {
            _boundVm.Rows.CollectionChanged += OnRowsCollectionChanged;
            _boundVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    // Idempotent: called both on DataContext swaps and on visual-tree detach
    // (the two paths can run in either order), so it must be safe to hit twice.
    private void UnhookViewModel()
    {
        if (_boundVm is null)
            return;
        _boundVm.Rows.CollectionChanged -= OnRowsCollectionChanged;
        _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        _boundVm = null;
    }

    // The DataGrid manages its own scrolling (no wrapping ScrollViewer); its
    // vertical ScrollBar template part is the offset surface. Grabbing it on
    // template-applied lets code-behind both drive Following (auto-scroll) and
    // read whether the user has scrolled away from the bottom.
    private void OnGridTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (_verticalScrollBar is not null)
            _verticalScrollBar.PropertyChanged -= OnVerticalScrollBarPropertyChanged;
        _verticalScrollBar = e.NameScope.Find<ScrollBar>("PART_VerticalScrollbar");
        if (_verticalScrollBar is not null)
            _verticalScrollBar.PropertyChanged += OnVerticalScrollBarPropertyChanged;

        // Initial catch-up: when the page is opened via the unread badge, the VM
        // already holds rows that arrived while it was hidden, so no later Add /
        // IsFollowing change fires. The grid is only now realizable, so anchor it
        // to the newest row if we are still Following — otherwise it would sit at
        // the top while the status bar claims "Following live".
        ScrollToNewestIfFollowing();
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollToNewestIfFollowing();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Turning Following back on (the "Resume following" toggle) jumps to the
        // newest row immediately; the not-at-bottom read below never re-fires it
        // because it only ever sets Following false.
        if (e.PropertyName == nameof(EventLogViewModel.IsFollowing))
            ScrollToNewestIfFollowing();
    }

    private void ScrollToNewestIfFollowing()
    {
        if (_boundVm is not { IsFollowing: true } vm || vm.Rows.Count == 0)
            return;
        var last = vm.Rows[^1];
        _autoScrolling = true;
        ScrollRowIntoView(last);
        Dispatcher.UIThread.Post(() => _autoScrolling = false, DispatcherPriority.Background);
    }

    private void ScrollRowIntoView(object item)
    {
        if (ScrollRowIntoViewOverride is { } hook)
            hook(item);
        else
            Grid.ScrollIntoView(item, null);
    }

    private void OnVerticalScrollBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty)
            return;
        if (_autoScrolling)
            return; // feedback-loop guard (see _autoScrolling)
        if (_boundVm is { IsFollowing: true } vm && !IsScrolledToBottom())
            vm.IsFollowing = false;
    }

    private bool IsScrolledToBottom()
    {
        var bar = _verticalScrollBar;
        return bar is null || bar.Maximum <= 0 || bar.Value >= bar.Maximum - 0.5;
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EventLogViewModel vm)
            return;
        var text = vm.BuildClipboardText();
        if (text.Length == 0)
            return;
        // Clipboard access stays in the view (design/plan: the VM is clipboard-free
        // and only builds the tab-separated text).
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_verticalScrollBar is not null)
        {
            _verticalScrollBar.PropertyChanged -= OnVerticalScrollBarPropertyChanged;
            _verticalScrollBar = null;
        }
        // Symmetric with the scrollbar teardown above: don't rely on the shell's
        // DataTemplate hosting nulling the DataContext on detach — an explicitly
        // set longer-lived DataContext would otherwise pin this view through the
        // VM's Rows/PropertyChanged subscriptions.
        UnhookViewModel();
    }
}

/// <summary>
/// Bool → the ConverterParameter string when true, empty string when false.
/// Backs the status bar's "Following live" right text, which shows only while
/// IsFollowing (StatusBarControl has no per-section visibility of its own).
/// </summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public static readonly BoolToTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter as string ?? "" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
