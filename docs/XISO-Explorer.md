# XISO Explorer

| Getting Started | Using the App | Technical Reference | Project |
|---|---|---|---|
| [Home](index.md) | [Usage Guide](Usage-Guide.md) | [Architecture](Architecture.md) | [Repository](Repository.md) |
| [Installation](Installation.md) | [Conversion Methods](Conversion-Methods.md) | [XDVDFS Technical Docs](XDVDFS-Technical-Documentation.md) | [Building from Source](Building-from-Source.md) |
| | [**XISO Explorer**](XISO-Explorer.md) | [Troubleshooting & FAQ](Troubleshooting-and-FAQ.md) | [Release Notes](Release-Notes.md) |

---

The **Explorer** tab provides a native file browser for Xbox and Xbox 360 images and archives. It reads the XDVDFS filesystem directly from ISO/CISO/CCI and GOD/SVOD images through the [XISOSharp](https://github.com/purelogiccode/XISOSharp) 1.8 `XisoImage`/`XisoImageSource` facade (StfsSharp-backed for GOD and CCISharp-backed for CCI), the CHD image through [CHDSharp](https://github.com/purelogiccode/CHDSharp) (decompressed on demand), and the ZAR archive tree through ZArchiveSharp, so you can inspect images without extracting anything. The file picker sits at the top with the explorer list below it, and the right-hand details panel shows title metadata and XBE image previews.

## Opening an Image

1. Switch to the **Explorer** tab.
2. Click **Browse...** and select a `.iso`, `.cso`, `.cci`, `.zar`, `.chd` or GOD/SVOD header file, or **Folder...** for a GOD/SVOD `.data` directory or its parent folder.
3. The root of the image appears in the file list and the details panel fills in.

Supported inputs:

- **`.iso`** — standard XISO and Redump ISOs; the explorer automatically locates the game partition.
- **`.cso`** — CISO containers (single file or a split `.1.cso` part set); the game partition is read through the decompressed view.
- **`.cci`** — CCI/CCIM containers (single file or a split part set, including multi-digit parts); the game partition is read through the CCISharp decompressed view.
- **`.zar`** — ZAR archives (ZArchive/zstd); the game tree is read directly from the compressed container. A ZAR tree has no disc geometry, so the title panel reports metadata as unavailable and the preview is disabled.
- **`.chd`** — CHD v5 images; the Xbox filesystem is read from the on-demand decompressed view. Parsing is limited to Xbox DVD images — CD/GD-ROM/hard-disk CHDs are rejected with a clear error.
- **GOD/SVOD** — Xbox 360 Games-on-Demand containers (a content-hash header beside a `<header>.data` directory). The fragment mapping is handled by `XisoImageSource`/StfsSharp, and the XDVDFS game partition is browsed without extraction. Plain STFS/XBLA packages are not disc images and are rejected.

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

## Title Metadata Panel

The details panel on the right shows:

- **Image details** — detected format (`Iso`, `Ciso`, `Cci`, `God`, `Chd`, `Zar`), logical/decompressed size, executable path, and kind.
- **Title metadata** — title name, title ID, region, disc number/count, version, publisher, developer, genre and description.

Original-Xbox values come from the XBE certificate. Xbox 360 values come from the XEX XDBF/XSRC resources and, for GOD containers, the XContent header (title name, title ID and disc number/count). If an image has no root `default.xbe`/`default.xex` (for example a ZAR tree or a data disc), the panel explains why fields are unavailable instead of failing.

## XBE Image Preview

Enter the image-internal path of an XBE (defaults to the root executable), choose **Logo**, **Title** or **Save**, and click **Load preview**:

- **Logo** — the Microsoft logo bitmap referenced by the XBE header.
- **Title** — the `$$XTIMAGE` section.
- **Save** — the `$$XSIMAGE` section.

The preview decodes to PNG in-process through `XbeAssets`; no CLI or external tool is used. XEX executables and ZAR trees do not carry XBE image sections, and a missing or undecodable asset produces a clean status message.

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
  read-only volumes). Since **3.1.2** the free-space check uses the filesystem that actually hosts the
  system temp folder (on Linux `/tmp` is often a separate tmpfs), and a full temp filesystem falls
  back to a writable per-user data folder. Failed or canceled extractions remove their partial output.
- Since **3.1.2** a drag gesture survives a DataGrid row being rebuilt between the press and the drag
  threshold, and stale drag state is discarded when the button is released or pointer capture is lost.
- An image that another program keeps open (sync client, antivirus, another application) can still be
  browsed: the explorer opens images with read/write sharing, including split `.1.cso` part sets.
  If the file is held exclusively, the explorer explains that the image is in use — since **3.1.0**
  this expected condition no longer generates an automatic bug report.
- Files with no associated application (Xbox `.xex` executables, `*.pak.xen` assets) cannot be launched
  by the operating system; since **3.1.0** the extracted copy is revealed in your file manager with an
  explanatory message instead of generating an automatic bug report.
- Extracting entries from a ZAR archive validates every entry name and refuses unsafe names
  (`..`, separators, drive-qualified or device names), so a crafted archive cannot write outside the
  chosen destination.
- Conversion, integrity testing, and exploration all use the same libraries, so what you see
  here reflects exactly what the other views process.
