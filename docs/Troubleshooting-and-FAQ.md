# Troubleshooting & FAQ

| Getting Started | Using the App | Technical Reference | Project |
|---|---|---|---|
| [Home](index.md) | [Usage Guide](Usage-Guide.md) | [Architecture](Architecture.md) | [Repository](Repository.md) |
| [Installation](Installation.md) | [Conversion Methods](Conversion-Methods.md) | [XDVDFS Technical Docs](XDVDFS-Technical-Documentation.md) | [Building from Source](Building-from-Source.md) |
| | [XISO Explorer](XISO-Explorer.md) | [**Troubleshooting & FAQ**](Troubleshooting-and-FAQ.md) | [Release Notes](Release-Notes.md) |

---

## Common Messages and How to Resolve Them

### Not enough disk space

**Message pattern:** *"Not enough disk space ... Required: ... Available: ..."*

The output drive does not have room for the converted file (with a safety margin).

- Free up space on the output drive, or
- Select a different output folder on another drive.

The application checks the output drive **before** converting, so a full disk skips the file with this message instead of failing halfway and leaving a corrupt partial file. If a write does still hit a full disk (for example, another program filled the drive concurrently), any partial output file is deleted automatically.

### File is too large for FAT32

**Message pattern:** *"parameter is incorrect"* on move, or a FAT32 limit message.

FAT32 cannot store files larger than 4 GB. Modern game images routinely exceed this.

- Use an **NTFS** or **exFAT** drive as the output destination.

### Access to the path is denied

The output folder (or the application folder itself) does not grant write permission.

- Do **not** run the application from, or write output to, protected folders such as `C:\Program Files`, `/usr`, or `/Applications`.
- Pick a normal user-writable folder (for example `D:\Games\Out` or `~/Games/Out`).
- If you must write to a protected location, adjust the folder's permissions — running the application as administrator/root is not required for normal use.

### File locked / being used by another process

Another program (most commonly antivirus real-time scanning, a cloud-sync client, or an indexer) temporarily holds the file.

- The application **waits and retries automatically** with exponential backoff (up to six attempts) before giving up.
- If the message persists, add your working folders to your antivirus exclusion list or close the program that holds the file.
- Files created moments ago are especially prone to this while the antivirus scans them; the retry logic exists precisely for this case.
- A file that stays locked after the retries is reported as failed (or left in place) but is **not sent as an automatic bug report** — since 3.0.1 the application treats persistent locks as environmental and logs them at Information level.
- The same applies to an existing output file that must be replaced: the delete is retried briefly, and if the file stays locked (for example, an emulator is still running the previous result) the conversion is reported as failed with guidance to close the program using it. Locked outputs are never uploaded as bugs.

### The existing output file is in use

**Message pattern:** *"The existing output file ... is in use by another process ..."*

The conversion needs to replace (or delete) an existing result, but another program still has it open — most often an emulator (for example Xenia loading the previous `.zar`/`.iso`), a media player, antivirus scanning, or an Explorer preview.

- Close the program that is using the file and run the conversion again.
- The delete is retried automatically a few times before giving up; if the file is held continuously, the retry cannot succeed.
- This is an environmental condition: it is logged at Information level and does **not** generate a bug report.

### Network errors (UNC paths and mapped drives)

Network paths are fully supported. Transient network failures are retried automatically with exponential backoff.

- Use wired connections for large batches.
- Ensure the share stays reachable (no scheduled disconnects/sleep) during the run.

### Cloud files (OneDrive / Dropbox)

Files stored in cloud-sync folders may not be physically present on disk (online-only).

- The application detects this and retries with exponential backoff while the provider hydrates the file.
- For very large batches, right-click the folder in Explorer and choose **Always keep on this device** first.

### Encrypted or password-protected archives

**Message pattern:** an encrypted-archive notice; the file is skipped.

The archive cannot be extracted without the password.

- Extract the archive manually with 7-Zip/WinRAR, then run the application on the extracted ISO.

### Not a valid Xbox ISO image

The file was read, but no XDVDFS volume descriptor with the `MICROSOFT*XBOX*MEDIA` signature was found at any known partition offset.

- Verify the file is an Xbox or Xbox 360 game image (not a PC/DVD/Blu-ray ISO).
- Re-dump or re-download the image if it came from an unreliable source; a truncated dump fails validation.
- Test the image on the **Test Integrity** tab for a more detailed diagnosis.

The **"High Rate of Invalid ISOs Detected"** warning counts only images that failed format validation — disk errors, access-denied errors, and move failures do not count toward it.

The explorer shows the same message when you open a file that is not an Xbox image:

- A `.chd` whose media type is **CD** or **GD-ROM** (for example Sega Chihiro arcade dumps) is a valid
  CHD file, but it is not an Xbox DVD image. The explorer only parses the Xbox XDVDFS filesystem, so
  it rejects the file and names the detected media type (`cd`, `gd-rom`) in the message.
- Files for other consoles (a PS5 `.pkg`, a renamed PC ISO) and truncated downloads are rejected the
  same way.
- Since **3.0.3**, these expected rejections are logged at Information and are **not** uploaded as
  bug reports; the message explains what to select instead.

### Font / rendering error at startup

**Message pattern:** a startup error mentioning fonts or rendering.

