namespace Lattice.App.ViewModels;

/// <summary>A rail view entry: label + icon resource keys + the page VM it opens.</summary>
public sealed record NavItemViewModel(string Title, string IconKey, string IconFilledKey, object Page);
