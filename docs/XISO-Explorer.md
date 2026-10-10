# XISO Explorer

| Getting Started | Using the App | Technical Reference | Project |
|---|---|---|---|
| [Home](index.md) | [Usage Guide](Usage-Guide.md) | [Architecture](Architecture.md) | [Repository](Repository.md) |
| [Installation](Installation.md) | [Conversion Methods](Conversion-Methods.md) | [XDVDFS Technical Docs](XDVDFS-Technical-Documentation.md) | [Building from Source](Building-from-Source.md) |
| | [**XISO Explorer**](XISO-Explorer.md) | [Troubleshooting & FAQ](Troubleshooting-and-FAQ.md) | [Release Notes](Release-Notes.md) |

---

The **Explorer** tab provides a native file browser for Xbox and Xbox 360 images and archives. It reads the XDVDFS filesystem directly from ISO/CSO images through the [XISOSharp](https://github.com/purelogiccode/XISOSharp) library, the CHD image through [CHDSharp](https://github.com/purelogiccode/CHDSharp) (decompressed on demand), and the ZAR archive tree through ZArchiveSharp, so you can inspect images without extracting anything. The file picker sits at the top with the explorer list directly below it, and the explorer uses the full window width (the log panel is hidden on this tab).

## Opening an Image

1. Switch to the **Explorer** tab.
2. Click **Browse...** and select a `.iso`, `.cso`, `.zar`, or `.chd` file.
3. The root of the image appears in the file list.

Supported inputs:

- **`.iso`** — standard XISO and Redump ISOs; the explorer automatically locates the game partition.
- **`.cso`** — CISO containers (single file or a split `.1.cso` part set); the game partition is read through the decompressed view.
- **`.zar`** — ZAR archives (ZArchive/zstd); the game tree is read directly from the compressed container.
- **`.chd`** — CHD v5 images; the Xbox filesystem is read from the on-demand decompressed view. Parsing is limited to Xbox DVD images — CD/GD-ROM/hard-disk CHDs are rejected with a clear error.

## Browsing

| Action | Result |
|:---|:---|
| Double-click a folder | Enter the folder and list its contents |
| **Up** button | Move to the parent directory |
| Path label | Shows the current path inside the image |

The list displays three columns:

| Column | Contents |
|:---|:---|
| **Name** | File or folder name |
| **Size** | Size in bytes (human-readable) |
| **Type** | File or directory |

## Opening Files

**Double-click any file** to extract it to a temporary location and open it with its default associated application (for example, a `.xbe` viewer or a text editor). The temporary copy is cleaned up by the application's normal temp-folder housekeeping.

## Drag-and-Drop Extraction

Drag one or more files (or whole folders) from the explorer list onto:

- Your file manager (Windows Explorer, Finder, Nautilus, …),
- The Desktop, or
- Any folder.

The selected items are extracted from the ISO to the drop target. This is the quickest way to pull individual files or directories out of an image without extracting the entire disc.

## Notes and Limitations

- The explorer operates read-only; it never modifies the source image.
- Very deep directory trees are handled iteratively by XISOSharp (no recursion limits); the ZAR tree
  walk is bounded at 1024 levels, mirroring ZArchiveSharp's extractor.
- Images that fail XDVDFS validation (or ZAR archives with a damaged header/index/tree, or CHD images
  with a damaged container) are rejected with a clear error rather than showing unreliable content.
- Files that are valid but are not Xbox images — CD/GD-ROM CHD dumps (for example Sega Chihiro arcade
  games) and packages for other consoles (a PS5 `.pkg`) — are rejected with a message naming the
  problem. Since **3.0.3** these expected rejections no longer generate automatic bug reports.
- Drag-and-drop and open-with-default-application extractions create their temp folder on a drive
  with enough free space and skip drives that cannot be written (ACL-restricted, BitLocker-locked, or
  read-only volumes).
- Extracting entries from a ZAR archive validates every entry name and refuses unsafe names
  (`..`, separators, drive-qualified or device names), so a crafted archive cannot write outside the
  chosen destination.
- Conversion, integrity testing, and exploration all use the same libraries, so what you see
  here reflects exactly what the other views process.
