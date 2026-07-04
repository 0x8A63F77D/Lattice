using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class MessageTests
{
    [Fact]
    public void Parses_message_list_fixture()
    {
        var reply = XElement.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "get_messages.xml")));
        var messages = reply.Element("msgs")!.Elements("msg").Select(Message.Parse).ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal("", messages[0].Project);
        Assert.Equal(MessagePriority.Info, messages[0].Priority);
        Assert.Equal(1, messages[0].Seqno);
        Assert.Contains("Starting BOINC client", messages[0].Body);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1751600000), messages[0].Timestamp);

        Assert.Equal("Einstein@Home", messages[1].Project);
        Assert.Equal(MessagePriority.UserAlert, messages[1].Priority);
    }
}
