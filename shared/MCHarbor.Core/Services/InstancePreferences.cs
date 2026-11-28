using System.Security.Cryptography;
using System.Text;
using MCHarbor.Core.Models;

namespace MCHarbor.Core.Services;

public static class InstancePreferences
{
    public static string Prefix(GameInstance instance)
    {
        var directory = InstanceFiles.CanonicalDirectory(instance.Directory);
        if (OperatingSystem.IsWindows()) directory = directory.ToUpperInvariant();
        return "instance-path:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory))) + ":";
    }
    public static string Get(GameInstance instance, string key, string fallback = "") =>
        HarborHost.GetPreference(Prefix(instance) + key, HarborHost.GetPreference("instance:" + instance.Id + ":" + key, fallback));
    public static int Number(GameInstance instance, string key, int fallback) => int.TryParse(Get(instance, key), out var value) ? value : fallback;
    public static void Set(GameInstance instance, string key, string value) => HarborHost.SetPreference(Prefix(instance) + key, value);
}
