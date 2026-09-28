using System.IO;

namespace AV1Studio.Services;

/// <summary>
/// ab-av1's own sample-encode cache. ab-av1 stores it in the operating system's per-user cache folder, which on
/// Windows is %LOCALAPPDATA%\ab-av1 (resolved for the current user, never hard-coded). With the cache enabled,
/// a new CRF search of an unchanged file reuses the stored sample results instead of encoding again.
/// </summary>
public static class AbAv1Cache
{
    public static string Folder => FolderOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ab-av1");

    /// <summary>Tests only: use another folder than the real per-user cache.</summary>
    internal static string? FolderOverride { get; set; }

    public static long SizeBytes()
    {
        try
        {
            return Directory.Exists(Folder)
                ? new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>Deletes the cache folder. Fails (with a readable reason) while ab-av1 is using it.</summary>
    public static bool Clear(out long freedBytes, out string? error)
    {
        freedBytes = SizeBytes();
        error = null;
        if (!Directory.Exists(Folder)) return true;
        try
        {
            Directory.Delete(Folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            freedBytes -= SizeBytes();
            error = "ab-av1's cache is in use (an encode or CRF search may be running in another program). Close it and try again.";
            Log.Warn($"Could not delete the ab-av1 cache: {ex.Message}");
            return false;
        }
    }
}
