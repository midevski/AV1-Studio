# Building AV1 Studio

## Prerequisites (build machine only)

* Windows 10/11 x64
* [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
* Optional: [Inno Setup 6](https://jrsoftware.org/isinfo.php) to build the installer

End users need nothing: the published executable is self-contained.

## Build & run (debug)

```powershell
dotnet build AV1Studio.sln
dotnet run --project src\AV1Studio
```

## Tests

```powershell
dotnet test
```

The unit tests cover argument building (ab-av1 and FFmpeg), stream planning, output naming and
collisions, folder mirroring, CPU resource planning, queue-job integrity, parsers (ab-av1 JSON, FFmpeg
progress, FFprobe) and Windows quoting.

The **end-to-end tests** run the real pipeline (CRF search → encode → verify → delete) on generated
videos in awkward paths (spaces, accents, brackets, apostrophes), and replicate a complete folder tree
(nested folders, empty folders, non-video files, resume without re-encoding). They also check that a
failed verification or a cancelled encode never deletes the source and leaves no partial files.
Point them at a folder containing `ab-av1.exe`, `ffmpeg.exe` and `ffprobe.exe`:

```powershell
$env:AV1STUDIO_TEST_TOOLS = "<folder with ab-av1.exe, ffmpeg.exe and ffprobe.exe>"
dotnet test
```

Without that variable the end-to-end tests are no-ops.

To also test the in-app downloader, which fetches ~150 MB into a temporary folder and checks the tools
are detected and usable:

```powershell
$env:AV1STUDIO_TEST_DOWNLOAD = "1"
dotnet test --filter "FullyQualifiedName~Downloader"
```

## Publish the Windows executable

```powershell
.\build.ps1            # runs tests, then publishes
.\build.ps1 -SkipTests # publish only
```

or directly:

```powershell
dotnet publish src\AV1Studio\AV1Studio.csproj -c Release -o dist
```

The output is `dist\AV1Studio.exe`: a single, self-contained, compressed win-x64 executable that
includes the .NET runtime and the application icon. The version, product name and publisher come
from `Directory.Build.props` (the single place to change the version).

## Installer

```powershell
.\build.ps1
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\AV1Studio.iss
```

The installer (`installer\Output\AV1Studio-Setup-<version>.exe`) installs the exe, creates Start menu
(and optionally desktop) shortcuts with the application icon and AppUserModelID, and registers an
uninstaller. User data in `%LOCALAPPDATA%\AV1 Studio` is kept on uninstall.

### Portable mode

Create an empty `portable.txt` next to `AV1Studio.exe`. Settings, queue, cache, logs and downloaded
tools are then stored in `.\data\` beside the exe instead of `%LOCALAPPDATA%\AV1 Studio`.
Put `ab-av1.exe`, `ffmpeg.exe` and `ffprobe.exe` next to the exe (or in `.\tools\`) for a fully
portable setup. The `AV1STUDIO_HOME` environment variable overrides the data folder too.

Data from pre-release builds (`%LOCALAPPDATA%\AbAv1Studio`) is moved to the new folder automatically
on first start.

## Icon

`src/AV1Studio/Assets/av1studio.ico` contains 16, 20, 24, 32, 40, 48, 64, 128 and 256 px images;
`docs/images` has PNG renders (64/256/512 px) and an SVG version of the logo.

## Technology choices

* **C# / WPF on .NET 9**: native Windows UI, Fluent dark theme, excellent process APIs, and
  single-file self-contained publishing.
* **No third-party NuGet packages** in the app: fewer supply-chain and licensing concerns.
* **Windows Job Objects** for the encoder process tree: priority and processor affinity, CPU/RAM
  accounting, and guaranteed termination if the GUI exits or crashes.
* **ProcessStartInfo.ArgumentList**: each argument is escaped individually; no shell is ever used.
