# What's New in XISO Studio

<!-- Keep this file focused on the newest release. Full history lives in docs/Release-Notes.md. -->

## Version 3.0.3

**Release date:** October 2026

Version 3.0.3 is a **reliability patch for 3.0.2**. It stops expected input problems - files that are not Xbox images (CD/GD-ROM CHD dumps, PS5 packages, corrupt or renamed ISOs) and folder-picker failures - from being uploaded as bug reports, and makes drag-and-drop and extract-to-temp skip drives that cannot be written.

### Fixes

#### Fewer false bug reports
- **Invalid or unsupported images are recognized as user input.** Opening a `.chd` that is a CD or GD-ROM image (for example Sega Chihiro arcade dumps), or a file that is not an Xbox image at all (a PS5 `.pkg`, a truncated download, a renamed ISO), is now logged at Information and explained with a clear message - "The selected file is not a valid Xbox or Xbox 360 image, or it cannot be read" - instead of being uploaded as an application bug.
- **Folder-picker failures no longer generate bug reports.** When the selected folder no longer exists (deleted, disconnected drive, unavailable network share), the app now logs at Information and tells you the folder is no longer available instead of reporting a defect.
- **Closing during a long operation no longer generates a bug report.** The "did not complete within timeout" exit path is normal behavior and is now logged at Information.

#### More reliable drag-and-drop and extraction
- **Unwritable drives are skipped.** The temp-folder resolver now creates the candidate folder immediately and moves on to the next drive when creation fails (ACL-restricted roots, BitLocker-locked or read-only volumes), instead of failing the whole drag or extraction. Environmental I/O failures are logged at Information with a friendly message.
- **Drive search is resilient.** A drive that fails inspection while the app looks for free space is skipped instead of aborting the whole search.

#### Dependencies
- **Avalonia 12.1.4**, **SharpCompress 0.50.5**, **Meziantou.Analyzer 3.0.297**, **xunit.runner.visualstudio 4.0.1** - analyzer and test-runner updates are build-time only.

### Upgrading
Download the archive for your platform and replace the previous files. There are no configuration, format, or workflow changes - 3.0.3 is a drop-in replacement for 3.0.2.

---

## Version 3.0.2

**Release date:** October 2026

Version 3.0.2 is a **post-3.0.1 reliability patch**. It recognizes fatal device hardware errors from a failing drive, stops locked output and source files from being uploaded as bug reports, and updates the XISOSharp engine to 1.4.2 while keeping invalid ZAR inputs classified as invalid images.

### Fixes

#### Fewer false bug reports
- **Fatal device hardware errors are recognized.** A drive that reports `ERROR_DEVICE_HARDWARE_ERROR` (Win32 483, `0x800701E3` — "The request failed due to a fatal device hardware error") during archive analysis or extraction is now treated like a failing drive: the batch stops with a drive-health message (check the cable, run `chkdsk`, copy the files to a healthy drive) and the event is logged at Information, so a dying disk no longer uploads one bug report per file. The existing `ERROR_IO_DEVICE` (`0x45D`) handling is unchanged, and the localized Italian message from the reports is recognized as well.
- **Locked output files no longer generate bug reports.** Before overwriting an existing result, the application briefly retries the delete while another process holds the file (an emulator playing the previous output, antivirus, or an Explorer preview). A file that stays locked is reported as failed with guidance to close the application using it, and is logged at Information instead of Warning. The same classification now covers deleting the originals when **Delete Originals** is enabled.
- **Device and network failures during archive analysis** are logged at Information while falling back to the default temp path, instead of one Warning per file.

#### Conversion engine
- **XISOSharp updated to 1.4.2** (from 1.4.1). XISOSharp 1.4.2 now returns its documented failure result for a structurally invalid image instead of throwing, so a failed ZAR pack audits the source and still reports genuinely invalid inputs as **invalid image** rather than a generic unconverted failure.
- Analyzer packages updated (Meziantou 3.0.294, Roslynator 5.0.1) — build-time only, no runtime impact.

