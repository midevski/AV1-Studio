using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Tests;

public class OutputPlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "abav1gui-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _src;

    public OutputPlannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Movies", "Sub (1)"));
        _src = Path.Combine(_dir, "Movies", "Sub (1)", "L'été [2020].mkv");
        File.WriteAllText(_src, "x");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private QueueItem Item() => new() { SourcePath = _src, SourceRoot = Path.Combine(_dir, "Movies") };

    [Fact]
    public void Default_is_suffix_next_to_source()
    {
        var p = OutputPlanner.Plan(new AppSettings(), Item(), 30);
        Assert.Equal(Path.Combine(_dir, "Movies", "Sub (1)", "L'été [2020]_AV1.mkv"), p.FinalPath);
        Assert.Equal(Path.Combine(_dir, "Movies", "Sub (1)", "L'été [2020]_AV1.av1studio.partial.mkv"), p.PartialPath);
        Assert.False(p.ReplacesSource);
        Assert.Null(p.SkipReason);
    }

    [Fact]
    public void Destination_mirrors_subfolders()
    {
        // With a destination folder the original filename is kept (Auto naming) and the tree is mirrored.
        var s = new AppSettings { DestinationFolder = Path.Combine(_dir, "Out") };
        var p = OutputPlanner.Plan(s, Item(), 30);
        Assert.Equal(Path.Combine(_dir, "Out", "Sub (1)", "L'été [2020].mkv"), p.FinalPath);
    }

    [Fact]
    public void Same_name_in_source_folder_requires_delete_source()
    {
        var s = new AppSettings { Naming = NamingMode.SameName };
        var p = OutputPlanner.Plan(s, Item(), 30);
        Assert.True(p.ReplacesSource);
        Assert.NotNull(p.SkipReason);

        s.DeleteSourceAfterSuccess = true;
        p = OutputPlanner.Plan(s, Item(), 30);
        Assert.True(p.ReplacesSource);
        Assert.Null(p.SkipReason);
        Assert.NotEqual(_src, p.PartialPath);
    }

    [Fact]
    public void Existing_output_is_never_overwritten_by_default()
    {
        File.WriteAllText(Path.Combine(_dir, "Movies", "Sub (1)", "L'été [2020]_AV1.mkv"), "old");
        // Default: the existing file is verified and reused, never overwritten.
        var def = OutputPlanner.Plan(new AppSettings(), Item(), 30);
        Assert.True(def.ExistingOutput);
        Assert.False(def.OverwriteExisting);
        Assert.NotNull(OutputPlanner.Plan(new AppSettings { Collision = CollisionPolicy.Skip }, Item(), 30).SkipReason);
        Assert.NotNull(OutputPlanner.Plan(new AppSettings { Collision = CollisionPolicy.Ask }, Item(), 30).SkipReason);

        var p = OutputPlanner.Plan(new AppSettings { Collision = CollisionPolicy.AppendNumber }, Item(), 30);
        Assert.EndsWith("L'été [2020]_AV1 (2).mkv", p.FinalPath);
        Assert.False(p.OverwriteExisting);

        Assert.True(OutputPlanner.Plan(new AppSettings { Collision = CollisionPolicy.Overwrite }, Item(), 30).OverwriteExisting);
    }

    [Fact]
    public void Template_tokens_and_invalid_chars()
    {
        var s = new AppSettings { Naming = NamingMode.Template, NameTemplate = "{name} crf{crf} p{preset} <x>", Preset = 5 };
        var p = OutputPlanner.Plan(s, Item(), 31.25);
        Assert.EndsWith("L'été [2020] crf31.25 p5 _x_.mkv", p.FinalPath);
    }

    [Fact]
    public void Temporary_files_are_recognised()
    {
        Assert.True(OutputPlanner.IsOurTemporaryFile(@"C:\a\Movie_AV1.av1studio.partial.mkv"));
        Assert.True(OutputPlanner.IsOurTemporaryFile(@"C:\a\.tmp.ab-av1-encoding.Movie_AV1.av1studio.partial.mkv"));
        Assert.False(OutputPlanner.IsOurTemporaryFile(@"C:\a\Movie.mkv"));
        Assert.False(OutputPlanner.IsOurTemporaryFile(@"C:\a\notes.partial.txt")); // a user's own file is never ours
    }

    [Fact]
    public void Container_same_as_source()
    {
        var s = new AppSettings { Container = ContainerFormat.SameAsSource };
        Assert.Equal("mp4", OutputPlanner.ContainerExtension(s, "a.MP4"));
        Assert.Equal("mkv", OutputPlanner.ContainerExtension(s, "a.avi"));
        Assert.Equal("webm", OutputPlanner.ContainerExtension(s, "a.webm"));
    }
}
