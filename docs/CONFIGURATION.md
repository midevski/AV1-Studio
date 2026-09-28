# Configuration

AB-AV1 settings live in **Settings › AB-AV1** (plus the quick controls in the main window's
settings strip in AB-AV1 mode). Manual AV1 settings live in the **Manual AV1 settings…** window
(Ctrl+M; the most common ones are also in the settings strip). The two never share options:
nothing from Manual AV1 is passed to ab-av1 and vice versa. Performance (CPU usage, concurrency),
Output & folders, Safety and Tools settings are shared infrastructure used by both modes.

**Queued jobs keep their settings.** When a file is added, the current settings are captured as that
job's configuration (identical configurations are stored once). Changing settings afterwards only
affects files added later; right-click › *Apply current settings to selected* updates queued files.
Run-level choices — source deletion, verification, disk-space rules, CPU usage and tool paths — always
use the current values.

## Profiles

| Profile | AB-AV1 | Manual AV1 |
|---|---|---|
| Balanced | VMAF 93 | medium quality/preset of the chosen encoder |
| High Quality | VMAF 95 | higher quality, slower preset |
| Small File | VMAF 90 | lower quality, smaller files |
| Fast Encode | VMAF 93, fast preset | fast preset |
| Archive | VMAF 97 | near-transparent quality, slow preset |

A profile fills in the values as a starting point; every value can be changed afterwards.

## CPU usage (Settings › Performance)

Separate profiles for the AB-AV1 CRF search and for the final encode (both modes, previews and
verification decodes). Limits are applied by Windows through the job object (priority class and
processor affinity) to every encoder process.

| CPU usage | Threads (logical processors) | Priority selected with the mode |
|---|---|---|
| **Auto (default)** | all but a few (none on ≤ 2 threads, 1 on ≤ 8, otherwise ~1/8 and at least 2) | Normal |
| Maximum | all | High |
| Balanced | about ¾ | Below normal |
| Low | about half | Low (idle) |
| Custom | a number of threads, or specific CPU cores such as `0-7,12` | unchanged |

The default priority is **High** (with Auto CPU usage). The process priority can be changed independently of the mode (Low, Below normal, Normal, Above normal,
High). The number of logical processors is detected on each PC.

Realtime priority is never used. On PCs with more than 64 logical processors the processor limit
cannot be enforced (Windows processor groups); the priority still applies. The resolved plan is shown in Settings and written to the log at startup.

## GPU encoder options (Manual AV1)

Hardware encoders expose their real options only (there is no "GPU %" control):

| Option | NVENC | Quick Sync | AMF |
|---|---|---|---|
| Quality | `-cq` (VBR, `-b:v 0`) | `-global_quality` (ICQ) | `-qp_i/-qp_p` (CQP) |
| Preset | `-preset p1…p7` | `-preset veryfast…veryslow` | `-quality speed…quality` |
| B-frames | `-bf N` | `-bf N` | — |
| Lookahead | `-rc-lookahead N` | `-extbrc 1 -look_ahead_depth N` | — |
| Multipass | `-multipass disabled/qres/fullres` | — | — |
| Adaptive quantization | `-spatial-aq 1`, `-temporal-aq 1` | — | — |
| Maximum bitrate | `-maxrate`, `-bufsize` | `-maxrate`, `-bufsize` | `-maxrate`, `-bufsize` |

For SVT-AV1 the maximum bitrate becomes `-svtav1-params mbr=…`; libaom-av1 uses `-maxrate`.

## Manual AV1 → FFmpeg arguments

The Manual engine runs FFmpeg directly (`ffmpeg -n -i <source> -map 0 -c copy -c:v:N <encoder> … -f matroska <name>.av1studio.partial.mkv`).
Every stream is copied unless a setting says otherwise; the main video stream is mapped explicitly.

