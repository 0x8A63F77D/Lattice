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
    public async Task Attach_flow_hooks_record_calls_without_credentials()
    {
        var lookup = new AccountLookupReply(BoincErrorCodes.InProgress, "", "");
        var fake = new FakeGuiRpcClient
        {
            OnPollAccountLookup = () => Task.FromResult(lookup),
            OnPollProjectAttach = () => Task.FromResult(new ProjectAttachReply(0, ["ok"])),
        };

        await fake.RequestAccountLookupAsync("http://p/", "a@b.c", "secret-pw");
        Assert.Equal(lookup, await fake.PollAccountLookupAsync());
        await fake.RequestProjectAttachAsync("http://p/", "key123", "Proj", "a@b.c");
        Assert.Equal(["ok"], (await fake.PollProjectAttachAsync()).Messages);

        Assert.Equal(
            ["lookup_account:http://p/:a@b.c", "lookup_account_poll", "project_attach:http://p/:Proj", "project_attach_poll"],
            fake.Calls);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("secret-pw") || c.Contains("key123"));
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
