using System.Reflection;

namespace DbPilot;

internal static class AppInfo
{
    private static readonly string Channel =
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildChannel")?.Value ?? "Production";

    public static bool IsDevelopment { get; } =
        string.Equals(Channel, "Development", StringComparison.OrdinalIgnoreCase);
    public static string DisplayName => IsDevelopment ? "DbPilot Dev" : "DbPilot";
    public static string DataFolderName => IsDevelopment ? "DbPilot Dev" : "DbPilot";
    public static string Version => typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
