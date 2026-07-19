namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// The BOINC error codes (lib/error_numbers.h) this library's callers branch on.
/// Branching is structural — on the numeric error_num field only, never on
/// &lt;error&gt; message text, whose wording changes between daemon versions.
/// </summary>
public static class BoincErrorCodes
{
    /// <summary>
    /// ERR_IN_PROGRESS (lib/error_numbers.h): the daemon's HTTP request to the
    /// project server is still outstanding — keep polling.
    /// </summary>
    public const int InProgress = -204;

    /// <summary>
    /// ERR_RETRY (lib/error_numbers.h): the daemon is busy with another GUI HTTP
    /// operation — keep polling (official GUIs treat this the same as in-progress).
    /// </summary>
    public const int Retry = -199;
}
