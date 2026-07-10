namespace Lattice.App.Infrastructure;

/// <summary>
/// Single funnel for HostRegistry mutations triggered from the UI. Every mutation
/// persists config.json synchronously and throws IOException (unwritable path,
/// full disk) or UnauthorizedAccessException (permissions) when that fails; an
/// unguarded call site turns those into unhandled dispatcher exceptions. All UI
/// mutation sites route through this guard and surface the returned text inline.
/// </summary>
public static class RegistryGuard
{
    /// <summary>Runs a registry mutation. Null on success, the failure detail otherwise.</summary>
    public static string? TryMutate(Action mutate)
    {
        try
        {
            mutate();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
