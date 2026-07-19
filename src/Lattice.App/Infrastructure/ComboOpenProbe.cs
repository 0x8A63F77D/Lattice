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

    /// <summary>Shared wall clock for every probe log line, so stall reports and
    /// pointer/opened events can be correlated on one timeline.</summary>
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static string Now => $"+{Clock.Elapsed.TotalSeconds:F3}s";

    /// <summary>
    /// UI-thread responsiveness monitor (owner hypothesis on #95: the click is late
    /// because the UI thread is busy with poll-update work). A background thread posts
    /// a no-op to the UI thread at <see cref="Avalonia.Threading.DispatcherPriority.Input"/>
    /// — the SAME priority pointer events dispatch at, so the measured completion
    /// latency is exactly the queueing delay a click experiences at that moment.
    /// Probes every 100 ms; logs any completion that took longer than 50 ms.
    /// </summary>
    private static void StartUiThreadStallMonitor()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        static () => { }, Avalonia.Threading.DispatcherPriority.Input);
                }
                catch (TaskCanceledException)
                {
                    return; // dispatcher shutting down
                }
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (ms > 50)
                    Console.WriteLine($"[combo-probe] {Now} UI-THREAD STALL: input-priority job waited {ms:F0}ms");
                await Task.Delay(100);
            }
        });
    }

    /// <summary>Bare-mode main window: a minimal shell-free host for one plain ComboBox.</summary>
    public static Window CreateBareWindow()
    {
        var box = new ComboBox
        {
            SelectedIndex = 0,
            ItemsSource = new[] { "All", "Running", "Waiting", "Suspended", "Uploading" },
        };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Children = { box } };
        // Diagnosis aid: LATTICE_PROBE_RING=visible|hidden adds an FAProgressRing to test
        // whether its infinite template animation keeps the dispatcher non-idle (and whether
        // IsVisible=false — the shell's loading-overlay state — stops it or not).
        if (Environment.GetEnvironmentVariable("LATTICE_PROBE_RING") is { } ringMode)
        {
            var ring = new FluentAvalonia.UI.Controls.FAProgressRing
            {
                Width = 32,
                Height = 32,
                IsVisible = ringMode is "visible" or "toggle",
            };
            panel.Children.Add(ring);
            if (ringMode == "toggle")
                _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(
                    _ =>
                    {
                        ring.IsVisible = false;
                        Console.WriteLine($"[combo-probe] {Now} ring HIDDEN (was visible from start)");
                    },
                    TaskScheduler.FromCurrentSynchronizationContext());
        }
        // Diagnosis aid: LATTICE_PROBE_TRANSITION=1 adds a Border whose Width runs a
        // repeating 200 ms transition (the Tasks grid's progress-fill motion), to test
        // whether ordinary UI-thread property transitions also starve input priority.
        if (Environment.GetEnvironmentVariable("LATTICE_PROBE_TRANSITION") is not null)
        {
            var bar = new Avalonia.Controls.Border
            {
                Height = 12,
                Width = 20,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Background = Avalonia.Media.Brushes.SlateBlue,
                Transitions = new Avalonia.Animation.Transitions
                {
                    new Avalonia.Animation.DoubleTransition
                    {
                        Property = Avalonia.Layout.Layoutable.WidthProperty,
                        Duration = TimeSpan.FromMilliseconds(200),
                    },
                },
            };
            panel.Children.Add(bar);
            var flip = false;
            Avalonia.Threading.DispatcherTimer.Run(() =>
            {
                flip = !flip;
                bar.Width = flip ? 300 : 20;
                return true;
            }, TimeSpan.FromMilliseconds(250));
        }
        // Diagnosis aid: LATTICE_PROBE_PBAR=1 adds a stock Avalonia indeterminate
        // ProgressBar (AddHostDialog's busy indicator) — an infinite style-driven
        // animation, to contrast with the ring's composition-driven one.
        if (Environment.GetEnvironmentVariable("LATTICE_PROBE_PBAR") is not null)
            panel.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 200 });
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Title = "ComboOpenProbe (bare)",
            Content = panel,
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
        Console.WriteLine($"[combo-probe] {Now} watch label={label} centerPx={centerPx.X},{centerPx.Y} " +
                          $"centerPt={centerPx.X / scaling:F0},{centerPx.Y / scaling:F0} scaling={scaling}");
        StartUiThreadStallMonitor();

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
            Console.WriteLine($"[combo-probe] {Now} watch press t=0.0ms (osTimestamp={e.Timestamp})");
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!WithinBox(e.Source))
                return;
            Console.WriteLine($"[combo-probe] {Now} watch release t={Stopwatch.GetElapsedTime(tPress).TotalMilliseconds:F1}ms (osTimestamp={e.Timestamp})");
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        box.DropDownOpened += (_, _) =>
        {
            Console.WriteLine($"[combo-probe] {Now} watch opened t={Stopwatch.GetElapsedTime(tPress).TotalMilliseconds:F1}ms");
            var popup = box.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
            var popupRoot = popup?.Child is { } child ? TopLevel.GetTopLevel(child) : null;
            popupRoot?.RequestAnimationFrame(_ =>
                Console.WriteLine($"[combo-probe] {Now} watch firstFrame t={Stopwatch.GetElapsedTime(tPress).TotalMilliseconds:F1}ms"));
        };
    }

    private static async Task RunAsync(ComboBox box, string label)
    {
        // Let the window finish first render + style warmup unrelated to the popup.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Console.WriteLine($"[combo-probe] {Now} start label={label} os={Environment.OSVersion.Platform} " +
                          $"renderScaling={(TopLevel.GetTopLevel(box)?.RenderScaling.ToString() ?? "?")}");
        StartUiThreadStallMonitor();
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
