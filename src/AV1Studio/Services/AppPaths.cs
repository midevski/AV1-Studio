using System.IO;

namespace AV1Studio.Services;

public static class AppPaths
{
    public const string HomeVariable = "AV1STUDIO_HOME";
    private const string FolderName = "AV1 Studio";
    private const string LegacyFolderName = "AbAv1Studio"; // data folder of pre-release builds

    /// <summary>Data folder. Resolution order:
    /// 1. AV1STUDIO_HOME environment variable
    /// 2. portable mode: a "portable.txt" file next to the exe → "&lt;exe folder&gt;\data"
    /// 3. %LOCALAPPDATA%\AV1 Studio</summary>
    public static string Root { get; set; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable(HomeVariable);
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")))
            return Path.Combine(AppContext.BaseDirectory, "data");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
    }

    /// <summary>Called once at application startup: moves settings, queue, cache and tools from the
    /// pre-release data folder, so nothing is lost. Only for the default location.</summary>
    public static void MigrateLegacyFolder()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.Equals(Root, Path.Combine(local, FolderName), StringComparison.OrdinalIgnoreCase)) return;
            var legacy = Path.Combine(local, LegacyFolderName);
            if (Directory.Exists(legacy) && !Directory.Exists(Root)) Directory.Move(legacy, Root);
        }
        catch { /* the old folder is simply left in place; a fresh configuration is created */ }
    }

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string State => Path.Combine(Root, "queue.json");
    public static string AnalysisCache => Path.Combine(Root, "analysis-cache.json");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Tools => Path.Combine(Root, "tools");
    public static string Temp => Path.Combine(Root, "temp");
    public static string Progress => Path.Combine(Root, "progress");

    public static void EnsureCreated()
    {
        foreach (var d in new[] { Root, Logs, Tools, Temp, Progress })
            Directory.CreateDirectory(d);
    }
}
