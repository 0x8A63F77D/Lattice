using Avalonia.Threading;

namespace Lattice.App.Infrastructure;

/// <summary>
/// The only thread-semantics surface ViewModels see. Core events arrive on
/// background threads; everything UI-side runs behind Post.
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action);
}

/// <summary>Production implementation over Avalonia's UI thread dispatcher.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static AvaloniaUiDispatcher Instance { get; } = new();
    private AvaloniaUiDispatcher() { }
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
