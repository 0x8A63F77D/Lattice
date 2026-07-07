namespace Lattice.Core;

/// <summary>
/// One monitored BOINC host. <paramref name="Id"/> is the stable identity and
/// survives renames and re-addressing. The password is stored in plaintext by
/// design — it mirrors BOINC's own plaintext gui_rpc_auth.cfg; never log it.
/// An empty password means "connect without authenticating" (localhost-only reads).
/// </summary>
public sealed record HostConfig(Guid Id, string Name, string Address, int Port, string Password)
{
    /// <summary>Display name: <see cref="Name"/> when set, else the address.</summary>
    public string DisplayName => Name.Length > 0 ? Name : Address;
}
