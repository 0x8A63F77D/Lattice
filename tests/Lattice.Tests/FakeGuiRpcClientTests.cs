using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class FakeGuiRpcClientTests
{
    [Fact]
    public async Task Records_calls_and_returns_scripted_values()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>([TestData.MakeMessage(seqno + 1)]),
        };
        await fake.ConnectAsync("localhost", 31416);
        Assert.True(await fake.AuthorizeAsync("pw"));
        Assert.Equal(new VersionInfo(8, 2, 0), await fake.ExchangeVersionsAsync());
        IReadOnlyList<Message> messages = await fake.GetMessagesAsync(41);
        Assert.Equal(42, messages.Single().Seqno);
        Assert.Equal(["connect:localhost:31416", "authorize", "exchange_versions", "get_messages:41"], fake.Calls);
        await fake.DisposeAsync();
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task Scripted_exception_propagates()
    {
        var fake = new FakeGuiRpcClient
        {
            OnConnect = (_, _) => throw new BoincConnectionException("refused"),
        };
        await Assert.ThrowsAsync<BoincConnectionException>(() => fake.ConnectAsync("localhost"));
    }
}
