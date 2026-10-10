# XISO Studio — Documentation

| Getting Started | Using the App | Technical Reference | Project |
|---|---|---|---|
| **Home** | [Usage Guide](Usage-Guide.md) | [Architecture](Architecture.md) | [Repository](Repository.md) |
| [Installation](Installation.md) | [Conversion Methods](Conversion-Methods.md) | [XDVDFS Technical Docs](XDVDFS-Technical-Documentation.md) | [Building from Source](Building-from-Source.md) |
| | [XISO Explorer](XISO-Explorer.md) | [Troubleshooting & FAQ](Troubleshooting-and-FAQ.md) | [Release Notes](Release-Notes.md) |

---

Welcome to the official documentation for **XISO Studio** — a high-performance, cross-platform (Avalonia) utility built for the Xbox preservation and emulation community. Convert, verify, and explore Xbox and Xbox 360 images (ISO, CSO, ZAR, and CHD), powered by the XISOSharp and CHDSharp libraries.

- **Repository:** <https://github.com/purelogiccode/XISOStudio>
- **Website:** <https://www.purelogiccode.com>
- **License:** GNU General Public License v3.0
- **Current version:** 3.0.3 (October 2026) — see the [Release Notes](Release-Notes.md#303)
- **Platform:** Windows, Linux, and macOS (x64 / ARM64), .NET 10.0 Runtime

---

## What This Tool Does

**XISO Studio** streamlines the process of converting standard Xbox and Xbox 360 disc images (Redump ISOs) into the optimized, trimmed **XISO** format used by original Xbox consoles, emulators, and FTP transfer tools.

The application combines:

1. The **[XISOSharp](https://github.com/purelogiccode/XISOSharp)** conversion engine, which repacks the XDVDFS game partition into an optimized, tightly packed XISO, and **[CHDSharp](https://github.com/purelogiccode/CHDSharp)**, which compresses that partition into a CHD image.
2. An **integrity tester** that deeply validates the XDVDFS filesystem structure (ISO/CSO, and the Xbox filesystem inside CHD images) or the ZAR archive tree, with an optional deep scan for physical media or block errors.
3. A built-in **Image Explorer** that browses the contents of an Xbox ISO, CSO, ZAR, or CHD without extracting it.

Whether you are managing a large collection of game backups, preparing files for an Xbox hard drive, or verifying the integrity of your dumps, the application provides a user-friendly interface with powerful batch processing capabilities.

---

## Feature Highlights

| Area | Capabilities |
|---|---|
| **Batch Conversion** | XISOSharp and CHDSharp conversion engines, XISO/ZAR/CSO/CHD output formats, ISO input (Redump full-disc images or optimized XISO files), archive input (`.zip`, `.7z`, `.rar`), optional `$SystemUpdate` removal, replace-originals mode, post-conversion verification |
| **Integrity Testing** | Structural XDVDFS validation for ISO/CSO and the Xbox filesystem inside CHD images, ZAR archive validation, optional deep scan, automatic move of passed/failed files into organized subfolders |
| **Image Explorer** | Native browsing inside ISO/CSO/ZAR/CHD images, file metadata, double-click to open, drag-and-drop extraction |
| **Monitoring** | Real-time success/fail/skip counters, per-drive read/write speed indicators (Windows), elapsed-time tracking, memory usage |
| **Reliability** | Atomic replace-originals workflow (archives are deleted only when every entry was converted), per-file error isolation, age-checked temp-folder cleanup, fallback temp drives, network path (UNC) support with retry logic, cloud-aware (OneDrive) retries, encrypted-archive detection, lease-guarded explorer and close handling |
| **Support** | In-app bug reporting and automatic update checks |

---

## Screenshots

| | |
|---|---|
| ![Convert Tab](https://raw.githubusercontent.com/purelogiccode/XISOStudio/master/screenshot.png) | ![Test Tab](https://raw.githubusercontent.com/purelogiccode/XISOStudio/master/screenshot2.png) |
| *Batch conversion with real-time monitoring* | *Integrity testing with batch organization* |
| ![Explorer Tab](https://raw.githubusercontent.com/purelogiccode/XISOStudio/master/screenshot3.png) | |
| *XISO file browser* | |

---

## Documentation Map

| Page | Contents |
|---|---|
| [Installation](Installation.md) | System requirements, download, first run, upgrading |
| [Usage Guide](Usage-Guide.md) | Complete walkthrough of every tab, option, and indicator |
| [Conversion Methods](Conversion-Methods.md) | How the XISOSharp conversion engine works |
| [XISO Explorer](XISO-Explorer.md) | Browsing, opening, and extracting files from inside an ISO, CSO, ZAR, or CHD |
| [Troubleshooting & FAQ](Troubleshooting-and-FAQ.md) | Common errors, known issues, frequently asked questions |
| [Architecture](Architecture.md) | Codebase design, services, dependency injection, data flow |
| [XDVDFS Technical Docs](XDVDFS-Technical-Documentation.md) | Binary format internals and the XISO conversion algorithm |
| [Building from Source](Building-from-Source.md) | Prerequisites, build, and test instructions |
| [Repository](Repository.md) | Repository layout, releases, contributing, and license |
| [Release Notes](Release-Notes.md) | Version history and detailed release notes |
| [What's New](https://github.com/purelogiccode/XISOStudio/blob/master/WhatsNew.md) | Highlights of the latest release |

---

## Quick Start

1. **Download** the release archive for your platform from the [Releases](https://github.com/purelogiccode/XISOStudio/releases) page and extract it — the application is fully portable, no installer required.
2. **Launch** `XISOStudio.exe` on Windows or `./XISOStudio` on Linux/macOS (requires the [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)).
3. On the **Convert** tab, select an **input folder** containing your ISO files and an **output folder** for the converted XISOs.
4. Optionally enable **Skip $SystemUpdate** and **Check output integrity**, then click **Start Conversion** and monitor progress in real time.

---

## Acknowledgements

- **[Avalonia](https://avaloniaui.net/)** — cross-platform .NET UI framework the application is built on
- **[XISOSharp](https://github.com/purelogiccode/XISOSharp)** — XISO/XDVDFS reading, writing, and conversion library that powers XISO/ZAR/CSO conversion, integrity testing, and exploration
- **[ZArchiveSharp](https://github.com/purelogiccode/ZArchiveSharp)** — ZArchive/zstd compression library used for ZAR output and reading
- **[CHDSharp](https://github.com/purelogiccode/CHDSharp)** — CHD (Compressed Hunks of Data) reading, verification, and creation for CHD output and Xbox CHD testing/exploration
- **[SharpCompress](https://github.com/adamhathcock/sharpcompress)** — high-performance archive extraction

---

*If you find this tool useful, please give the repository a star on GitHub!*

---

*This documentation is published automatically to the [GitHub wiki](https://github.com/purelogiccode/XISOStudio/wiki) and [GitHub Pages](https://purelogiccode.github.io/XISOStudio/) on every change. Both destinations show the same side menu: the wiki renders [`_Sidebar.md`](https://github.com/purelogiccode/XISOStudio/blob/master/docs/_Sidebar.md), and the Pages site renders [`_layouts/default.html`](https://github.com/purelogiccode/XISOStudio/blob/master/docs/_layouts/default.html).*
