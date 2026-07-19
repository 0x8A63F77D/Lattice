#if DEBUG
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Lattice.App.Infrastructure;

/// <summary>
/// DEBUG-only measurement instrumentation for issue #95 (Tasks "State" ComboBox
/// dropdown-open latency). Never compiled into Release. Activated by env var
/// <c>LATTICE_COMBO_PROBE=tasks</c> (probe the real StateFilterBox inside the
/// shell) or <c>LATTICE_COMBO_PROBE=bare</c> (probe a bare ComboBox in a minimal
/// window, replacing the shell entirely — isolates FA popup cost from anything
/// Tasks/command-bar specific).
///
/// Methodology: programmatically setting IsDropDownOpen=true enters the same
/// popup-open path as a pointer click (ComboBox's pointer handler just flips
/// that property); what it skips is only OS input routing/hit-testing. Three
/// timestamps per open cycle, all relative to the IsDropDownOpen write:
///   set     — the synchronous IsDropDownOpen=true write returning (popup host
///             creation + dropdown content template inflation + layout happen
///             synchronously inside it)
///   opened  — ComboBox.DropDownOpened firing
///   frame   — first RequestAnimationFrame callback on the popup's own TopLevel
///             (PopupRoot), i.e. the first rendered frame of the dropdown: the
///             closest programmatic proxy for "the user sees the menu".
/// Six cycles distinguish first-open (host creation / style-resolution warmup)
/// from steady-state (per-open transition or realization work).
/// </summary>
internal static class ComboOpenProbe
{
    public const string EnvVar = "LATTICE_COMBO_PROBE";

    public static string? Mode => Environment.GetEnvironmentVariable(EnvVar);

    private static bool _attached;

    /// <summary>Bare-mode main window: a minimal shell-free host for one plain ComboBox.</summary>
    public static Window CreateBareWindow()
    {
        var box = new ComboBox
        {
            SelectedIndex = 0,
            ItemsSource = new[] { "All", "Running", "Waiting", "Suspended", "Uploading" },
        };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Title = "ComboOpenProbe (bare)",
            Content = new StackPanel { Margin = new Avalonia.Thickness(24), Children = { box } },
        };
        Attach(box, "bare");
        return window;
    }

    /// <summary>Hook a ComboBox for probing; runs once per process (TasksView is recreated per navigation).</summary>
    public static void Attach(ComboBox box, string label)
    {
        if (_attached)
            return;
        _attached = true;
        if (Mode == "watch")
            box.AttachedToVisualTree += (_, _) => _ = WatchAsync(box, label);
        else
            box.AttachedToVisualTree += (_, _) => _ = RunAsync(box, label);
    }

    /// <summary>
    /// Passive real-input mode (LATTICE_COMBO_PROBE=watch): instruments the box with
    /// tunneled pointer handlers + DropDownOpened + first-frame hooks, prints the box's
    /// screen-coordinate center (for an external click-synthesis tool), and stays open
    /// until killed. Measures the true OS-click → popup-visible path that RunAsync's
    /// programmatic IsDropDownOpen writes bypass.
    /// </summary>
    private static async Task WatchAsync(ComboBox box, string label)
    {
        try
        {
            await WatchCoreAsync(box, label);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[combo-probe] watch FAILED: {ex}");
        }
    }

    private static async Task WatchCoreAsync(ComboBox box, string label)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        var topLevel = TopLevel.GetTopLevel(box);
        var scaling = topLevel?.RenderScaling ?? 1.0;
        var centerPx = box.PointToScreen(new Avalonia.Point(box.Bounds.Width / 2, box.Bounds.Height / 2));
        Console.WriteLine($"[combo-probe] watch label={label} centerPx={centerPx.X},{centerPx.Y} " +
                          $"centerPt={centerPx.X / scaling:F0},{centerPx.Y / scaling:F0} scaling={scaling}");

        if (topLevel is null)
        {
            Console.WriteLine("[combo-probe] watch FAILED: no TopLevel");
            return;
        }

        // Timestamp hooks live on the TopLevel's tunnel, not the box's: tunnel runs
        // root→target, so these fire before ANY handler on the box itself — including
        // ComboBoxPressOpenBehavior's press-open tunnel handler, which otherwise opens
        // the dropdown (raising DropDownOpened synchronously) before tPress is stamped.
        long tPress = 0;
        bool WithinBox(object? source) =>
            source is Avalonia.Visual v && (v == box || box.IsVisualAncestorOf(v));
        topLevel.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!WithinBox(e.Source))
                return;
            tPress = Stopwatch.GetTimestamp();
            Console.WriteLine($"[combo-probe] watch press t=0.0ms");
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!WithinBox(e.Source))
                return;
            Console.WriteLine($"[combo-probe] watch release t={Stopwatch.GetElapsedTime(tPress).TotalMilliseconds:F1}ms");
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        box.DropDownOpened += (_, _) =>
        {
            Console.WriteLine($"[combo-probe] watch opened t={Stopwatch.GetElapsedTime(tPress).TotalMilliseconds:F1}ms");
            var popup = box.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
            var popupRoot = popup?.Child is { } child ? TopLevel.GetTopLevel(child) : null;
            popupRoot?.RequestAnimationFrame(_ =>
                Console.WriteLine($"[combo-probe] watch firstFrame t={Stopwatch.GetElapsedTime(tPress).TotalMilliseconds:F1}ms"));
        };
    }

    private static async Task RunAsync(ComboBox box, string label)
    {
        // Let the window finish first render + style warmup unrelated to the popup.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Console.WriteLine($"[combo-probe] start label={label} os={Environment.OSVersion.Platform} " +
                          $"renderScaling={(TopLevel.GetTopLevel(box)?.RenderScaling.ToString() ?? "?")}");
        for (var i = 1; i <= 6; i++)
        {
            var openedTcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameTcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t0 = Stopwatch.GetTimestamp();

            void OnOpened(object? sender, EventArgs e)
            {
                openedTcs.TrySetResult(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                var popup = box.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
                var popupRoot = popup?.Child is { } child ? TopLevel.GetTopLevel(child) : null;
                if (popupRoot is null)
                    frameTcs.TrySetResult(double.NaN);
                else
                    popupRoot.RequestAnimationFrame(_ =>
                        frameTcs.TrySetResult(Stopwatch.GetElapsedTime(t0).TotalMilliseconds));
            }

            box.DropDownOpened += OnOpened;
            box.IsDropDownOpen = true;
            var setMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            var timeout = Task.Delay(TimeSpan.FromSeconds(5));
            var openedMs = await Task.WhenAny(openedTcs.Task, timeout) == timeout ? double.NaN : openedTcs.Task.Result;
            var frameMs = await Task.WhenAny(frameTcs.Task, timeout) == timeout ? double.NaN : frameTcs.Task.Result;
            box.DropDownOpened -= OnOpened;

            Console.WriteLine($"[combo-probe] {label} open#{i}: set={setMs:F1}ms opened={openedMs:F1}ms firstFrame={frameMs:F1}ms");

            await Task.Delay(400);
            box.IsDropDownOpen = false;
            await Task.Delay(400);
        }
        Console.WriteLine("[combo-probe] done");
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
#endif
