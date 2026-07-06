using System.Xml.Linq;
using Xunit;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class FileTransferTests
{
    private static List<FileTransfer> Load()
    {
        var reply = XElement.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "get_file_transfers.xml")));
        return reply.Element("file_transfers")!.Elements("file_transfer").Select(FileTransfer.Parse).ToList();
    }

    [Fact]
    public void Parses_active_upload_with_nested_blocks()
    {
        FileTransfer t = Load()[0];
        Assert.Equal("h1_0437.60_result_upload_0", t.Name);
        Assert.Equal("https://einsteinathome.org/", t.ProjectUrl);
        Assert.Equal("Einstein@Home", t.ProjectName);
        Assert.Equal(54198000.0, t.Nbytes);
        Assert.Equal(1, t.Status);
        Assert.True(t.IsUpload);
        Assert.Equal(0, t.NumRetries);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1751600000), t.FirstRequestTime);
        Assert.Null(t.NextRequestTime); // 0.0 => null, matches ParseHelpers.GetTimestamp semantics
        Assert.Equal(12.5, t.TimeSoFar);
        Assert.Equal(35862528.0, t.BytesXferred); // live bytes_xferred wins over last_bytes_xferred
        Assert.Equal(1048576.0, t.FileOffset);
        Assert.Equal(2867200.0, t.XferSpeed);
        Assert.True(t.PersXferActive);
        Assert.True(t.XferActive);
    }

    [Fact]
    public void Parses_retrying_download()
    {
        FileTransfer t = Load()[1];
        Assert.False(t.IsUpload);
        Assert.Equal(3, t.NumRetries);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1999999999), t.NextRequestTime);
        Assert.Equal(524288.0, t.BytesXferred); // falls back to last_bytes_xferred
        Assert.Equal(161.0, t.ProjectBackoff);
        Assert.True(t.PersXferActive);
        Assert.False(t.XferActive);
    }

    [Fact]
    public void Parses_queued_entry_with_legacy_direction_tag()
    {
        FileTransfer t = Load()[2];
        Assert.True(t.IsUpload); // <generated_locally/> presence fallback
        Assert.Equal(0, t.NumRetries);
        Assert.Equal(0.0, t.BytesXferred);
        Assert.False(t.PersXferActive);
        Assert.False(t.XferActive);
    }
}
