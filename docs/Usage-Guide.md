# Usage Guide

| Getting Started | Using the App | Technical Reference | Project |
|---|---|---|---|
| [Home](index.md) | [**Usage Guide**](Usage-Guide.md) | [Architecture](Architecture.md) | [Repository](Repository.md) |
| [Installation](Installation.md) | [Conversion Methods](Conversion-Methods.md) | [XDVDFS Technical Docs](XDVDFS-Technical-Documentation.md) | [Building from Source](Building-from-Source.md) |
| | [XISO Explorer](XISO-Explorer.md) | [Troubleshooting & FAQ](Troubleshooting-and-FAQ.md) | [Release Notes](Release-Notes.md) |

---

The main window is organized into three views, selectable from the navigation buttons at the top:

| View | Purpose |
|:---|:---|
| **Convert** | Batch-convert ISOs (and archives containing ISOs) into trimmed XISO, ZAR, CSO, or CHD files |
| **Test Integrity** | Validate ISO, CSO, ZAR, and Xbox CHD images and organize results |
| **Explorer** | Browse the contents of an Xbox ISO, CSO, ZAR, CHD or GOD/SVOD container without extracting it, with title metadata and XBE image previews |

Below the three views, a shared **status bar** shows a live log, progress bar, cancel button, statistics (total / success / fail / skipped / processing time), and per-drive read/write speed indicators.

---

## The Convert Tab

### 1. Select Folders

- **Input folder** — the folder containing your source files. Supported inputs: `.iso` (Redump full-disc images or optimized XISO files), `.zip`, `.7z`, `.rar`.
- **Output folder** — the folder where converted `.iso` (XISO) files are written.

> The system temporary folder (or a subfolder of it) cannot be selected as an input or output folder. The application refuses it deliberately to avoid recursive processing and cleanup conflicts.

Enable **Search Subfolders** to recurse into nested directories; the file list is rescanned immediately.

### 2. Select Files to Convert

Once an input folder is chosen, the **Select Files to Convert** list is filled with every supported file found (respecting **Search Subfolders**). Each row shows a **Select** checkbox, the file name relative to the input folder, and the file size.

- Untick any file you do not want to process; only ticked files are converted.
- Use **Select All** / **Deselect All** to toggle the entire list.
- The list is refreshed automatically after each batch, so it reflects deleted originals and newly created outputs.

### 3. Conversion Engine

