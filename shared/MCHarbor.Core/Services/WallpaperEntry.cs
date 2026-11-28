namespace MCHarbor.Core.Services;

public sealed record WallpaperEntry(string Name, string FileName)
{
    public bool Video => FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
}
