using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace AV1Studio.Services;

/// <summary>
/// Optional, user-initiated download of the required tools from their official distribution
/// points into the "tools" folder of the data folder. Nothing is bundled with the application:
/// * ab-av1 (MIT) — official GitHub release asset "ab-av1.exe"
/// * FFmpeg (GPL build incl. libsvtav1 + libvmaf) — BtbN/FFmpeg-Builds GitHub releases
/// </summary>
public static class ToolDownloader
{
    public const string AbAv1ReleaseApi = "https://api.github.com/repos/alexheretic/ab-av1/releases/latest";
    public const string FfmpegZipUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AV1Studio/1.0");
        return c;
    }

    public static async Task<string> DownloadAbAv1Async(IProgress<string> progress, CancellationToken ct)
    {
        using var http = CreateClient();
        progress.Report("Querying latest ab-av1 release…");
        using var doc = JsonDocument.Parse(await http.GetStringAsync(AbAv1ReleaseApi, ct));
        var tag = doc.RootElement.GetProperty("tag_name").GetString();
        string? url = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            if (string.Equals(a.GetProperty("name").GetString(), "ab-av1.exe", StringComparison.OrdinalIgnoreCase))
                url = a.GetProperty("browser_download_url").GetString();
        if (url is null) throw new InvalidOperationException($"Release {tag} has no ab-av1.exe asset.");

        Directory.CreateDirectory(AppPaths.Tools);
        var dest = Path.Combine(AppPaths.Tools, "ab-av1.exe");
        await DownloadFileAsync(http, url, dest, $"ab-av1 {tag}", progress, ct);
        return dest;
    }

    public static async Task<string> DownloadFfmpegAsync(IProgress<string> progress, CancellationToken ct)
    {
        using var http = CreateClient();
        Directory.CreateDirectory(AppPaths.Tools);
        var zip = Path.Combine(AppPaths.Temp, "ffmpeg-download.zip");
        await DownloadFileAsync(http, FfmpegZipUrl, zip, "FFmpeg (BtbN win64 gpl)", progress, ct);

        progress.Report("Extracting ffmpeg.exe / ffprobe.exe…");
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{name} not found in archive");
                var tmp = Path.Combine(AppPaths.Tools, name + ".download");
                entry.ExtractToFile(tmp, overwrite: true);
                File.Move(tmp, Path.Combine(AppPaths.Tools, name), overwrite: true);
            }
        }, ct);
        TryDelete(zip);
        return Path.Combine(AppPaths.Tools, "ffmpeg.exe");
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string dest, string label,
        IProgress<string> progress, CancellationToken ct)
    {
        var tmp = dest + ".download";
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buf = new byte[1 << 16];
            long done = 0;
            var last = DateTime.MinValue;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 250)
                {
                    last = DateTime.UtcNow;
                    progress.Report(total is long t && t > 0
                        ? $"Downloading {label}: {100.0 * done / t:0}% ({Util.Fmt.Bytes(done)} / {Util.Fmt.Bytes(t)})"
                        : $"Downloading {label}: {Util.Fmt.Bytes(done)}");
                }
            }
        }
        File.Move(tmp, dest, overwrite: true);
        progress.Report($"Downloaded {label}.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
