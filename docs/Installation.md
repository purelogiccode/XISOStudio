# Installation

| Getting Started | Using the App | Technical Reference | Project |
|---|---|---|---|
| [**Home**](index.md) | [Usage Guide](Usage-Guide.md) | [Architecture](Architecture.md) | [Repository](Repository.md) |
| [**Installation**](Installation.md) | [Conversion Methods](Conversion-Methods.md) | [XDVDFS Technical Docs](XDVDFS-Technical-Documentation.md) | [Building from Source](Building-from-Source.md) |
| | [XISO Explorer](XISO-Explorer.md) | [Troubleshooting & FAQ](Troubleshooting-and-FAQ.md) | [Release Notes](Release-Notes.md) |

---

## System Requirements

| Component | Minimum Requirement |
|:---|:---|
| Operating System | Windows 10 (1809)+ / Windows 11, modern Linux, or macOS 12+ |
| Runtime | [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Architecture | x64 (64-bit) or ARM64 |
| RAM | 4 GB recommended |
| Storage | Varies based on ISO collection size |

### Architecture Notes

| Platform | Architectures |
|:---|:---|
| **Windows** | x64 and ARM64 — fully supported |
| **Linux** | x64 and ARM64 — fully supported |
| **macOS** | Intel (x64) and Apple Silicon (ARM64) — fully supported |

---

## Installing the Application

The application is **fully portable** — there is no installer and no registry footprint.

1. Download the release archive for your platform from the [Releases](https://github.com/purelogiccode/XISOStudio/releases) page. Bundles are named `release_<version>_<rid>.zip` (for example `release_3.1.2_win-x64.zip`) for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.
2. Extract the archive to any folder on a local drive (for example `C:\Tools\XISOStudio` or `~/Tools/XISOStudio`).
3. Run the application — `XISOStudio.exe` on Windows, or `./XISOStudio` on Linux/macOS (run `chmod +x XISOStudio` once if needed).

> **Tip:** Avoid placing the application inside `C:\Program Files`, `/usr`, or `/Applications`. Writing to protected locations requires elevation and can prevent the application from replacing originals, moving files, or writing its logs. See [Troubleshooting — Access Denied](Troubleshooting-and-FAQ.md#access-to-the-path-is-denied).

---

## Deployed Files

After extraction, the application folder contains:

| File | Purpose |
|:---|:---|
| `XISOStudio` / `XISOStudio.exe` | The main application — a single framework-dependent executable with all conversion engines and the Avalonia/Skia rendering libraries bundled (the native libraries are extracted to a temporary folder on first run) |
| `7za.exe` / `7za_arm64.exe` | Windows builds only: bundled 7-Zip CLI fallback used when SharpCompress cannot extract an archive (complex/unsupported `.7z` or `.zip` compression methods). A system 7-Zip installation is auto-detected as well |
| `7zz_linux_x64` / `7zz_linux_arm64` | Linux builds only: bundled 7-Zip CLI fallback (static build, official 7-Zip release). A system `7z` on `PATH` is used as a further fallback |
| `7zz_osx` | macOS builds only: bundled 7-Zip CLI fallback (official universal arm64/x86-64 build). A system `7z` from Homebrew on `PATH` is used as a further fallback |
| `7-Zip-License.txt` | License of the bundled 7-Zip binaries (LGPL + unRAR restriction) |
| `ReadMe.md`, `WhatsNew.md`, `LICENSE.txt` | Documentation and license, included in every release archive |

Do not delete or rename the bundled helper files — the corresponding features will fail if they are missing. On Linux/macOS the bundled 7-Zip helper must be executable (`chmod +x 7zz_*` if your unzip tool did not restore the permission). On macOS, Gatekeeper may quarantine the unsigned helper; if archive extraction reports that 7-Zip is missing or cannot run, clear the quarantine flag with `xattr -d com.apple.quarantine 7zz_osx`.

---

## .NET Runtime

The application targets **.NET 10.0**. If it fails to start with a message about a missing runtime:

1. Download and install the **.NET 10.0 Runtime** for your platform and architecture from <https://dotnet.microsoft.com/download/dotnet/10.0>.
2. Re-launch the application.

Alternatively, some releases may be published as self-contained builds that bundle the runtime — check the release notes on the [Releases](https://github.com/purelogiccode/XISOStudio/releases) page.

---

## First Run

On first start the application:

1. **Cleans orphaned temporary folders** left behind by previous sessions or crashes (see [Safety & Reliability](#safety-and-reliability) below).
2. Sends an **anonymous usage statistic** ping and, when enabled by the user, checks for a **newer version** on GitHub. If a new release exists, the application offers to open the download page. This can be declined; no personal data is collected.
3. Opens the main window with the **Convert** tab active.

---

## Upgrading

1. Close any running instance of the application.
2. Download the new release archive for your platform.
3. Replace the contents of your existing application folder with the extracted files (or extract to a fresh folder).
4. Your settings are not stored in the application folder, so replacing the folder is always safe.

---

## Uninstalling

Simply delete the application folder. The application is portable and does not write to the registry or system folders.

---

## Safety and Reliability

The installation is designed to be safe for long batch jobs:

- **Atomic operations** — converted files are verified before originals are deleted (when *Replace Originals* is enabled).
- **Automatic cleanup** — orphaned temporary files from interrupted jobs are removed at startup.
- **Fallback temp drives** — when the system temp drive lacks space for archive extraction, alternative local drives are used automatically.
- **Network resilience** — UNC paths and mapped drives are supported on Windows with automatic retry logic for transient failures.
- **Cloud-aware** — files stored in OneDrive or similar sync folders are handled with exponential-backoff retries while they hydrate.

See the [Usage Guide](Usage-Guide.md) and [Troubleshooting & FAQ](Troubleshooting-and-FAQ.md) for details.
