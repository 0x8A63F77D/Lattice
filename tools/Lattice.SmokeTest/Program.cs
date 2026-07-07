using Lattice.Boinc.GuiRpc;
using Lattice.Core;

string host = "localhost";
int port = 31416;
string? password = null;
bool coreMode = false;

foreach (string arg in args)
{
    if (arg == "--core")
        coreMode = true;
    else if (arg.StartsWith("--password=", StringComparison.Ordinal))
        password = arg["--password=".Length..];
    else if (int.TryParse(arg, out int p))
        port = p;
    else
        host = arg;
}

password ??= ReadPasswordFile();
if (password is null)
{
    Console.Error.WriteLine("No password given and no gui_rpc_auth.cfg found. Use --password=<pw>.");
    return 1;
}

if (coreMode)
    return await RunCoreModeAsync(host, port, password);

try
{
    await using var client = new BoincGuiRpcClient();

    Console.WriteLine($"Connecting to {host}:{port} ...");
    await client.ConnectAsync(host, port);

    Console.WriteLine("Authorizing ...");
    if (!await client.AuthorizeAsync(password))
    {
        Console.Error.WriteLine("FAILED: daemon rejected the password.");
        return 1;
    }

    VersionInfo version = await client.ExchangeVersionsAsync();
    Console.WriteLine($"Daemon version: {version}");

    CcStatus status = await client.GetCcStatusAsync();
    Console.WriteLine($"Modes: tasks={status.TaskMode} gpu={status.GpuMode} network={status.NetworkMode}");
    Console.WriteLine($"Suspend reasons: tasks={status.TaskSuspendReason} network={status.NetworkSuspendReason}");

    CcState state = await client.GetStateAsync();
    Console.WriteLine($"Host: {state.HostInfo?.DomainName} ({state.HostInfo?.PModel}, {state.HostInfo?.NCpus} CPUs)");
    Console.WriteLine($"Projects ({state.Projects.Count}):");
    foreach (Project project in state.Projects)
        Console.WriteLine($"  {project.ProjectName,-30} user={project.UserTotalCredit:F0} host={project.HostTotalCredit:F0}");

    IReadOnlyList<Result> results = await client.GetResultsAsync();
    Console.WriteLine($"Tasks ({results.Count}):");
    foreach (Result result in results)
    {
        string progress = result.ActiveTask is { } at ? $"{at.FractionDone:P1}" : result.State.ToString();
        Console.WriteLine($"  {result.Name,-60} {progress}");
    }

    IReadOnlyList<Message> messages = await client.GetMessagesAsync();
    Console.WriteLine($"Messages: {messages.Count} total; last 3:");
    foreach (Message message in messages.TakeLast(3))
        Console.WriteLine($"  [{message.Timestamp:HH:mm:ss}] {message.Project}: {message.Body}");

    IReadOnlyList<FileTransfer> transfers = await client.GetFileTransfersAsync();
    Console.WriteLine($"Transfers ({transfers.Count}):");
    foreach (FileTransfer t in transfers)
    {
        string direction = t.IsUpload ? "up" : "down";
        string xferState = t.XferActive
            ? $"active {t.XferSpeed / 1048576:F2} MB/s"
            : t.NextRequestTime is { } next && next > DateTimeOffset.UtcNow
                ? $"retry at {next:HH:mm:ss} (attempt {t.NumRetries})"
                : "queued";
        Console.WriteLine($"  {t.Name,-50} {direction,-4} {t.BytesXferred / 1048576.0:F1}/{t.Nbytes / 1048576.0:F1} MB  {xferState}");
    }

    Console.WriteLine("SMOKE TEST PASSED");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
    if (ex is BoincProtocolException protocolEx)
        Console.Error.WriteLine($"Raw payload:\n{protocolEx.RawPayload}");
    return 1;
}

static async Task<int> RunCoreModeAsync(string host, int port, string password)
{
    var config = new HostConfig(Guid.NewGuid(), "local", host, port, password);
    var registry = new HostRegistry(
        new LatticeConfig(5, [config]),
        Path.Combine(Path.GetTempPath(), $"lattice-smoke-{Guid.NewGuid():N}.json"));
    await using var manager = new HostMonitorManager(registry, () => new BoincGuiRpcClient(), TimeProvider.System);

    var firstSnapshot = new TaskCompletionSource<HostSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    int messageCount = 0;
    manager.StatusChanged += (_, s) =>
        Console.WriteLine($"[status] {s.State}" + (s.LastError is { } err ? $" ({err})" : ""));
    manager.MessagesAdded += (_, m) => Interlocked.Add(ref messageCount, m.Messages.Count);
    manager.SnapshotUpdated += (_, snap) => firstSnapshot.TrySetResult(snap);

    Console.WriteLine($"Core mode: monitoring {host}:{port} ...");
    manager.Start();

    Task done = await Task.WhenAny(firstSnapshot.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    if (done != firstSnapshot.Task)
    {
        Console.Error.WriteLine("FAILED: no snapshot within 30 seconds.");
        return 1;
    }

    HostSnapshot snapshot = await firstSnapshot.Task;
    Console.WriteLine($"Snapshot @ {snapshot.Timestamp:HH:mm:ss}: " +
        $"{snapshot.Tasks.Count} tasks, {snapshot.Transfers.Count} transfers, " +
        $"{snapshot.Projects.Count} projects, {messageCount} messages buffered");
    foreach (TaskSnapshot t in snapshot.Tasks.Take(5))
        Console.WriteLine($"  {t.Result.Name,-60} app={t.ApplicationName} project={t.ProjectName} " +
            $"elapsed={t.ElapsedSeconds:F0}s atRisk={t.IsDeadlineAtRisk}");

    Console.WriteLine("CORE SMOKE TEST PASSED");
    return 0;
}

static string? ReadPasswordFile()
{
    List<string> candidates = [];
    if (Environment.GetEnvironmentVariable("BOINC_DATA_DIR") is { Length: > 0 } dataDir)
        candidates.Add(Path.Combine(dataDir, "gui_rpc_auth.cfg"));
    candidates.AddRange(OperatingSystem.IsWindows()
        ? [@"C:\ProgramData\BOINC\gui_rpc_auth.cfg"]
        : OperatingSystem.IsMacOS()
            ? ["/Library/Application Support/BOINC Data/gui_rpc_auth.cfg"]
            : ["/var/lib/boinc-client/gui_rpc_auth.cfg", "/var/lib/boinc/gui_rpc_auth.cfg"]);

    foreach (string path in candidates)
        if (File.Exists(path))
            return File.ReadAllText(path).Trim();
    return null;
}
