using Xunit;

namespace Lattice.Tests;

public class GateCheckTests
{
    [Fact]
    public void Intentionally_fails_to_verify_the_gate() => Assert.True(false);
}
