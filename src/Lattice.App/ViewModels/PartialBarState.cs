namespace Lattice.App.ViewModels;

/// <summary>
/// The one copy of the partial-bar episode wiring: holds the dismissed and
/// current fingerprints, advances them per PartialBarPolicy, and applies the
/// All-hosts scope gate. Episode semantics stay in PartialBarPolicy; this
/// class only removes the three-per-view transcription of its call protocol.
/// </summary>
public sealed class PartialBarState
{
    private PartialBarPolicy.Fingerprint _dismissed = PartialBarPolicy.EmptyFingerprint;
    private PartialBarPolicy.Fingerprint _current = PartialBarPolicy.EmptyFingerprint;

    /// <summary>Advance with this rebuild's slice facts; returns whether the bar shows.</summary>
    public bool Advance(IReadOnlySet<Guid> unreachableIds, IReadOnlySet<Guid> coveredIds, bool isAllHostsScope)
    {
        _current = new PartialBarPolicy.Fingerprint(unreachableIds, coveredIds);
        (PartialBarPolicy.Fingerprint dismissed, bool visible) = PartialBarPolicy.Advance(_dismissed, _current);
        _dismissed = dismissed;
        return isAllHostsScope && visible;
    }

    public void Dismiss() => _dismissed = PartialBarPolicy.Dismiss(_current);
}
