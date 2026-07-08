using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.Tests;

/// <summary>Builders for GuiRpc model records, defaulting every field tests don't care about.</summary>
public static class TestData
{
    public static Result MakeResult(
        string name = "task_1",
        string wuName = "wu_1",
        string projectUrl = "https://example.org/",
        DateTimeOffset? deadline = null,
        bool readyToReport = false,
        double finalElapsed = 0,
        double estRemaining = 0,
        ActiveTask? activeTask = null)
        => new(name, wuName, projectUrl, ResultState.FilesDownloaded, deadline,
               readyToReport, false, 0, finalElapsed, estRemaining, 0, "", 0, activeTask);

    public static Project MakeProject(
        string url = "https://example.org/", string name = "Example", double share = 100)
        => new(url, name, 0, 0, 0, 0, share, false, false);

    public static FileTransfer MakeTransfer(
        string name = "file_1",
        string projectUrl = "https://example.org/",
        string projectName = "",
        bool xferActive = false,
        DateTimeOffset? nextRequest = null)
        => new(name, projectUrl, projectName, 1000, 0, false, 0, null, nextRequest,
               0, 0, 0, 0, 0, true, xferActive);

    public static HostConfig MakeHostConfig(
        Guid? id = null,
        string name = "test",
        string address = "localhost",
        int port = 31416,
        string password = "pw")
        => new(id ?? Guid.NewGuid(), name, address, port, password);

    public static Message MakeMessage(int seqno, string body = "hello")
        => new("Example", MessagePriority.Info, seqno, DateTimeOffset.UnixEpoch, body);

    public static CcState MakeState(
        IReadOnlyList<Project>? projects = null,
        IReadOnlyList<App>? apps = null,
        IReadOnlyList<Workunit>? workunits = null)
        => new(new VersionInfo(8, 2, 0), null, projects ?? [], apps ?? [], [], workunits ?? [], []);
}