### Upgrading
Download the archive for your platform and replace the previous files. There are no configuration, format, or workflow changes — 3.0.2 is a drop-in replacement for 3.0.1.

---

## Version 3.0.1

**Release date:** October 2026

Version 3.0.1 is a **reliability patch for 3.0.0**. It stops environmental problems — files locked by antivirus or a download manager, and inaccessible system folders on Linux/macOS — from being uploaded as bug reports, and fixes a progress-reporting race that could show percentages out of order during CSO, ZAR, and CHD encoding.

### Fixes

#### Fewer false bug reports
- **Locked files no longer generate bug reports.** When a tested image cannot be moved to `_success`/`_failed` because another process still holds it, the file is still reported as failed and the batch continues, but the event is now logged at Information level instead of Error, so it is no longer auto-uploaded as an application bug. The same applies when the source cannot be opened for testing and the cloud-copy fallback also fails with a sharing violation.
- **Inaccessible temp roots are skipped silently on Linux and macOS.** `DriveInfo.GetDrives()` returns system mounts (`/root`, `/.snapshots`, `/sys/kernel/tracing`, …) that the current user cannot read. The startup cleanup scan now logs each one at Debug level and skips it instead of emitting a Warning per mount point, which previously auto-uploaded several bogus reports on every launch.

#### Correct progress reporting
- **Progress is now reported in order.** The CSO, ZAR, and CHD progress adapters used `Progress<T>`, which posts callbacks to the thread pool when no UI synchronization context is present. Concurrent callbacks could race on the "last reported percentage" and deliver values out of order (for example `54%` before `49%`), making the progress bar appear to move backwards. The adapters now run synchronously on the reporting thread, so percentages are monotonic and every step is delivered in order.

#### Tests and CI
- Platform-dependent tests now skip correctly on Linux and macOS instead of failing (drive-letter cases, Windows file-locking retry tests, and macOS path-length limits), and the CI matrix is green on Windows, Linux, and macOS again.

### Upgrading
Download the archive for your platform and replace the previous files. There are no configuration, format, or workflow changes — 3.0.1 is a drop-in replacement for 3.0.0.

---

## Version 3.0.0

**Release date:** September 2026

