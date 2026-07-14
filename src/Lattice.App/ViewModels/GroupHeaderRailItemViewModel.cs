using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>A status-group header row in the hosts rail (design 3a). Attention is
/// always expanded (not collapsible); Healthy toggles, the shell persists.</summary>
public sealed partial class GroupHeaderRailItemViewModel : ObservableObject
{
    public GroupHeaderRailItemViewModel(RailTier tier, int count, bool expanded)
    {
        Tier = tier;
        _expanded = expanded;
        // Exhaustive over the RailTier DU (decisions §2). The `_ => throw` is a SANCTIONED
        // guard, NOT a silent catch-all: it mirrors RailStateProjection.From so that adding
        // an M3 tier (e.g. Offline) fails loudly here instead of mislabelling it "Healthy · N".
        // Do not "simplify" it back to a two-way ternary. (RailTier is an F# DU, so this uses
        // its generated Is<Case> predicates as property patterns, not enum constants.)
        Text = tier switch
        {
            { IsAttention: true } => string.Format(Strings.RailGroupAttentionFmt, count),
            { IsHealthy: true } => string.Format(Strings.RailGroupHealthyFmt, count),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier,
                "RailTier grew — add its group-header label (decisions §2)."),
        };
    }

    public RailTier Tier { get; }
    public string Text { get; }

    /// <summary>Attention is pinned open (decisions spec §2); only the others show a chevron.</summary>
    public bool IsCollapsible => !Tier.Equals(RailTier.Attention);

    [ObservableProperty] private bool _expanded;

    /// <summary>Raised by <see cref="ToggleCommand"/>; the shell flips + persists + recomputes.</summary>
    public event EventHandler<RailTier>? ToggleRequested;

    [RelayCommand]
    private void Toggle()
    {
        if (IsCollapsible)
            ToggleRequested?.Invoke(this, Tier);
    }
}
