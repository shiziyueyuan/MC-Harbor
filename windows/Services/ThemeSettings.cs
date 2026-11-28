using System.Globalization;
using System.Text.Json;

namespace MCHarbor.Windows.Services;

internal sealed class HarborThemeSettings
{
    public Dictionary<string, string> Roles { get; set; } = new() { ["buttonFill"] = "theme", ["buttonText"] = "system", ["controls"] = "theme", ["effects"] = "theme" };
    public Dictionary<string, string> Custom { get; set; } = [];
    public string InverseMode { get; set; } = "contrast";
    public string InverseHex { get; set; } = "#10271E";
    public double InverseBrightness { get; set; }
    public double InverseSaturation { get; set; } = 1;
    public bool ProtectContrast { get; set; } = true;
    public double SurfaceStrength { get; set; } = 0.14;
    public double BorderStrength { get; set; } = 0.35;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    internal Rgb Resolve(string area, Rgb fallback, Rgb theme)
    {
        var role = Roles.GetValueOrDefault(area, "system");
        return role switch
        {
            "theme" => theme,
            "custom" => Rgb.TryParse(Custom.GetValueOrDefault(area), out var custom) ? custom : fallback,
            "inverse" => Inverse(theme),
            _ => fallback
        };
    }

    internal Rgb Inverse(Rgb theme)
    {
        var basis = InverseMode switch
        {
            "rgb" => new Rgb((byte)(255 - theme.R), (byte)(255 - theme.G), (byte)(255 - theme.B)),
            "complementary" => theme.Complementary(),
            "custom" when Rgb.TryParse(InverseHex, out var custom) => custom,
            _ => Rgb.Readable(theme)
        };
        return basis.Adjust(InverseBrightness, InverseSaturation);
    }
}

internal readonly record struct Rgb(byte R, byte G, byte B)
{
    internal string Hex => $"#{R:X2}{G:X2}{B:X2}";
    internal static bool TryParse(string? value, out Rgb color)
    {
        color = default;
        if (value is null || value.Length != 7 || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number)) return false;
        color = new((byte)(number >> 16), (byte)(number >> 8), (byte)number);
        return true;
    }
    internal static Rgb Parse(string value) => TryParse(value, out var color) ? color : throw new FormatException("请输入 #RRGGBB 格式的颜色。");
    internal static Rgb Mix(Rgb first, Rgb second, double weight)
    {
        weight = Math.Clamp(weight, 0, 1);
        return new((byte)Math.Round(first.R * (1 - weight) + second.R * weight),
            (byte)Math.Round(first.G * (1 - weight) + second.G * weight), (byte)Math.Round(first.B * (1 - weight) + second.B * weight));
    }
    private double Luminance
    {
        get
        {
            static double Linear(byte c) { var v = c / 255d; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);
        }
    }
    internal static double Contrast(Rgb first, Rgb second) => (Math.Max(first.Luminance, second.Luminance) + 0.05) / (Math.Min(first.Luminance, second.Luminance) + 0.05);
    internal static Rgb Readable(Rgb background) => Contrast(new(0, 0, 0), background) >= Contrast(new(255, 255, 255), background) ? new(0, 0, 0) : new(255, 255, 255);
    internal static Rgb Protect(Rgb ink, Rgb background, bool enabled) => enabled && Contrast(ink, background) < 4.5 ? Readable(background) : ink;
    internal Rgb Complementary()
    {
        var max = Math.Max(R, Math.Max(G, B)); var min = Math.Min(R, Math.Min(G, B));
        return new((byte)(max + min - R), (byte)(max + min - G), (byte)(max + min - B));
    }
    internal Rgb Adjust(double brightness, double saturation)
    {
        brightness = double.IsFinite(brightness) ? Math.Clamp(brightness, -1, 1) : 0;
        saturation = double.IsFinite(saturation) ? Math.Clamp(saturation, 0, 2) : 1;
        var gray = 0.2126 * R + 0.7152 * G + 0.0722 * B;
        byte Channel(byte channel) => (byte)Math.Round(Math.Clamp(gray + (channel - gray) * saturation + brightness * 255, 0, 255));
        return new(Channel(R), Channel(G), Channel(B));
    }
}
