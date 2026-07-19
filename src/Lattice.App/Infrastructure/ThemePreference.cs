using Avalonia;
using Avalonia.Styling;

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
        // Construction is PURE: it loads the persisted value but does NOT touch the
        // UI-thread-affine Application.Current.RequestedThemeVariant (#101). A ctor that
        // wrote global UI state raced whatever [AvaloniaFact] session owned Application.Current
        // when a plain [Fact] built this off-thread. The composition root applies the initial
        // theme explicitly on the UI thread via ApplyInitial(); Set() applies subsequent changes.
    }

    public AppTheme Value { get; private set; }

    /// <summary>Applies the persisted theme to the running Application ONCE at startup.
    /// The composition root calls this from the UI thread (App.OnFrameworkInitializationCompleted),
    /// keeping construction free of UI-thread-affine writes (#101).</summary>
    public void ApplyInitial() => Apply();

    public void Set(AppTheme value)
    {
        if (Value == value) return;
        Value = value;
        _store.Update(s => s with { Theme = value });
        Apply();
    }

    private void Apply()
    {
        // No-op when headless without an app (Application.Current is null — plain,
        // non-Avalonia unit tests). Every real caller runs on the UI thread: ApplyInitial()
        // from the composition root at startup, and Set() from the bound Settings ComboBox.
        // Construction no longer applies (see ctor), so there is no off-thread caller to guard
        // against — an off-thread misuse SHOULD throw loudly (VerifyAccess) rather than silently
        // no-op, so the former Dispatcher.UIThread.CheckAccess() mask is deliberately gone.
        if (Application.Current is not { } app) return;
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