| Setting | FFmpeg |
|---|---|
| SVT-AV1 CRF / preset | `-crf:v:N`, `-preset:v:N` (fractional CRF via `-svtav1-params crf=`) |
| libaom-av1 CRF / preset | `-crf:v:N Q -b:v:N 0 -cpu-used:v:N 0…8 -row-mt:v:N 1` |
| NVENC CQ / preset | `-rc:v:N vbr -cq:v:N Q -b:v:N 0 -preset:v:N p1…p7 -tune:v:N hq` |
| QSV ICQ / preset | `-global_quality:v:N Q -preset:v:N veryfast…veryslow` |
| AMF QP / quality | `-rc:v:N cqp -qp_i:v:N Q -qp_p:v:N Q -quality:v:N …` (experimental, untested) |
| Bit depth | `-pix_fmt:v:N yuv420p10le / yuv420p` (SVT) or `p010le / nv12` (hardware) |
| Resolution | `scale=-2:H` (downscale only) or `scale=W:H` (custom) |
| Frame rate | `fps=…` (empty = source) |
| Keyframes | `-g:v:N` (seconds converted with the source/target fps) |
| SVT-AV1 options | `-svtav1-params scd, tune, film-grain, film-grain-denoise, fast-decode, tile-columns, tile-rows, lp, …` |
| Filters | `bwdif` (deinterlace), `fieldmatch,…,decimate` (detelecine), `crop`, `hqdn3d` (denoise), `deblock`, `hue=s=0` (grayscale), custom |
| HDR (preserve) | source `-color_primaries/-color_trc/-colorspace/-color_range`; SVT-AV1 also gets `enable-hdr=1:mastering-display=…:content-light=…` |
| HDR → SDR | `zscale … tonemap=hable …` then BT.709 tags (asks for confirmation) |
| Audio | copy by default; transcode `-c:a <codec> [-b:a] [-ac 2]`; per-track selection via `-map -0:a:N` |
| Subtitles | copy / languages / forced only / remove; default flag via `-disposition:s:N` |
| Metadata | kept by default; `-map_chapters -1`, `-map_metadata -1` when turned off |
| Container | MKV (`-f matroska`, `-dn`) or MP4 (`-f mp4 -movflags +faststart`, `mov_text` subtitles) |

The live command is shown in the Manual AV1 settings window (**Copy command**) and, for the
selected queued file, in its details pane (**Commands**).

## Preview

