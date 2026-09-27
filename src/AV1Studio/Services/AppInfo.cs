using System.Reflection;

namespace AV1Studio.Services;

/// <summary>Application identity. The version comes from Directory.Build.props (single place to update).</summary>
public static class AppInfo
{
    public const string Name = "AV1 Studio";
    public const string Description = "A modern AV1 encoding application for Windows.";
    public const string AppUserModelId = "AV1Studio.AV1Studio";
    public const string RepositoryUrl = "https://github.com/"; // set to the public repository when published

    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info)) return info.Split('+')[0]; // drop source-revision suffix
            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    public static string NameAndVersion => $"{Name} {Version}";
}
