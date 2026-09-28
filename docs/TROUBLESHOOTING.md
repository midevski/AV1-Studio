# Troubleshooting

First look at the **Log** tab, the selected file's **File log**, and its **Commands** section,
which shows the exact ab-av1 command line. Copy it and run it in a terminal to reproduce outside
the app. Full logs, including raw ab-av1/FFmpeg output, are in
`%LOCALAPPDATA%\AV1 Studio\logs` (Ctrl+L).

When asking for help, attach the diagnostic report (About › Diagnostics › Copy / Save as…). It
contains no user name, computer name or personal paths.

## Understanding errors

Failed files show three parts in the Queue details: **What happened**, **Why** and **What you can do**.
The raw FFmpeg / ab-av1 output is in the Log tab (tick *Raw tool output*) and always in
the log file.

## Manual AV1

**An encoder is missing from the Manual encoder list**
Only encoders that pass a real test encode are listed. Hover over the encoder list (or open
Settings › CPU & Hardware) to see why each missing encoder is unavailable. Hardware AV1 encoding
needs NVIDIA RTX 40+, Intel Arc / Core Ultra or AMD RX 7000+ with a current driver.

**"FFmpeg rejected a parameter"**
Check the Expert section (custom SVT-AV1 parameters / extra FFmpeg options) and custom filters. The
command preview in the Manual AV1 settings window shows exactly what will run; copy it to reproduce in a terminal.

**HDR looks washed out in the preview frames**
Frames are tone-mapped for display when possible; if your FFmpeg lacks `zscale`, HDR frames are shown
as-is. The encoded file itself keeps its HDR metadata (unless you chose tone-mapping to SDR).

**Pause vs Stop after current**
*Pause* freezes the running encode (nothing is lost) until *Resume*. *Stop after current* lets the
current file finish and then stops the queue. *Stop* kills the running encode immediately; its partial
output is deleted and the source is untouched.

## Tools

**"ab-av1.exe / ffmpeg.exe not found"**
Settings › Tools: browse to the executables, or use the download buttons (the System & tools tab and
the first-run check show ✓/✕ for every dependency). Put them in `%LOCALAPPDATA%\AV1 Studio\tools`
or next to `AV1Studio.exe` to have them found automatically.

**"This FFmpeg build has no libsvtav1 encoder" / "no libvmaf filter"**
Essentials/LGPL builds lack these. Use a BtbN `win64-gpl` build or a gyan.dev *full* build
(the download button fetches a suitable one).

**ab-av1 uses a different FFmpeg than the one I selected**
It doesn't: the app puts the selected FFmpeg folder first on the child process's `PATH`
(your system PATH is not modified). Check the "FFmpeg …" line at the top of the log.

**The "GPU encoding" button is greyed out**
No hardware AV1 encoder passed the test encode. You need an NVIDIA RTX 40 series (or newer) or an
Intel Arc / Core Ultra GPU, a recent driver, and an FFmpeg build with `av1_nvenc` / `av1_qsv`.
GTX 16 and RTX 20/30 series cards can decode AV1 but cannot encode it. Update the GPU driver, then
Settings › Tools › Re-detect. The log shows which encoders were found at startup.

**GPU-encoded files are bigger than expected**
That's normal: hardware encoders need more bits than SVT-AV1 for the same VMAF. Use a slower GPU
preset (p6/p7), or switch GPU encoding off for maximum savings.

## CRF search

**File "Skipped: No CRF reaches VMAF … within the size limit"**
ab-av1 couldn't find a CRF that meets the target VMAF while staying below
`--max-encoded-percent` (default 80% of the source). This is typical for sources that are
already very efficient (e.g. well-compressed HEVC). Options: lower the target VMAF, raise
*Max encoded size %*, or leave that file as is.

**CRF search is slow**
It encodes and VMAF-scores several samples per CRF attempt. Faster: a higher preset, fewer
samples (`--samples 2`), shorter samples, or a temp folder on a fast SSD. Results are cached;
re-running on unchanged files is instant.

**"Invalid use of --vmaf NUMBER"**
*Extra VMAF args* expects `name=value` pairs (e.g. `n_subsample=2`), not a score. Use *Target VMAF*.

**Errors about `crf-increment` / fractional CRF with old SVT-AV1**
ab-av1 ≥ 0.11 uses 0.25 CRF steps, which need SVT-AV1 ≥ 4.0. The app detects older SVT-AV1 and
passes `--crf-increment 1`; if detection failed, set *CRF increment* to 1 manually.

## Encoding

**Status FAILED — Source: PRESERVED**
Nothing was deleted. The reason is in the details pane and the log. Common causes:
* *Encode failed … decode error*: the source is damaged (`--fail-fast`). Try playing it; remux or
  re-download it.
* *Verification failed: Duration matches source*: the output length differs from the source.
  Often a broken source, or a custom filter that changes timing.
* *Audio tracks: expected N, found M*: a track couldn't be written to the chosen container. Use MKV.
* *Output file appeared during encoding*: another program created a file with the same name. The
  verified result is kept as `.partial`.

Right-click › **Retry** re-queues failed and skipped files.

**Queue paused: insufficient disk space**
The destination doesn't have `estimated size × safety factor + reserve` free. Free up space,
lower the safety factor (Settings › Output & folders), or enable source deletion so space is reclaimed
after each file. Note that the Recycle Bin option does not free space.

**MP4 output lost subtitles / audio was re-encoded**
MP4 can't hold image subtitles (PGS/VobSub) or TrueHD/DTS-style audio. The app drops/re-encodes
only those tracks and logs a warning. Use MKV (default) to keep everything untouched.

**The PC is sluggish while encoding**
CPU usage is *Auto* by default (a few processors kept free, *Below normal* priority). Choose
*Balanced* or *Low* in Settings › Performance, separately for the CRF search and the final encode,
or *Custom* for an exact number of processors.

**Multiple concurrent jobs aren't faster**
SVT-AV1 already uses all cores. Parallel jobs mostly add RAM, disk and temporary-space pressure.
Keep 1 unless you have specific reasons.

## Crashes & interruptions

**The app/PC crashed during an encode**
Sources are never modified during encoding. On the next start, interrupted files return to the
queue and leftover `*.av1studio.partial.*` / `.tmp.ab-av1-encoding.*` files are listed for deletion.
With *Reuse if valid* (default), outputs that were already finished and verify correctly are kept
instead of being encoded again.
The toolbar's trash button also finds leftover partial files at any time.

**A file shows "Source: replaced by verified output"**
In *same filename* mode, the source was deleted after verification but the final rename failed
(e.g. antivirus lock). The verified video is at the `.partial` path shown, and the rename is
completed automatically on the next start.

## Folders

**A folder in the destination is empty / a file was copied instead of encoded**
Folder jobs recreate every folder, including empty ones. Files that are not encoded (non-video files,
videos that are already AV1, too small, or where no CRF reaches the target) are copied unchanged so
the replica is complete. The file's status says *Copied* and why.

**"Output files already exist"**
The collision policy decides (Settings › Output & folders). With *Ask*, one dialog before the run
lets you reuse valid outputs, skip, replace or keep both.

## Paths & special characters

Paths with spaces, accents, brackets, apostrophes, `&`, `%`, Unicode, etc. are passed as individual
process arguments (never through a shell). If a very long path (> 260 characters) fails inside
FFmpeg, enable Windows long paths (`LongPathsEnabled`) or use a shorter destination folder.

## Reset

Close the app and delete `%LOCALAPPDATA%\AV1 Studio\queue.json` (queue),
`analysis-cache.json` (CRF cache) or `settings.json` (settings). Your videos are not affected.
