using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Lattice.App.Infrastructure;

/// <summary>Single owner of the app theme (UiState.Theme): live value + persistence,
/// and applies it to the running Application. System => ThemeVariant.Default, which
/// FluentAvaloniaTheme resolves by following the OS (design 2d/1f).</summary>
public sealed class ThemePreference
{
    private readonly UiStateStore _store;

    public ThemePreference(UiStateStore store)
    {
        _store = store;
        Value = store.Load().Theme;
        Apply();
    }

    public AppTheme Value { get; private set; }

    public void Set(AppTheme value)
    {
        if (Value == value) return;
        Value = value;
        _store.Update(s => s with { Theme = value });
        Apply();
    }

    private void Apply()
    {
        // No-op both when headless without an app (Application.Current is null — plain,
        // non-Avalonia unit tests) AND when an Application singleton exists process-wide
        // but the calling thread doesn't own its dispatcher: xunit's plain [Fact] tests
        // run on threadpool threads, and once ANY AvaloniaFact test has run earlier in the
        // same test process, Application.Current is a live, non-null singleton owned by a
        // different (UI) thread — writing a StyledProperty on it from here would throw
        // "calling thread cannot access this object". Real app construction always happens
        // on the UI thread, so this guard never no-ops in production.
        if (Application.Current is not { } app || !Dispatcher.UIThread.CheckAccess()) return;
#pragma warning disable CS8524 // No `_` arm on purpose: CS8509 (a new NAMED AppTheme left
        // unhandled) must stay a build error so the theme mapping is revisited. CS8524 is the
        // residual "unnamed enum value" case — an out-of-range cast like (AppTheme)999, unreachable
        // for a well-formed value — and is suppressed here; a `_` arm would silence CS8509 too and
        // defeat the guard. System => ThemeVariant.Default (FluentAvaloniaTheme follows the OS).
        // Same pattern as RailTierProjection.
        app.RequestedThemeVariant = Value switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.System => ThemeVariant.Default,
        };
#pragma warning restore CS8524
    }
}
