using Lattice.Boinc.GuiRpc;

string host = "localhost";
int port = 31416;
string? password = null;

foreach (string arg in args)
{
    if (arg.StartsWith("--password=", StringComparison.Ordinal))
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

static string? ReadPasswordFile()
{
    string[] candidates = OperatingSystem.IsWindows()
        ? [@"C:\ProgramData\BOINC\gui_rpc_auth.cfg"]
        : OperatingSystem.IsMacOS()
            ? ["/Library/Application Support/BOINC Data/gui_rpc_auth.cfg"]
            : ["/var/lib/boinc-client/gui_rpc_auth.cfg", "/var/lib/boinc/gui_rpc_auth.cfg"];

    foreach (string path in candidates)
        if (File.Exists(path))
            return File.ReadAllText(path).Trim();
    return null;
}
