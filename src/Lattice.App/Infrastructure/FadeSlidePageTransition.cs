using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace Lattice.App.Infrastructure;

/// <summary>
/// The view-switch page transition (design card 1h / shell-design spec §11): the incoming
/// page fades in (opacity 0 → 1) while rising <see cref="VerticalOffset"/> px (translateY
/// 8 → 0); the outgoing page fades out. Runs over <see cref="Duration"/> with the Fluent
/// <c>decelerateMid</c> curve (cubic-bezier(0, 0, 0, 1)).
///
/// <para>Motion is cosmetic and never gates data: <see cref="TransitioningContentControl"/>
/// assigns its <c>Content</c> (the new page view-model) synchronously the moment the bound
/// value changes; this transition only animates the visual swap of the already-bound
/// presenters. The value-first invariant is therefore structural — the animation cannot delay
/// a data update because it runs after the content is already in place.</para>
///
/// <para><see cref="Duration"/> is a plain property so the headless wiring test can assert the
/// timing without judging the visual feel (duration/curve are owner-gated per the wave's
/// merge-gate policy; presence/timing are machine-gated).</para>
/// </summary>
public sealed class FadeSlidePageTransition : Avalonia.Animation.IPageTransition
{
    /// <summary>The incoming page's rise distance in device-independent pixels (design: 8 px).</summary>
    public double VerticalOffset { get; set; } = 8;

    /// <summary>Transition length (design: 150 ms).</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>The Fluent <c>decelerateMid</c> curve — cubic-bezier(0, 0, 0, 1).</summary>
    public Easing Easing { get; set; } = new SplineEasing(0, 0, 0, 1);

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        var animations = new List<Task>(2);

        if (from is not null)
        {
            var fadeOut = new Animation
            {
                Duration = Duration,
                Easing = Easing,
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                },
            };
            animations.Add(fadeOut.RunAsync(from, cancellationToken));
        }

        if (to is not null)
        {
            to.IsVisible = true;
            var fadeInRise = new Animation
            {
                Duration = Duration,
                Easing = Easing,
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters =
                        {
                            new Setter(Visual.OpacityProperty, 0d),
                            new Setter(TranslateTransform.YProperty, VerticalOffset),
                        },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters =
                        {
                            new Setter(Visual.OpacityProperty, 1d),
                            new Setter(TranslateTransform.YProperty, 0d),
                        },
                    },
                },
            };
            animations.Add(fadeInRise.RunAsync(to, cancellationToken));
        }

        await Task.WhenAll(animations).ConfigureAwait(true);

        // A cancelled swap (the user switched again mid-transition) leaves the outgoing
        // page to the next transition; only hide it on a completed run.
        if (from is not null && !cancellationToken.IsCancellationRequested)
            from.IsVisible = false;
    }
}