A preview encodes a segment (default 30 s, starting ~⅓ into the file). *Preview selected…* in the
main window uses the mode and settings that file was queued with; the preview in the Manual AV1
settings window uses the current Manual settings. It uses a fast input seek plus a precise output seek so stream-copied audio stays
in sync, and leaves out subtitles/chapters/attachments (they would distort the segment's duration).
Results: side-by-side frames (HDR frames are tone-mapped for display only), size, bitrate, speed,
quality, preset, and a full-size extrapolation that also feeds the disk-space estimate for that file.

Settings are stored in `%LOCALAPPDATA%\AV1 Studio\settings.json` (or `.\data\` in portable mode).
Empty fields mean **"let ab-av1 decide"**: the app never passes a flag you did not set, so ab-av1's
own defaults apply.

The argument mapping below was written against the ab-av1 **v0.11.7** source
(`src/command/args/*.rs`, `crf_search.rs`, `encode.rs`) and its
[stdout-format-json.md](https://github.com/alexheretic/ab-av1/blob/main/stdout-format-json.md).

## How files are processed

For each file, one at a time by default:

1. **Probe**: `ffprobe -show_format -show_streams -show_chapters`.
2. **CRF search**: `ab-av1 crf-search -i <file> [encoder/video args] --min-vmaf <target> [search args] --temp-dir <temp> --stdout-format json`.
   Each `sample-encode-done` line is one attempt shown in the UI; `crf-search-done` is the result.
   A `crf-search-error` ("Failed to find a suitable crf") means no CRF meets the VMAF target within
   `--max-encoded-percent`. The file is then **Skipped** (AV1 wouldn't save enough space), not failed.
3. **Encode**: `ab-av1 encode -i <file> --crf <CRF> -o <Name>.av1studio.partial.mkv [same encoder/video args] [stream args] --verify --fail-fast --enc progress=<file>`.
   ab-av1 writes to `.tmp.ab-av1-encoding.<name>`, verifies, and renames to the `.partial` name.
   The same encoder/video args are used for search and encode, so the measured VMAF applies to
   the real output.
4. **Verify** (independent checks by the app, see below).
5. **Rename** `.av1studio.partial.mkv` → final name (never overwriting unless you allowed it).
6. **Delete source** (optional).

## Settings → ab-av1 flags

### Quality / CRF search
| Setting | Flag | Default |
|---|---|---|
| Target VMAF / quality preset | `--min-vmaf` | 95 |
| Max encoded size % | `--max-encoded-percent` | ab-av1 default (80) |
| CRF range | `--min-crf`, `--max-crf` | ab-av1 default (5–70 for SVT-AV1) |
| CRF increment | `--crf-increment` | ab-av1 default (0.25). The app passes `1` automatically when SVT-AV1 < 4.0 is detected |
| Thorough | `--thorough` | off |
| Samples (Auto, 1–10; main window or Settings › AB-AV1) | Auto: no `--samples` (ab-av1 decides from the duration, using `--sample-every` / `--min-samples` if set). A number: `--samples N` only | Auto |
| Sample duration | `--sample-duration` | ab-av1 default (20s) |
| ab-av1 sample cache | `--cache false` when disabled | on |

**Clear analysis cache** (Settings › Advanced) removes AV1 Studio's saved CRF results **and** ab-av1's own
sample-encode cache (`%LOCALAPPDATA%\ab-av1`, resolved for the current user), and makes queued AB-AV1 files that
already had a CRF run a new search. Finished files are not affected.
| Extra VMAF args | `--vmaf <arg>` (each) | none |
| VMAF scale | `--vmaf-scale` | ab-av1 default (auto) |

Profiles set the target VMAF (and, for Fast Encode, a faster preset): Archive 97, High Quality 95,
Balanced 93, Fast Encode 93, Small File 90.

### Encoder (SVT-AV1)
| Setting | Flag |
|---|---|
| Preset | `--preset N` (empty = ab-av1 default, currently 8) |
| Keyframe interval | `--keyint` (frames or duration, e.g. `10s`) |
| Scene-change detection | `--scd true/false` |
| Encoder threads | `--svt lp=N` |
| Custom SVT-AV1 parameters | one `--svt key=value` per pair. `crf`, `preset`, `keyint`, `scd`, `input-depth` are rejected because ab-av1 sets them |

With hardware encoding OFF (default) the encoder is **libsvtav1** (ab-av1's default) and
`-e/--encoder` is never passed.

### Hardware acceleration in AB-AV1 (GPU encoding)
Toggle with the **GPU encoding: ON/OFF** button in the main window's settings strip in AB-AV1 mode (Ctrl+H) or in Settings › AB-AV1.
(Manual AV1 chooses its encoder directly from the list of encoders that work on the PC.)

| Setting | Flag |
|---|---|
| GPU encoding ON | `-e av1_nvenc` or `-e av1_qsv` |
| GPU preset | `--preset p1…p7` (NVENC) or `veryfast…veryslow` (QSV); empty = encoder default |
| Pixel format | `--pix-format yuv420p10le` unless you chose one (ab-av1 sets none for GPU encoders; FFmpeg maps it to the GPU's `p010le`, so output stays 10-bit) |
| Not passed when ON | SVT-AV1 preset, `--svt …`, `lp`, `--scd`, the SVT < 4.0 `--crf-increment 1` workaround |

* ab-av1 maps its CRF to the encoder's quality option (NVENC `-cq`, QSV `-global_quality`), so the
  **CRF search still targets your VMAF**. GPU encoders are much faster but produce larger files than
  SVT-AV1 at the same VMAF.
* Availability is determined by a real 1-frame test encode at startup, not just by the GPU name.
  FFmpeg builds contain `av1_nvenc` even when the card (e.g. an RTX 30-series GPU) cannot encode AV1. The
  toggle is disabled when nothing works, and a run refuses to start if the selected GPU encoder is
  unavailable: it never silently falls back to the CPU.
* AMD `av1_amf` is not offered: ab-av1 passes `-crf` to it, which AMF does not support.
* The encoder is part of the analysis fingerprint, so switching CPU ↔ GPU triggers a new CRF search.
* Output verification is identical for both encoders.

### Video
All video options become one `--vfilter` chain (applied to both the VMAF reference and the encode):

| Setting | Filter |
|---|---|
| Crop `w:h:x:y` | `crop=w:h:x:y` |
| Resolution "Max 1080p" etc. | `scale=-2:1080:flags=lanczos`, only when the source is taller (never upscales) |
| Frame rate | `fps=N` |
| Custom filters | appended as-is |
| Pixel format | `--pix-format` (empty = ab-av1 default `yuv420p10le`, 10-bit) |

Defaults: same resolution, same frame rate, 10-bit. Nothing is resized or re-timed silently.

### Audio
| Mode | What happens |
|---|---|
| Copy (default) | ab-av1 default `-c:a copy`, all tracks kept. Tracks the container can't hold (MP4: TrueHD/DTS/…; WebM: anything but Opus/Vorbis) are re-encoded individually to Opus via `--enc c:a:N=libopus`; everything else stays untouched. |
| Transcode | `--acodec <codec>` (+ `--enc b:a=<bitrate>`, `--downmix-to-stereo`) |
| Remove | `--enc an` |

Track selection: *Keep audio languages* (e.g. `eng,fre,und`) or per file (right-click › Choose tracks).
Dropped tracks become `--enc map=-0:a:N`. If a language filter would remove every audio track,
all tracks are kept and a warning is logged.

### Subtitles & metadata
| Setting | What happens |
|---|---|
| Copy all (default) | ab-av1 default `-c:s copy` (language/title/forced/default preserved) |
| Keep languages | others dropped with `--enc map=-0:s:N` |
| Remove | `--enc sn` |
| MP4 / WebM output | text subs converted to `mov_text` / `webvtt`; image subs (PGS/VobSub) dropped with a warning |
| Keep chapters | off → `--enc map_chapters=-1` |
| Keep global metadata | off → `--enc map_metadata=-1` |
| Keep attachments | off (or non-MKV output) → `--enc map=-0:t?` |

### Output & naming (Settings › Output & folders)
* **Output container**: **MKV** (default) or **MP4**, for both modes (main window, Manual AV1 settings, or
  Settings › Output & folders). The video is AV1 either way; the choice decides the file format and extension,
  regardless of the source's container (`Movie.mp4` → `Movie.mkv` with MKV, `Movie.mkv` → `Movie.mp4` with MP4).
  Each queued file keeps the container it was added with.
* **Naming**: *Automatic* (default) keeps the original file name when writing to a destination folder
  and adds the suffix when writing next to the source (`Movie.mkv → Movie_AV1.mkv`); or always the
  suffix, same filename, or a template (`{name} {crf} {vmaf} {preset} {date}`).
* **Keep sub-folders**: `<source>\A\x.mkv` → `<destination>\A\x.mkv`.
* **Existing output**: *Reuse if valid* (default: an existing complete AV1 output is verified and kept,
  which makes interrupted jobs resumable), *Ask* (one decision before the run starts), *Skip*,
  *Add a number*, or *Replace* (asks for confirmation; never applies to the source).

### MKV and MP4 output

| | MKV | MP4 |
|---|---|---|
| FFmpeg muxer | `-f matroska` | `-f mp4 -movflags +faststart` (index at the start for quick playback) |
| AB-AV1 | `--enc f=matroska` | `--enc f=mp4 --enc movflags=+faststart` (ab-av1 has no container option; `--enc` passes FFmpeg output options) |
| Audio (copy) | every format | AAC, AC-3, E-AC-3, MP3, Opus, FLAC, ALAC, MP2 copied; others (TrueHD, DTS, PCM…) converted to Opus |
| Text subtitles | copied (MP4 `mov_text` converted to SRT) | converted to `mov_text` |
| Image subtitles (PGS, VobSub) | copied | cannot be stored — you are asked before encoding starts |
| Chapters, title, stream languages | kept | kept |
| Attachments (fonts) | kept | cannot be stored — you are asked before encoding starts |
| HDR colour tags, 10-bit | kept | kept |

MP4 output is offered only when a test proves the installed FFmpeg can write AV1 into MP4; otherwise encoding
with MP4 is refused with an explanation (never silently switched to MKV). After encoding, FFprobe checks the
real container (Matroska, or MP4 by its brand — not the file name) before anything is renamed or deleted.
Temporary files are named `<name>.av1studio.partial.mkv` / `.mp4` and are never treated as finished files.
An existing file of the other container (e.g. `Movie.mkv` when MP4 is selected) is not treated as an existing output.

### Folder jobs (complete replica)
With **Added folders: recreate the complete folder tree** (default on) and a destination folder:

* every sub-folder of the added folder is created in the destination, including empty ones;
* videos are encoded with their original names and relative paths;
* all other files (subtitles, images, NFO, documents…) — and videos that are not re-encoded (already
  AV1, too small, no CRF reaches the target) — are **copied** unchanged through a temporary file,
  verified (size; SHA-256 when *Verify copied files with a hash* is on) and renamed into place;
* copied files are never deleted from the source, even when source deletion is enabled;
* hidden/system files, reparse points (junctions/symlinks) and the destination folder itself are skipped;
* duplicate paths are detected after normalization (case, `.\`, `..\`, trailing separators);
* before starting, the free space needed for the folder is checked;
* the queue groups files by folder; each folder shows overall / videos / copies progress and ends as
  *Completed*, *Completed with errors*, *Cancelled* or *Completed — nothing to process*.
* **Same filename in the source folder** is allowed only with source deletion enabled. The source
  is then deleted after verification and the verified output takes its name. This is recorded
  before deletion so an interruption is completed on the next start.

### Safety & disk space
| Setting | Default | Notes |
|---|---|---|
| Full decode verification | on | ab-av1 `--verify` (full decode + duration within 2 s). With ab-av1 < 0.11.7 the app runs an equivalent `ffmpeg -xerror -f null` decode itself. |
| Stop at first FFmpeg error | on | ab-av1 `--fail-fast` (`-xerror`); catches corrupted sources. |
| Check track counts | on | audio/subtitle counts in the output must match what was planned |
| Duration tolerance | 2 s | the effective tolerance is the larger of this and 0.05% of the duration |
| Delete source | off | Permanent or Recycle Bin (Recycle Bin frees no space until emptied) |
| Keep at least | 2 GB free | |
| Safety factor | 1.25 × estimated size | unanalyzed files use the source size |
| When a file doesn't fit | Pause queue | or skip the file |
| Concurrent jobs | 1 | more jobs multiply CPU/RAM/disk load and temporary space |
| CPU usage | Auto, High priority | see [CPU usage](#cpu-usage-settings--performance) |

**Verification steps** (all must pass before rename/deletion):
1. Process exit code is 0
2. Output file exists
3. Output size > 0
4. FFprobe can read the output
5. A video stream is present, and it is AV1
6. Output duration ≈ source duration
7. Expected number of audio and subtitle tracks
8. Full decode without errors (ab-av1 `--verify`, or the app's own decode)
9. The source file is unchanged since the encode started (size + modification time)

Deleting the source additionally re-checks that the final file exists with the verified size.
If you untick *Delete source* while a queue is running, it takes effect immediately.

### Advanced FFmpeg
* **Output options** → `--enc name=value` (one per line)
* **Input options** → `--enc-input name=value`

Options ab-av1 manages (`c:v`, `c:a`, `crf`, `preset`, `pix_fmt`, `vf`, `svtav1-params`, …) are
rejected with an explanation.

## Data files

| File | Content |
|---|---|
| `settings.json` | all settings |
| `queue.json` | queue and per-file state (status, CRF, VMAF, output path, commands…), each job's settings snapshot, folder jobs |
| `analysis-cache.json` | CRF search results keyed by path + size + mtime, plus a quick content hash (SHA-256 of the size and the first/last 4 MiB) to recognise moved files |
| `logs\av1-studio_YYYY-MM-DD.log` | full log including raw ab-av1 / FFmpeg output |
| `temp\` | ab-av1 sample files during CRF search (deleted afterwards) |
| `tools\` | tools downloaded by the app |

All JSON files are written atomically (temp file + flush + replace, with a `.bak` copy).

A cached analysis is reused only when the file is unchanged **and** every search-relevant setting
(target VMAF, preset, filters, pixel format, keyint/scd, SVT params, search options) is identical.
Right-click › *Force new CRF search* discards it.

## ab-av1 version adaptation

At startup the app runs `ab-av1 --version`, `ab-av1 crf-search --help` and `ab-av1 encode --help`,
and only uses flags the installed version supports:

| Feature | Needed version | Fallback |
|---|---|---|
| `--stdout-format json` | 0.11.5 | parse the human result line |
| `--verify`, `--fail-fast` | 0.11.7 | the app's own decode check; no fail-fast |
| quarter-step CRF (0.25) | ab-av1 0.11 + SVT-AV1 4.0 | `--crf-increment 1` when SVT-AV1 < 4.0 |

The FFmpeg build is checked for `libsvtav1` and `libvmaf`, and the SVT-AV1 library version is read
from a 1-frame test encode.