Version 3.0.0 is the **CHD, cross-platform & logging release**: the application was ported from WPF to **Avalonia** and now runs on **Windows, Linux, and macOS**, the [CHDSharp](https://github.com/purelogiccode/CHDSharp) library is now built in, so Xbox and Xbox 360 images can be converted to **CHD** (`.chd`, Compressed Hunks of Data) and Xbox DVD CHD files can be integrity-tested and explored without extraction, and all logging runs through [Serilog](https://serilog.net/) with automatic bug reporting.

### Highlights

#### Cross-platform: Windows, Linux, and macOS
- The UI was ported from WPF to **[Avalonia](https://avaloniaui.net/)** with the same dark theme, colors, fonts, layout, and controls — one codebase now produces binaries for **Windows, Linux, and macOS** on **x64 and ARM64** (six release archives).
- All imaging libraries (XISOSharp, ZArchiveSharp, CHDSharp, SharpCompress) are pure managed code with no native dependencies, so every feature — conversion, testing, and exploration — works identically on all three platforms.
- Windows-specific integrations degrade gracefully: the disk read/write speed monitor uses Windows performance counters and shows **N/A** on Linux/macOS, links/pickers use the native OS dialogs, and the 7-Zip CLI fallback is bundled for every platform (`7za.exe`/`7za_arm64.exe` on Windows, `7zz_linux_x64`/`7zz_linux_arm64` on Linux, `7zz_osx` on macOS) with a system `7z` on `PATH` as a further fallback.
- CI now builds and tests on **Windows, Linux, and macOS** and publishes all six platform archives.

#### CHD output format
- The Convert tab's **Output Format** selector now includes **CHD** (`.chd`, CHD v5). The optimized game partition is encoded with the chdman `createdvd` preset — 4096-byte hunks, 2048-byte units, and the `lzma,zlib,huff,flac` codec list — and tagged as a DVD image.
- Non-optimized (Redump) inputs are rewritten to the game partition first, so **Skip $SystemUpdate** and **Delete Originals** work exactly as they do for XISO/ZAR/CSO.
- **Check Output Integrity** deep-verifies the new CHD — every hunk is decompressed and every checksum and hash is validated — before the conversion is reported as successful.

#### Test Xbox CHD files
- The Test tab now accepts `.chd` files, limited to Xbox DVD images: CD/GD-ROM/hard-disk CHDs and differential child CHDs are rejected with a clear message.
- The CHD container is verified first (header-only, or every hunk and checksum with **Perform Deep Scan**), and the Xbox filesystem structure inside the decompressed image is then audited with XISOSharp.

#### Explore Xbox CHD files
- The Explorer opens `.chd` files, decompressing hunks on demand, and browses and copies out entries through XISOSharp just like ISO/CSO/ZAR.
- Both game-partition-only CHDs (produced by this app) and full Redump-image CHDs (produced by `chdman createdvd`) are supported — partition offsets are auto-detected.

#### Structured logging with automatic bug reports
- All logging now runs through [Serilog](https://serilog.net/): the on-screen log viewer, a rolling daily file log (`%LocalAppData%\XISOStudio\logs`), and a bug-report sink that forwards every **Warning-or-higher** event to the Bug Report API.
- Reports include complete Environment, Error, and Exception sections (type, message, source, and stack trace, including nested exceptions); expected user/environmental errors are logged at Information level so they never generate noise.
- Every `catch` block logs at an appropriate level, Avalonia's internal diagnostics are routed through the same pipeline, and global handlers (`AppDomain.UnhandledException`, `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`) report through the same path.

#### Pick exactly which files to process
- After choosing an input folder, the Convert view lists every supported file (`.iso`, `.zip`, `.7z`, `.rar`) and the Test view lists every ISO, with a **Select** checkbox, file name, and size.
- Use **Select All** / **Deselect All** to toggle the whole list, then click **Start** — only ticked files are processed.
- **Search Subfolders** now rescans the list immediately when toggled, and the list refreshes automatically after each batch (for example after originals are deleted or tested files are moved to `_success`/`_failed`).

#### Choose the output format
- A new **Output Format** selector on the Convert tab produces **XISO** (default), **ZAR** (`.zar`, ZArchive/zstd — Xenia canary loads it directly), **CSO** (`.cso`, CISO v2/LZ4, byte-identical to `xdvdfs compress`), or **CHD** (`.chd`).
- **Skip $SystemUpdate**, **Delete Originals**, and integrity checking work for all formats — for ZAR/CSO the integrity check validates the source image that gets packed.

#### Consistent, responsive lists
- The lists use the same dark terminal styling as the rest of the app and load in chunks, so folders with thousands of files stay responsive.
- The Convert and Test panels (folder pickers, options, and file list) now sit on the **left**, with the log viewer / XISO explorer on the **right** and a draggable splitter between them — the same layout used by the other BatchConvert tools.
- The **Explorer** tab shows the file picker at the top with the explorer list directly below it and uses the full window width — the log panel is hidden on this tab.
- The **Options** panel on both tabs is collapsible: click its header to hide the checkboxes and give the file list more room.
- File names are shown relative to the selected input folder (subfolders included), and sizes are formatted for readability.
- The engine applies the same extension filters as the lists, so the UI can never offer a file the converter cannot handle.

#### ISO/XISO only (CUE/BIN support removed)
- The bundled `bchunk.exe` and `.cue` input support were removed. The application now supports only `.iso` (Redump full-disc images) and already-optimized XISO files, plus archives (`.zip`, `.7z`, `.rar`). CUE/BIN images are no longer listed or converted.

#### Reliability and bug fixes
- **No more silent data loss** — with **Delete Originals** enabled, an archive is removed only when every entry was extracted and every extracted image was converted; skipped entries (extra ISOs, non-ISO images, already-optimized files) keep the archive.
- **No output collisions** — two inputs with the same file name (for example `Disc1/game.iso` and `Disc2/game.iso`) now produce `game.iso` and `game (2).iso` instead of overwriting each other.
- **`Skip $SystemUpdate` works everywhere** — already-optimized inputs are rewritten through the filter before CSO/CHD packing, so the option is honored for every output format.
- **Cloud and split images are handled correctly** — split CISO sets copied to a temporary working folder keep their `.1.cso`/`.2.cso` part markers and travel together; the cloud-failure path cleans up its temporary folder.
- **One bad file no longer aborts a batch** — files that vanish, lock, or cannot be read mid-scan or mid-test are reported individually and the remaining files continue.
- **Fewer false bug reports** — locked files, full disks, offline networks, permission problems, and update-check failures log at Information level; retries only retry genuinely transient errors.
- **Safer cleanup and shutdown** — temporary-folder cleanup only removes app-created GUID work folders older than six hours, the Exit confirmation can't be bypassed or shown twice, and the explorer is never disposed while a copy-out is still reading from it.
- **Cross-platform correctness** — path comparisons respect case-sensitive file systems, 7-Zip arguments are escaped per platform, and compressed outputs use a size-aware free-space estimate instead of the uncompressed size.
- **A pre-release review added more fixes** — explorer extractions run off the UI thread again, drag-and-drop files are kept until the drop target finishes copying, failed extractions clean up their temp folders, split CISO sets keep their original extension casing when moved, and cross-mount-point moves on Linux/macOS check destination free space. The Serilog pipeline is now configured before Avalonia starts (capturing platform-startup diagnostics), Avalonia framework warnings are no longer auto-reported, and the log viewer can no longer feed failures back into the logging pipeline.
- **A final pre-release review closed the remaining edge cases** — a failed test whose "move failed files" move also failed was counted twice; a cross-volume move skipped for lack of destination space was reported as a successful move; an archive could be deleted even though it still held a lone split-CISO continuation part (for example `disc.2.cso`); a missing `.zar`/`.chd` file counted toward the "not valid Xbox ISOs" warning; a mis-routed CHD conversion could silently produce an XISO under a `.chd` name; and macOS folder validation treated folders that differ only by letter case as distinct. **Cancel** and **Exit** are now honored during the pre-operation temporary-folder scan, and the image explorers always return a normalized parent path. The on-screen log viewer is now bounded and throttled (newest 2,000 lines, 500 flushed every 100 ms), and XISOSharp's chunked console output — including its backspace progress animation that produced floods of `[xiso]` noise — is reassembled into clean, complete lines, so heavy logging can no longer freeze the window or hide the **Cancel** button. Log messages are also rendered as plain text (no Serilog 4 JSON-style quotes around file names and paths), and the finish path drains pending UI updates before reading the summary counters, so the batch summary always includes the last file (a 2-file batch can no longer report 1 success). CHD conversion now logs its prepare/encode/verify phases and a 5% progress line, so the log pane shows activity during the minutes-long encode like the other formats.

### Upgrading
Download the archive that matches your platform and architecture. No action is required for existing Windows users — the portable layout and all existing formats keep working unchanged. Version 3.0.0 adds the Serilog logging pipeline, per-file selection lists, ZAR/CSO output, and CHD support (the **CHDSharp** 1.4.3 package, pure managed code), and changes the framework the window is drawn with (WPF → Avalonia). CUE/BIN images are no longer supported — convert them to ISO with another tool first. The workflow changed slightly: previously everything found in the folder was processed automatically; now tick the files you want (all files are ticked by default).

---

See the complete history in [docs/Release-Notes.md](docs/Release-Notes.md).