All conversion is performed in-process by the **[XISOSharp](https://github.com/purelogiccode/XISOSharp)** library, with **[CHDSharp](https://github.com/purelogiccode/CHDSharp)** handling CHD encoding — there is no external tool. Choose the **Output Format** in the Options panel:

- **XISO** (default) — tightly packed, optimized XISO (`.iso`); images that already carry the optimized tag are skipped automatically.
- **ZAR** — ZArchive (`.zar`, zstd) for direct use in Xenia canary.
- **CSO** — compressed ISO (`.cso`, CISO v2/LZ4), compatible with the xdvdfs ecosystem.
- **CHD** — Compressed Hunks of Data (`.chd`, CHD v5) with the chdman `createdvd` preset (4096-byte hunks, 2048-byte units, `lzma,zlib,huff,flac`).

See the [Conversion Methods](Conversion-Methods.md) page for details.

### 4. Options

| Option | Behavior when enabled |
|:---|:---|
| **Output Format** | Produces `.iso` (optimized XISO), `.zar` (ZArchive/zstd), `.cso` (CISO v2/LZ4), or `.chd` (CHD v5, chdman `createdvd` preset). |
| **Skip $SystemUpdate** | Excludes the `$SystemUpdate` folder from the output for extra space savings (~100–300 MB). |
| **Delete Originals** | Replaces each input file with its converted version. Deletion happens **only after** the output has been produced and verified. An archive is kept when any entry was skipped or any extracted image was not converted. |
| **Check Output Integrity** | Runs a structural validation on each newly created XISO and a full deep verification (every hunk and hash) on each newly created CHD before reporting success (for ZAR/CSO the source image is validated instead). |
| **Search Subfolders** | Includes files found in subdirectories of the input folder in the list. |

### 5. Start, Monitor, Cancel

- Click **Start Conversion** to begin the batch.
- The progress bar shows per-file and overall progress; the log pane records every action with timestamps.
- The statistics panel updates live: **Total**, **Success**, **Failed**, **Skipped**, **Processing Time**, plus **read/write speed** with a drive-letter indicator for the currently active disk.
- Click **Cancel** at any time. The current file operation is cancelled as soon as possible; already-converted files remain valid.

### 6. What Happens to Each File

The orchestrator decides per input file:

1. **`.iso`** — converted with the XISOSharp engine (or CHDSharp for CHD) into the selected output format (`.iso`, `.zar`, `.cso`, or `.chd`).
2. **`.zip` / `.7z` / `.rar`** — extracted to a temporary folder (with automatic drive fallback if the temp drive is short on space), the first ISO inside is converted, then temporaries are cleaned up. Entries that are skipped (additional ISOs, non-ISO images, unsafe paths) are reported in the log.
   - Password-protected/encrypted archives are detected and **skipped with a clear message** instead of a cryptic failure.
   - With **Delete Originals** enabled, an archive is kept whenever an entry was skipped or an image was not converted.
3. At the end, a **summary** is logged (files processed, succeeded, failed, skipped) and the operation is finalized.

Files stored in cloud-sync folders (OneDrive, etc.) that are not hydrated locally are detected and retried automatically with exponential backoff while the cloud provider downloads them.

---

## The Test Integrity Tab

Use this view to validate images without converting them. Supported formats:

- **`.iso`** and **`.cso`** (CISO, including split `.1.cso` part sets) — a deep audit of the XDVDFS file tree, with an optional sequential sector scan.
- **`.zar`** (ZArchive/zstd) — opens the archive (header, index, name table, file tree), walks the whole tree, and with the deep scan enabled decompresses every file to prove all blocks are readable.
- **`.chd`** (Xbox DVD images only) — verifies the CHD container (header-only, or every hunk and checksum with the deep scan enabled) and then audits the Xbox filesystem inside the decompressed image. CD/GD-ROM/hard-disk CHDs are rejected.

### Options

| Option | Behavior when enabled |
|:---|:---|
| **Move Passed Files** | Moves images that pass validation into a `_success` subfolder (a split CISO's continuation parts travel with part 1) |
| **Move Failed Files** | Moves images that fail validation into a `_failed` subfolder |
| **Search Subfolders** | Recurses into subdirectories of the input folder |
| **Perform Deep Scan** | Reads the entire image — every sector for ISO/CSO, every decompressed block for ZAR, every CHD hunk and checksum for CHD — to detect physical corruption / bad sectors (slower, but thorough) |

### Workflow

1. Select the **input folder** containing the images to test.
2. Tick the files you want to test in the **Select Files to Test** list (or use **Select All** / **Deselect All**).
3. Choose any of the options above.
4. Click **Start Integrity Test**.
5. Review the log: each file is reported as passed or failed, with the reason for failure where applicable.

> File moves performed by the test view use the same retry logic as conversion: transient locks (antivirus scans, cloud hydration, network hiccups) are retried with exponential backoff before being reported as failures.

---

## The Explorer Tab

The built-in Image Explorer lets you inspect the contents of an Xbox `.iso`, `.cso`, `.zar`, `.chd` or GOD/SVOD container without extraction. The right-hand panel shows title metadata (title name/ID, region, disc, version, publisher, developer, genre, description) and renders the XBE logo, title and save images. See the dedicated [XISO Explorer](XISO-Explorer.md) page for the full walkthrough.

---

## Statistics Panel

The shared statistics panel at the bottom of the window is active during conversion and testing:

| Field | Meaning |
|:---|:---|
| **Total Files** | Number of files discovered in the batch |
| **Success** | Files processed successfully |
| **Failed** | Files that could not be processed (reason logged) |
| **Skipped** | Files intentionally skipped (e.g., unsupported, encrypted archives, insufficient disk space) |
| **Processing Time** | Elapsed time for the current/last batch |
| **Read Speed / Write Speed** | Live throughput of the drives involved, with the drive letter as an indicator |

The **memory indicator** shows the current process memory usage, which is useful when processing very large batches of large images.

---

## Logging

Every operation is written to the in-application log pane. The log includes:

- File-by-file decisions (source, destination, engine output)
- Warnings for skipped files with clear reasons (disk space, FAT32 size limit, permissions, locked files)
- Retry attempts for transient failures (file locks, network errors)
- A final batch summary

When an unexpected error occurs, the application can send an automatic bug report (exception message and stack trace) to the developer. **Environmental errors — such as full disks, network failures, or files held open by another process — are not reported as bugs**; they are shown to you with actionable messages instead.

---

## Practical Tips

- **Test one file first** when converting a new type of image, then run the whole batch.
- **Keep 10–15% free space** on the output drive; the application pre-checks free space but headroom avoids edge cases.
- **Antivirus software** can temporarily lock newly created files. The application waits and retries automatically, but real-time scanning of large ISO folders slows batches down — consider adding your working folders to the exclusion list.
- **Network shares** are fully supported (UNC paths and mapped drives). Wired connections reduce retry-related slowdowns.
- **FAT32 output drives** cannot hold files larger than 4 GB; the application detects this in advance and skips those files with a clear message. Use NTFS or exFAT for modern game images.
