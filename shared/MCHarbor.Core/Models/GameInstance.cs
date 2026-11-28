namespace MCHarbor.Core.Models;

public sealed record GameInstance(
    string Id,
    string Version,
    string Loader,
    string Directory,
    string RunDirectory,
    string? IconPath,
    DateTimeOffset Modified)
{
    public bool Favorite { get; init; }
    public string? Nickname { get; init; }
    public string FriendlyName => string.IsNullOrWhiteSpace(Nickname) ? Id : Nickname;
    public string FavoriteSymbol => Favorite ? "★" : "☆";
    public string DisplayName => Favorite ? $"★ {FriendlyName}" : FriendlyName;

    public string Artwork => new Uri(IconPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Loaders",
        (Loader == "vanilla" ? "grass" : Loader) + ".png")).AbsoluteUri;
}
