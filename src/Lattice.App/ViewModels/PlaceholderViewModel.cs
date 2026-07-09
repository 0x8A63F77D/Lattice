namespace Lattice.App.ViewModels;

/// <summary>Stage-2 view stand-in: proves navigation and scope propagation.</summary>
public sealed record PlaceholderViewModel(string Title)
{
    public string Body => $"{Title} arrives in M2c-2.";
}
