using Lattice.App.ViewModels;
using Lattice.Core;

namespace Lattice.App.Tests.Fakes;

/// <summary>
/// The attach-flow seam for tests that construct a <see cref="ProjectsViewModel"/>
/// without exercising attach. Single-copy so no test file has to hand-transcribe the
/// delegate's four-parameter shape: ProjectsViewModel requires the seam (there is no
/// null-seam construction state any more), and most Projects tests care about the
/// grid, not the "Add project…" dialog. The attach behaviour itself is gated by
/// ProjectsViewModelAttachTests, which scripts its own run.
/// </summary>
public static class FakeAttachFlow
{
    /// <summary>Reports immediate success without touching a daemon.</summary>
    public static Task<AttachFlowResult> NoopRun(
        Guid hostId, AttachMachine.AttachRequest request,
        IProgress<AttachMachine.Stage>? progress, CancellationToken ct) =>
        Task.FromResult(new AttachFlowResult(AttachFlowOutcome.Attached, [], null));
}
