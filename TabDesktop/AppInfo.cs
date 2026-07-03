using System.Globalization;
using System.Reflection;

namespace TabDesktop;

// Version and build time baked into the assembly by the csproj (Version mirrors package.json; BuildTimeUtc is stamped as assembly metadata).
public static class AppInfo
{
    public static string Version { get; } = ComputeVersion();
    public static DateTime? BuiltAtUtc { get; } = ComputeBuiltAt();

    private static string ComputeVersion()
    {
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        // The SDK appends "+<commit>" to the informational version when repository info is available.
        int plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    private static DateTime? ComputeBuiltAt()
    {
        string? raw = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTimeUtc")?.Value;
        return DateTime.TryParse(raw, null, DateTimeStyles.RoundtripKind, out DateTime builtUtc) ? builtUtc : null;
    }
}
