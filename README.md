<div align="center">

<img src="Assets/FontVault.png" alt="FontVault" width="128" />

# FontVault

**A fast, single-file Windows desktop app to scan, deduplicate, organize, preview and install your font collection — built to handle millions of files.**

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)](#requirements)
[![.NET](https://img.shields.io/badge/.NET%2010-WPF-512BD4)](#requirements)
[![Build](https://img.shields.io/badge/build-single--file%20exe-success)](#build)

</div>

---

## Overview

FontVault scans one or more folders (and, optionally, your installed Windows fonts), reads each font's internal metadata, removes duplicates, keeps the best version of each variant, and copies everything into a clean, predictable **vault** organized by family. It then gives you a virtualized browser with search, a Unicode glyph viewer, live text preview (including real-time variable-font axes), OpenType/ligature inspection, and per-user install/uninstall — all from a single portable executable with no installer.

It is designed for **very large collections**: several million files and hundreds of GB, with a memory-mapped binary index, partial table reads, and parallel in-memory search.

## Features

- **Scan & import** — recursive scan of any folders, plus optional inclusion of the system (`C:\Windows\Fonts`) and per-user font folders.
- **Archive support** — extracts fonts directly from `.zip` (native), `.rar` and `.7z` archives during the scan.
- **Formats** — OpenType/TrueType `.otf` / `.ttf`, web fonts `.woff` / `.woff2`, and `.eot` (including MicroType Express / MTX-compressed).
- **Deduplication** — exact duplicates removed by size + CRC32 with binary tie-break; concurrent variants grouped and the best one selected by a deterministic comparator (format, version, glyph count, Unicode coverage, metadata quality).
- **Clean vault** — files renamed from internal metadata, not source file names: `Family/Style/Version/GlyphCount`, grouped by typographic family and first letter. Copy only, never a destructive move. Additive: existing entries are never deleted by a later scan.
- **Binary index** — single memory-mapped `fontvault.idx`; only light fields stay resident, heavy fields are read on demand by offset. Loads ~1M entries in ~1.6 s.
- **Search** — instant substring search across display/family/style/full/PostScript names, with format and variable-font filters; parallel above 200k entries.
- **Glyph viewer** — Unicode-block view with per-block coverage ratios, full grid with missing glyphs marked, and a present-only mode.
- **Preview** — custom multiline text at any size; raw (cmap-based) and shaped (ligatures, kerning, OpenType features) modes.
- **Real-time variable fonts** — one live slider per `fvar` axis, outlines re-rendered through direct DirectWrite interop at the exact axis values.
- **Ligatures / GSUB inspection** — feature list with resolved lookup types and concrete ligature substitutions rendered by glyph index.
- **Windows install integration** — install/uninstall a variant or family **per-user** (no admin), with content-based detection of already-installed fonts and an "installed only" filter.
- **Drag & drop out** — drag a variant (one file) or a family (all variants) straight to Explorer.
- **Resumable** — interrupted scans resume from an append-only journal; per-file errors are logged, never a global stop.
- **Modern UI** — built-in WPF Fluent theme (Windows 11 look, light/dark following the system), colored iconography, English.

## Screenshots

> _Add screenshots here (main window, glyph viewer, variable-axis preview)._

## Requirements

- **Windows 10 1809+ or Windows 11** (x64). Real-time variable-axis rendering requires Windows 10 1809+; a graceful message is shown otherwise.
- **.NET 10 Desktop Runtime** installed on the machine (the published exe is framework-dependent, ~0.3 MB).

To build from source you also need the **.NET 10 SDK**.

## Build

```sh
# regular build (RID-less, output in bin\<Config>\net10.0-windows\)
dotnet build -c Release

# portable single-file exe (framework-dependent, win-x64)
dotnet publish -c Release -p:_IsPublishing=true
# → bin\Release\net10.0-windows\publish\FontVault.exe
```

The published `FontVault.exe` is portable: the exe, `settings.txt`, the index and the vault all resolve relative to the exe folder and move together. No installer.

## Usage

1. Launch `FontVault.exe`. If an index sits next to it, it loads automatically at startup.
2. Set the **source** folder (and/or keep the **+ Windows fonts** option enabled) and the **vault** folder (default `.\fonts-vault`).
3. Click **Process** — scan → deduplicate → copy → index. Progress and counts show in the status bar; **Logs** opens `scan.log`.
4. Browse, search, preview, inspect glyphs and ligatures, toggle variable axes, install/uninstall fonts, or drag files out to Explorer.

### Vault layout

```
<exe folder>/
  FontVault.exe
  settings.txt
  fontvault.idx
  scan.log
  fonts-vault/
    A/
      Arial/
        Arial_Regular_v7.01_g4547.ttf
```

## Architecture

Single WPF project, organized by folders (no extra DLLs):

- `Core/` — data model, binary index, CRC32, Unicode block table.
- `Fonts/` — in-house SFNT parsing (useful tables only), WOFF/WOFF2/EOT containers, sfnt rebuild, GSUB inspection.
- `Scan/` — scan pipeline, archive extraction, deduplication, best-version selection, vault placement.
- `UI/` — window, minimal MVVM viewmodels, preview / glyph-grid / variable-axis controls, DirectWrite interop.

Font parsing, the index format, CRC32 and the DirectWrite interop are implemented in-house with no wrapper libraries; WOFF/WOFF2 decompression uses the BCL (`ZLibStream` / `BrotliStream`) and MTX-compressed EOT uses the in-box `t2embed.dll`. The only NuGet dependency is **SharpCompress** (for `.rar` / `.7z` extraction). See [`docs/project.md`](docs/project.md) for the full design reference and decision log.

## Performance

Measured on a synthetic index (realistic string duplication, NVMe):

| Entries   | Index size | Write | Load   | Resident RAM | Search (parallel) |
|-----------|-----------:|------:|-------:|-------------:|------------------:|
| 1,000,000 | 410 MB     | 3.6 s | 1.64 s | 553 MB       | 5.9 ms            |
| 2,000,000 | 824 MB     | 7.0 s | 3.34 s | 1.12 GB      | 8.3 ms            |

## Support / Donations

FontVault is developed in spare time. If it saves you some, you can support its development:

> _Replace the placeholders below with your own funding links before publishing._

- ☕ **Buy Me a Coffee** — `https://buymeacoffee.com/<your-handle>`
- 💜 **GitHub Sponsors** — `https://github.com/sponsors/<your-handle>`
- 🅿️ **PayPal** — `https://paypal.me/<your-handle>`
- ⭐ **Star the repo** — the simplest way to help.

(GitHub also renders a **Sponsor** button if you add a `.github/FUNDING.yml` file.)

## License

No license file is currently present in the repository. Until one is added, all rights are reserved by the author. Add a `LICENSE` file (e.g. MIT) to allow reuse.

---

<div align="center">
Made by <a href="https://github.com/Metasharp">Metasharp</a>
</div>