This happens when required system fonts are missing or the rendering backend cannot initialize.

- Windows: run `sfc /scannow` to repair system files; ensure *Segoe UI* and *Arial* are installed.
- Linux: install a font package (for example `ttf-dejavu` or `ttf-mscorefonts-installer`) and make sure the graphics driver supports Skia/OpenGL.
- macOS: fonts ship with the OS; a missing GPU/rendering backend is usually the cause.

### The application seems not to start / the main window takes a long time to appear

In versions before **2.8.0**, startup ran temporary-folder cleanup before showing the main window.
That cleanup probes every drive, and waking an idle spinning disk can take ~20 seconds — making the
app look like it never started (the process was running with no window). It also caused a pause before
each batch (pre-operation cleanup).

- **Update to 2.8.0 or later** — cleanup now runs after the window is shown, on a background thread.
- On older versions, wait ~30 seconds on first launch; subsequent launches are fast while the disk
  remains spun up.

### The drive reported a hardware I/O error

**Message pattern:** *"The drive reported a hardware I/O error ..."*, an `I/O device error`
(`ERROR_IO_DEVICE`, `0x45D`), or a *fatal device hardware error* (`ERROR_DEVICE_HARDWARE_ERROR`,
Win32 483 / `0x800701E3`, for example the Italian *"Richiesta non riuscita a causa di un errore
hardware del dispositivo irreversibile"*).

The read or write request failed at the hardware level — the drive is failing, was disconnected, or is
power-cycling. External USB drives with a bad cable/port or bad sectors are a common cause. The batch
is stopped deliberately because continuing could produce corrupt output.

- Check the cable/connection and the drive's health (for example, run `chkdsk`, or check S.M.A.R.T. status).
- Copy the source files to a healthy local drive and retry.
- These errors are treated as environmental: they are logged at Information level with the drive-health
  guidance and are not sent as automatic bug reports. Archive extraction does not retry them as if they
  were transient network glitches.

### The output file is missing or the conversion failed after antivirus activity

Antivirus software can quarantine or delete the freshly created XISO during conversion. XISOSharp writes
the output directly to the requested path; if the file cannot be found or read back afterwards, the
conversion is reported as failed and the partial output is removed. Check your antivirus quarantine
list and consider excluding the input/output folders from real-time scanning.

---

## Frequently Asked Questions

**Which conversion engine is used?**
All conversion is performed by the [XISOSharp](https://github.com/purelogiccode/XISOSharp) library, with CHD output encoded by [CHDSharp](https://github.com/purelogiccode/CHDSharp). See [Conversion Methods](Conversion-Methods.md) for how it works.

**Does the tool modify my source files?**
Only when **Delete Originals** (Replace Originals) is enabled — and even then, originals are removed only after the converted file has been produced and verified. Archives are removed only when every entry was extracted and every extracted image was converted; if an entry was skipped or an image was not converted, the archive is kept.

**Are Xbox 360 images supported?**
Xbox and Xbox 360 images are supported; conversion repacks the game partition of Redump-style dumps into an optimized XISO.

**Where are temporary files stored?**
In the system temp folder, in dedicated subfolders. They are cleaned automatically after each file and at startup (orphaned leftovers from crashes are removed too). If the temp drive lacks space, other local drives are used as fallback. Startup cleanup only removes app-created work folders (GUID-named children of `XISOStudio_*` folders) that are older than six hours, so unrelated folders and another instance's active work folders are never deleted.

**Does the application run on Linux and macOS?**
Yes. The UI is built with Avalonia, and all image libraries are pure managed code, so builds are provided for Windows, Linux, and macOS (x64 and ARM64). The disk read/write speed monitor uses Windows performance counters and shows `N/A` on other platforms; the 7-Zip CLI fallback used for complex archives is bundled in every release archive (`7za*.exe`, `7zz_linux_*`, or `7zz_osx`), with a system `7z` on `PATH` as a further fallback.

**Where is the log file?**
The app writes a rolling log under the per-user application-data folder — `%LocalAppData%\XISOStudio\logs\log-*.txt` on Windows, `~/.local/share/XISOStudio/logs` or `~/Library/Application Support/XISOStudio/logs` elsewhere (10 MB per file, 14 files retained). It contains the same messages shown in the log pane, with levels and full exception details.

**Does the application collect my data?**
It sends an anonymous usage ping and, for warnings and errors, an automatic bug report containing the message, environment details, and exception details. Expected environmental errors (disk space, network, device/hardware failures, locked files) are logged at Information level and are never reported. No personal data or file contents are collected.

**How do I report a bug?**
Warnings and errors are reported automatically with environment and exception details. For anything else, open an issue at <https://github.com/purelogiccode/XISOStudio/issues> and include the relevant lines from the log pane or the log file (under the per-user application-data folder, see above).

**Where do I download new versions?**
From the [Releases](https://github.com/purelogiccode/XISOStudio/releases) page. The application checks for updates automatically and offers to open the page when a new version exists.

**Is there an installer?**
No. The application is portable — extract and run (see [Installation](Installation.md)).

**Can I run multiple instances at once?**
Not recommended: parallel instances compete for disk bandwidth and the replace-originals workflow becomes risky if they share folders. Temporary-folder cleanup is age-based (six hours) and only removes app-created GUID work folders, so one instance cannot delete another's active folders.
