namespace Lattice.App.ViewModels;

/// <summary>
/// The global host scope shared by every view (design rule: selecting a host
/// scopes ALL views; persists across view switches). Null = All hosts.
/// </summary>
public readonly record struct ScopeSelection(Guid? HostId)
{
    public static ScopeSelection AllHosts => new((Guid?)null);
    public bool IsAllHosts => HostId is null;
}
