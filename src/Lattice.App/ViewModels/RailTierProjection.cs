using Lattice.App.Aggregation;

namespace Lattice.App.ViewModels;

/// <summary>
/// Buckets the five rail visuals into the two many-hosts status groups (design 3a;
/// decisions spec §2 — owner-simplified to Attention + Healthy). Total, no wildcard:
/// adding a RailState case must force a choice here.
/// </summary>
public static class RailTierProjection
{
#pragma warning disable CS8524 // No `_` arm on purpose: CS8509 (a new NAMED RailState left
    // unhandled) must stay a build error so the taxonomy is revisited. CS8524 is the residual
    // "unnamed enum value" case — an out-of-range cast like (RailState)999, unreachable for a
    // well-formed RailState — and is suppressed here; a `_` arm would silence CS8509 too and
    // defeat the guard. (No repo precedent for this pragma — RailTierProjection is the first
    // no-`_` enum switch; only CS1591 doc pragmas exist today.)
    public static RailTier From(RailState state) => state switch
    {
        RailState.Unreachable or RailState.AuthFailed or RailState.Retrying => RailTier.Attention,
        RailState.Connected or RailState.Connecting => RailTier.Healthy,
    };
#pragma warning restore CS8524
}
