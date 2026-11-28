namespace MCHarbor.Windows.Services;

// macOS points map to Windows DIPs; physical pixels use the monitor's DPI scale.
internal static class MacLayout
{
    internal const double SidebarWidth = 210;
    internal const double MinimumWindowWidth = 1040;
    internal const double MinimumWindowHeight = 700;
    internal const double TopBarHeight = 54;
    internal const double PageInset = 28;
    internal const double SettingsInset = 24;
    internal const double AccountListWidth = 240;
    internal const double FriendsListWidth = 225;
    internal const double SettingsListWidth = 126;
    internal const double WorkspaceMaxWidth = 1120;
    internal const double WorkspaceTopInset = 52;

    internal static (int Columns, double ItemWidth) Cards(double available, double minimum, double maximum, double gap)
    {
        available = Math.Max(1, available);
        var columns = Math.Max(1, (int)Math.Floor((available + gap) / (minimum + gap)));
        return (columns, Math.Min(maximum, Math.Max(1, (available - gap * (columns - 1)) / columns)));
    }
}
