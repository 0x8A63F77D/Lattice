namespace Lattice.App.ViewModels;

/// <summary>Which form the snooze pill takes on a rail row (design PR H, S1).</summary>
public enum RunModePillForm
{
    /// <summary>Form A — the default time pill ("⏸ hh:mm").</summary>
    Time,
    /// <summary>Form B — the degraded 20×20 icon chip (time moves to the tooltip).</summary>
    Chip,
}

/// <summary>
/// Per-row decision for the snooze pill's form (design PR H, S1 degrade rule). The
/// default is the time pill; a row whose host text is too wide to sit beside it
/// without dropping the text below its legibility floor degrades to the compact icon
/// chip, and the time moves to the tooltip (which already carries "Snoozed until hh:mm").
///
/// One threshold. The host-text width is a char-count estimate rather than a per-frame
/// <c>TextLayout</c> measure — kept here so the decision stays a pure, Avalonia-free,
/// deterministic function (transition-table tested); the estimate is deliberately
/// approximate (host names are alphanumeric and both forms are legible, so a marginal
/// mis-pick is benign).
/// </summary>
public static class RunModePillPolicy
{
    // The open pane is 260px; less the ListBoxItem 8px×2 padding leaves ~244px of row
    // content. The 24px state icon + its 4px text margin take ~28px. Form A (the time
    // pill) is ~62px + a 4px left margin. The host text must keep a ~110px legibility
    // floor. All in device-independent px (rail is fixed at RenderScaling considerations
    // aside — the pane width is constant while open; the compact 48px rail hides the pill
    // entirely, so no other width regime reaches this policy).
    private const double RowContentWidth = 244.0;
    private const double IconArea = 28.0;
    private const double TimePillWidth = 66.0;
    private const double TextFloor = 110.0;

    // Approx glyph advance: name is 13px (semibold), subtext 11px.
    private const double NameCharPx = 7.0;
    private const double SubtextCharPx = 5.8;

    /// <summary>
    /// Decides the pill form from the rail row's host-text lengths. The wider of name /
    /// subtext drives it: reserve the greater of that estimate and the floor for the
    /// text, and if what remains beside the state icon still fits the time pill, keep
    /// Form A; otherwise degrade to the chip.
    /// </summary>
    public static RunModePillForm Decide(int nameLength, int subtextLength)
    {
        double textWidth = System.Math.Max(nameLength * NameCharPx, subtextLength * SubtextCharPx);
        double pillBudget = RowContentWidth - IconArea - System.Math.Max(textWidth, TextFloor);
        return pillBudget >= TimePillWidth ? RunModePillForm.Time : RunModePillForm.Chip;
    }
}
