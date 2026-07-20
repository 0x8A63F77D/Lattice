using CommunityToolkit.Mvvm.ComponentModel;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Backing state for a data view's control-op failure InfoBar (design 2.5):
/// holds the LAST failure only — a newer report replaces an older one — and is
/// cleared both by the user dismissing the bar and by any subsequent op
/// success on the same view. One instance per data-view VM; the view binds the
/// InfoBar's IsOpen/Title/Message to it and routes the bar's Closed event to
/// <see cref="Clear"/>.
/// </summary>
public sealed partial class ControlFailureSurface : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";

    /// <summary>Shows (or replaces) the last failure. Texts are display-only.</summary>
    public void Report(string title, string message)
    {
        Title = title;
        Message = message;
        IsOpen = true;
    }

    /// <summary>Closes the bar (user dismissal or a subsequent op success).</summary>
    public void Clear() => IsOpen = false;
}
