using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;

namespace Lattice.App.ViewModels;

/// <summary>
/// The one copy of the per-host facts projection every data view feeds to
/// ViewSlice.compute. Encodes the Codex-adjudicated subtleties structurally:
/// unreachable/stale tiers span ALL hosts (scope-independent episode
/// semantics) while row materialization is gated on inScope AND isRowSource —
/// out-of-scope hosts' rows are never built (Rebuild runs every 1 s tick;
/// Codex P2, PR #37).
/// </summary>
public static class ViewSliceProjection
{
    public static Slice<TRow> Compute<TRow>(
        IReadOnlyList<HostEntry> hosts,
        ScopeSelection scope,
        Func<HostEntry, TRow[]> rowsOf)
    {
        var facts = hosts.Select(h =>
        {
            var rail = RailStateProjection.From(h.Status);
            var inScope = scope.IsAllHosts || h.Config.Id == scope.HostId;
            var isRowSource = rail == RailState.Connected && h.Snapshot is not null;
            // Positional construction: the F# record's compiler-generated
            // constructor exposes camelCase parameter names (id, inScope, ...)
            // that don't match the PascalCase field names C# named-argument
            // syntax would need, so positional is the reliable path here.
            return new HostFacts<TRow>(
                h.Config.Id,
                inScope,
                isRowSource,
                rail is RailState.Unreachable or RailState.AuthFailed,
                rail is RailState.Retrying or RailState.Unreachable,
                isRowSource ? h.Snapshot!.Timestamp : null,
                inScope && isRowSource ? rowsOf(h) : []);
        }).ToArray();
        return ViewSlice.compute(facts);
    }
}
