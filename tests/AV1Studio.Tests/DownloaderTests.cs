using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Tests;

/// <summary>Downloads the real tools (~150 MB). Opt-in: set AV1STUDIO_TEST_DOWNLOAD=1.</summary>
[Collection("e2e")]
public class DownloaderTests
{
    [Fact]
    public async Task Downloads_working_abav1_ffmpeg_and_ffprobe()
    {
        if (Environment.GetEnvironmentVariable("AV1STUDIO_TEST_DOWNLOAD") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "abav1gui-dl-" + Guid.NewGuid().ToString("N")[..8]);
        var previous = AppPaths.Root;
        AppPaths.Root = root;
        try
        {
            AppPaths.EnsureCreated();
            var progress = new Progress<string>(_ => { });
            await AV1Studio.Services.ToolDownloader.DownloadAbAv1Async(progress, CancellationToken.None);
            await AV1Studio.Services.ToolDownloader.DownloadFfmpegAsync(progress, CancellationToken.None);

            // Empty paths = auto-detect, exactly what the Settings download buttons leave behind.
            var tools = await ToolLocator.DetectAsync(new AppSettings());
            Assert.StartsWith(root, tools.AbAv1Path);
            Assert.StartsWith(root, tools.FfmpegPath);
            Assert.StartsWith(root, tools.FfprobePath);
            Assert.True(tools.Ready, string.Join("; ", tools.Problems));
            Assert.Empty(Directory.GetFiles(AppPaths.Tools, "*.download"));
            Assert.Empty(Directory.GetFiles(AppPaths.Temp, "*.zip"));
        }
        finally
        {
            AppPaths.Root = previous;
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
