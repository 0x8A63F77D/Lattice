using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Guards the DEBUG-only boundary of the sample fleet (PR F): the injectable
/// sample host is a walkthrough aid that must never ship. This test is NOT
/// <c>#if DEBUG</c>-gated — it runs in both configurations and flips its
/// expectation, so the Release CI legs actively verify the types are absent
/// (no trace) while a local DEBUG run confirms they are present. Reflection by
/// full name keeps it independent of the (DEBUG-only) type references themselves.
/// </summary>
public class SampleHostReleaseExclusionTests
{
    private static readonly string[] SampleTypeNames =
    [
        "Lattice.App.Infrastructure.SampleHost",
        "Lattice.App.Infrastructure.SampleHostData",
        "Lattice.App.Infrastructure.SampleGuiRpcClient",
        "Lattice.App.Infrastructure.SampleRoutingGuiRpcClient",
    ];

    [Fact]
    public void Sample_host_types_are_debug_only()
    {
        var appAssembly = typeof(App).Assembly;

        foreach (string name in SampleTypeNames)
        {
            var type = appAssembly.GetType(name, throwOnError: false);
#if DEBUG
            Assert.NotNull(type);
#else
            Assert.Null(type); // Release: no trace of the sample fleet.
#endif
        }
    }
}
