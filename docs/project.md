# FontVault — Reference Document

Source of truth for the project. Any code generation must read this document, respect existing decisions and update the relevant checklists.

# 1. GUI Choice

**WPF (.NET 10, Windows Desktop).**

Reasons:

- Included in the .NET Desktop SDK: no NuGet dependency, no additional runtime.
- Mature UI virtualization (`VirtualizingStackPanel`, container recycling) proven on very large lists.
- Fonts loadable from file without system installation (`GlyphTypeface`), vector rendering through DirectWrite.
- Single project, simple packaging (portable single-file exe).

Visual theme: the built-in **Fluent theme** (`Application.ThemeMode = System`, .NET 9+ WPF, still gated behind the WPF0001 experimental diagnostic) — Windows 11 look (rounded controls, accent color, light/dark following the system) with zero dependency. On top of it: **colored** Segoe Fluent Icons glyphs (Segoe MDL2 fallback) on buttons and tab headers, rounded card panels (`CardBackgroundFillColorDefaultBrush` via `DynamicResource`), colored letter chips per family, colored format badges per variant, a "?" legend popup on the Families header explaining the iconography, search box with placeholder, accent `AccentButtonStyle` on the primary action. App icon: generated in-repo (`Assets\FontVault.ico`, violet→blue gradient "Aa", BMP-only entries because csc rejects PNG-compressed icon entries), used as the exe icon and the window icon.

UI language: **English**. Workflow: the source folder comes first, the vault below it; the index (next to the exe) is **loaded automatically at startup** when present (no load button); the single action button is **Process** (scan → dedup → copy → index), placed on the vault row with a **Logs** button (opens `scan.log`) beside it. Status bar: font count bottom-left (`N fonts`, or `M / N fonts` while a filter is active), transient messages in the middle, progress bar, and a persistent completion message bottom-right ("✔ Process completed — …"). The Details panel does not show source paths (still recorded in the index).

Further interactions:

- **Windows install integration**: every variant shows its install state (green dot; detail line). A toggle on the Details tab installs the font **per-user** (no admin: copy to `%LocalAppData%\Microsoft\Windows\Fonts` with a `.fv<CRC32>` marker name, `HKCU\…\Fonts` registry value, `AddFontResource` + `WM_FONTCHANGE`) or uninstalls it. Detection is content-based (marker CRC for FontVault installs, size + CRC32 against the system and per-user font folders for external installs, computed in the background after index load). Uninstall is only offered for FontVault installs; system/externally-installed fonts show a disabled toggle. WOFF/WOFF2/EOT variants are installed as their rebuilt standalone sfnt.
- **Installed filter**: shows only fonts currently installed in Windows. **Sort**: families by name, glyph count (max of variants) or variant count.
- **Outbound drag & drop**: dragging a variant to Explorer copies its vault file; dragging a family copies all its variant files (standard `FileDrop`).

Rejected alternatives:

- WinUI 3: Windows App SDK dependency, heavier packaging; only notable advantage: native variable axes.
- Avalonia: external NuGet dependency, no gain on a Windows-only target.

Known limits:

- WPF does not expose variable-font axis variation (named instances only). Real-time axis rendering is therefore done through direct DirectWrite interop (raw vtable calls, no dependency): `IDWriteFactory6` → `IDWriteFontResource` → `IDWriteFontFace5` at arbitrary axis values, glyph outlines captured by a managed `IDWriteGeometrySink` into WPF geometry. Requires Windows 10 1809+; graceful message otherwise.
- WPF snapshots a folder's font list on first access; a font file added to that folder later in the same process breaks subsequent `GlyphTypeface` loads. Mitigated by the preview cache (one immutable folder per font, see section 11).

# 2. General Architecture

Single project `FontVault` (WPF executable). Organized by folders, not by DLLs:

- `Core/`: data model, index, CRC32, Unicode block table, utilities.
- `Fonts/`: in-house SFNT parsing (binary read of the useful tables), WOFF/WOFF2/EOT containers, sfnt rebuild, GSUB inspection.
- `Scan/`: scan pipeline, deduplication, selection, vault placement.
- `UI/`: window, views, ViewModels (minimal MVVM, no framework), preview / glyph-grid / variable-axis controls, DirectWrite interop.

External dependencies: none. WOFF decompression via `ZLibStream`, WOFF2 via `BrotliStream` (both in the BCL). CRC32 implemented in-house (~30 lines, standard table). DirectWrite reached through raw vtable calls (`unsafe`, slot numbers and IIDs verified against the SDK headers) — no COM wrapper library. MTX-compressed EOT decompressed through the in-box `t2embed.dll` (P/Invoke, OS component like dwrite/gdi32).

Font parsing: in-house SFNT reader, partial read of the useful tables only (`name`, `head`, `OS/2`, `maxp`, `cmap`, `fvar`, `GSUB`, `GPOS`). No unnecessary full load beyond what the global CRC32 already requires.

Packaging: **portable framework-dependent single-file exe** — `dotnet publish` (win-x64, `PublishSingleFile`) produces the single `FontVault.exe` (~0.3 MB, no .pdb); no installer, but the **.NET 10 Desktop Runtime must be installed** on the target machine. Regular builds stay RID-less (`bin\<Config>\net10.0-windows\`, 5 files, no runtime copy); the RID is applied only while publishing. `settings.txt` is stored next to the exe.

# 3. Vault Structure

```
<exe folder>/
  FontVault.exe
  settings.txt
  fontvault.idx
  scan.checkpoint        (during an interrupted scan only)
  scan.partial           (during an interrupted scan only)
  scan.log
  fonts-vault/           (default vault: ".\fonts-vault"; configurable)
    A/
      Arial/
        Arial_Regular_v7.01_g4547.ttf
```

Relative vault paths (the default `.\fonts-vault`) resolve against the exe folder, so the exe, settings, index and vault move together.

The index and all scan artifacts live **next to the exe**; the vault folder holds font files only. Older layouts (index inside the vault) are migrated automatically at startup.

- Level 1: first letter of the family name (`#` if non-alphabetic, diacritics stripped).
- Level 2: family name — the **Windows display name** (typographic family, name ID 16, when present, otherwise family ID 1), so every member of one family lands in the same folder (e.g. all "Segoe UI" weights including Black/Light/Semilight which carry distinct ID 1 values).
- File: `WindowsDisplayName_Style_vVersion_gGlyphCount.ext` where Style is the **effective style** (typographic subfamily ID 17 preferred over subfamily ID 2).
- Collision: `WindowsDisplayName_Style_vVersion_gGlyphCount_cCRC32.ext`.

`WindowsDisplayName`: extracted from internal metadata (`name` table, Windows Fonts display logic: ID 16 then ID 1, en-US language with fallbacks). Never derived from the source file name. NTFS-invalid characters replaced by `-`.

# 4. Data Model

Index entry `FontEntry` (light fields resident in memory):

| Field | Type | Residence |
|---|---|---|
| WindowsDisplayName, FamilyName, TypographicFamilyName | string (pool) | memory |
| Style, TypographicSubfamily, FullName, PostScriptName | string (pool) | memory |
| Version | string (pool) | memory |
| GlyphCount | uint16 | memory |
| Format (TrueType/CFF), Extension | enum | memory |
| FileSize | int64 | memory |
| CRC32 | uint32 | memory |
| IsVariableFont | bool | memory |
| MetadataScore | byte | memory |
| UnicodeCoverage | (start,end)[] intervals | on demand |
| VariableAxes | (tag, min, default, max)[] | on demand |
| OpenType features | tags[] (ligature presence derived) | on demand |
| OriginalPath(s) | string[] | on demand |
| VaultPath | string (reconstructible from the convention) | memory |
| ScanDate | int64 (UTC ticks) | memory |

- `EffectiveStyle` (derived): TypographicSubfamily if present, otherwise Style; used for grouping, naming and the variants list.
- String pool: deduplication of repeated family/style names.
- Heavy fields: read from `fontvault.idx` by offset (memory-mapped file), never loaded globally.

# 5. Index Format

Single binary file `fontvault.idx` next to the exe (the vault holds font files only). No SQL, no external DAT.

Structure (physical order; header offsets make the order transparent to the reader):

1. Header: magic `FVIX`, format version, entry count, vault counters (files, bytes), section offsets.
2. String section: deduplicated UTF-8 pool.
3. Heavy-block section: Unicode coverage, variable axes, feature tags, source paths; one block per entry.
4. Entry section: fixed 70-byte records (light fields + heavy-block offset).
5. Validation CRC32 over the light ranges (header + strings + entries) only.

Properties:

- Startup: sequential read of header, strings and entries only; heavy blocks memory-mapped, accessed by offset. The validation CRC excludes the heavy section so startup never touches it; heavy-block counts are sanity-capped at read time instead. Measured: 1M-entry load 1.64 s, 2M 3.34 s (was 3.15 s / 6.2 s with full-file CRC). Loading reports determinate progress (CRC bytes, then entry records, then search keys), surfaced as a progress bar in the status bar; the scan reports processed/discovered on the same bar.
- Atomic write: written to `.idx.tmp` then replaced.
- Invalidation: on load, background quick comparison (file count, aggregated sizes) between index and vault; mismatch → rescan suggested. Full rescan only on demand or detected invalidation.
- Evolution: format version change → regeneration by rescan (no migration). Current version: **3** (v2 added variable axes and feature tags; v3 restricted the CRC to the light sections).

# 6. Scan Pipeline

1. **Discovery**: recursive enumeration of sources, extension filter (otf, ttf, woff, woff2, eot), access errors logged and skipped. Sources = the user-selected folder plus, when the "+ Windows fonts" option is enabled (default, persisted), the system font folder (`C:\Windows\Fonts`) and the per-user one (`%LocalAppData%\Microsoft\Windows\Fonts`); the user folder may be left empty in that case.
2. **Minimal read**: sfnt header + table directory; only the useful tables are parsed. WOFF: per-table zlib decompression of the useful tables only. WOFF2: Brotli decompression of the single stream; the tables needed for metadata are never transformed and are used as-is. EOT: embedded sfnt located at the end of the container; XOR obfuscation handled directly; MicroType Express (MTX) compression decompressed through the in-box `t2embed.dll` (`TTLoadEmbeddedFont` private install + `GetFontData`, serialized; on name collision with an installed font, a placeholder load is used and the name table is restored from the EOT header).
3. **Metadata extraction**: all data-model fields, including fvar axes and GSUB/GPOS feature tags.
4. **Signatures**: CRC32 of the whole file (streamed during the read), FileSize.
5. **Best-version selection**: see section 8.
6. **Vault copy/placement**: copy (never destructive move), naming per convention, collision → CRC32 suffix; idempotent (existing target with same size/CRC is skipped).
7. **Index generation**: atomic write of `fontvault.idx`.

Execution constraints:

- Producer/consumer over a `Channel`; bounded parallelism (cores − 1).
- Worker threads at `BelowNormal` priority; Windows stays responsive.
- Resume: append-only journal `scan.partial` (one extracted entry per file read, length + CRC32 framed records, format-versioned header, truncated at the first corruption); `scan.checkpoint` holds the session key (sources + vault). On restart of the same session, the journal is reloaded and already-processed files are skipped; the copy phase is idempotent.
- Per-file errors: logged to `scan.log` (path, stage, error), never a global stop.

# 7. Deduplication

- **Exact duplicates**: key (FileSize, CRC32); on a match, binary comparison rules out CRC32 collisions. One copy kept; all source paths recorded in `OriginalPath(s)`.
- **Concurrent variants**: grouped by logical key (normalized typographic family + effective style); selection per section 8; losers are not copied but their source paths are recorded in the index under the winning entry.
- **Name/style/version/glyph collision**: different files producing the same vault file name → `_cCRC32` suffix.
- **Additive vault**: entries already indexed are never removed by a later scan; a new file is copied only if it beats the kept entries of its group (section 8 comparator) or matches the section 8 exception. Exact duplicates of a file already in the vault merge their source path into the existing entry.

# 8. Best-Version Selection

Deterministic comparator, criteria in order, applied within a logical key (family, style):

1. Format priority: OTF > WOFF2 > TTF > WOFF > EOT.
2. Newer version (numeric comparison extracted from the version string).
3. Higher GlyphCount.
4. Wider Unicode coverage (codepoint count).
5. Better metadata (count of populated `name` fields, typographic fields present).
6. Full tie: lowest CRC32 (determinism).

Exception to format priority: a TTF/WOFF/EOT is kept in addition if, compared to the retained OTF/WOFF2, it has a newer version, more glyphs, wider Unicode coverage, better metadata, or a detected specific interest (variable font while the retained one is not).

# 9. Search

- Data: normalized lowercase keys precomputed at index load for display/family/style/typographic-subfamily/full/PostScript names.
- Algorithm: in-memory scan over the normalized keys (substring); partitioned parallel scan above 200k entries, sequential below. Measured: 5.9 ms per query at 1M entries, 8.3 ms at 2M (parallel); no inverted index needed.
- Instant filters: combinable predicates on resident light fields (format, variable font).
- UI: input debounce, virtualized results, cancellation of stale searches.

# 10. Glyph Viewer

- Source: `UnicodeCoverage` intervals extracted from `cmap` (formats 4 and 12), loaded on demand.
- Unicode block view: static block table compiled into the application; per block, coverage ratio (present/total) shown in the block selector.
- Full Unicode view: virtualized grid of all codepoints of the selected block; present glyphs rendered, missing codepoints drawn as a distinct hollow box; hex codepoint label under each cell.
- Present-glyphs view: same grid restricted to covered codepoints (toggle).
- Rendering: `GlyphTypeface` loaded from the preview cache, bounded LRU-style cache of open typefaces, rows rendered by a custom element and virtualized with recycling (handles 40k+ codepoint blocks).

# 11. Preview

- Custom text, variable size, multiline rendering (explicit breaks + wrapping).
- Raw mode (direct `GlyphRun`, cmap-based): two sub-modes — included glyphs only (absent characters skipped) or all characters with visible holes (hollow box).
- Shaped mode (WPF text stack): ligatures and OpenType features active via `Typography.*` (liga, dlig, kern, smcp toggles), weight/style mapped from the effective style. Note: system shaping applies font fallback for absent characters.
- Variable fonts — real-time axes ("Variable axes" tab): one slider per fvar axis (min/default/max from the index), text outlines re-rendered live through DirectWrite at the exact axis values (see section 1), reset to defaults. Validated: Bahnschrift ink area +72 % from wght 300 to 700, width −26 % at wdth 75.
- Ligature / GSUB inspection ("Ligatures / GSUB" tab): GSUB feature list with resolved lookup types (Extension lookups unwrapped) and per-feature ligature counts; concrete ligature substitutions (LookupType 4) listed as component characters (reverse cmap) with the resulting ligature glyph rendered by glyph index.
- WOFF/WOFF2/EOT: not loadable directly by `GlyphTypeface`; a standalone sfnt is rebuilt — WOFF by per-table decompression, WOFF2 including the transformed `glyf`/`loca`/`hmtx` reconstruction per the W3C specification (validated against Google-encoder files), EOT by extracting the embedded sfnt. Rebuilt files are cached in a temp folder, **one immutable subfolder per font** (WPF folder-snapshot behavior, see section 1); OTF/TTF previews go through the same cache for the same reason. Consequence: vault paths (spaces, any NTFS-valid characters) never reach `GlyphTypeface` — cache paths are short, ASCII and URI-safe, so vault file naming has no impact on preview reliability. The cache checks vault-file existence (explicit "missing from the vault — run Process to repair" error) and strips read-only attributes inherited from source fonts (both at vault copy time and in the cache) so overwrites cannot fail.

# 12. Performance

Target: several million files, several hundred GB, NVMe SSD, multi-core CPU.

- **RAM**: only light fields resident (~100–150 bytes/entry excluding pool); deduplicated string pool; heavy fields by offset over a memory-mapped file.
- **I/O**: partial table reads, CRC32 computed while streaming, atomic batched index writes.
- **Allocations**: `Span<byte>`/`ArrayPool` for parsing, structs for records, no LINQ in hot paths.
- **Startup**: index load only (light sections), no scan; target < 2 s for 1M entries.
- **Search**: see section 9.
- **UI**: virtualization and recycling mandatory on every list/grid, lazy loading of previews and coverage, pagination if a view does not virtualize properly.

Measured profile (synthetic index, realistic string duplication, NVMe, 2026-06-12):

| Entries | Index size | Write | Load | Resident RAM (entries + search keys) | Search (parallel) | GetHeavy |
|---|---|---|---|---|---|---|
| 1,000,000 | 410 MB | 3.6 s | 1.64 s | 553 MB | 5.9 ms | 3.5 µs |
| 2,000,000 | 824 MB | 7.0 s | 3.34 s | 1.12 GB | 8.3 ms | 3.4 µs |

Single-file index kept: targets hold at 2M entries; index segmentation re-evaluated only beyond ~5M.

# 13. Progress

MVP, V1 and V2 implemented (.NET 10 build with zero warnings; pipeline validated on real fonts: extraction, exact deduplication, selection, copy, index, reload, interrupt/resume; WOFF/WOFF2/EOT containers validated including transformed-glyf reconstruction against real Google-encoded WOFF2 files and MTX-compressed EOT via a TTEmbedFont round-trip; Windows fonts corpus scanned without errors; real-time axis variation validated on Bahnschrift through DirectWrite; GSUB inspection validated on Calibri/Arial; index profiled at 1M and 2M entries; framework-dependent single-file publish and GUI startup verified). Post-V2 product pass also delivered: English Fluent UI with colored iconography and app icon, automatic index load, Windows per-user install/uninstall integration with installed filter, sorting modes, outbound drag & drop, progress bar and status counters (sections 1, 5, 6).

- [x] Architecture
- [x] Data model
- [x] Scanner
- [x] Deduplication
- [x] Index
- [x] Search
- [x] GUI
- [x] Preview
- [x] Glyph viewer
- [x] Import (incremental: any folder scan merges into the existing vault/index)
- [x] Export (copy of the selected variant or whole family to a chosen folder)
- [x] Tests (validation harness on real corpus and real WOFF2/WOFF/EOT containers; no permanent test project, single-project constraint)
- [x] Packaging (framework-dependent single-file exe, no installer; .NET 10 Desktop Runtime required on the machine)

# 14. Decision Log

Date | Decision | Rationale
---|---|---
2026-06-11 | WPF GUI | Zero dependencies, mature virtualization, file-based font loading without installation
2026-06-11 | In-house SFNT parsing, useful tables only | No NuGet, partial reads, full control over performance
2026-06-11 | Single binary index `fontvault.idx`, sections + offsets, memory-mapped | Fast startup, no SQL, on-demand access to heavy fields
2026-06-11 | In-house CRC32 | Avoids the System.IO.Hashing NuGet
2026-06-11 | WOFF via ZLibStream, WOFF2 via BrotliStream (BCL) | Decompression without external dependency
2026-06-11 | MicroType Express compressed EOT unsupported | Proprietary format, disproportionate cost; logged per file
2026-06-11 | Exact duplicates: (size, CRC32) + binary comparison on tie | Safety against CRC32 collisions at millions-of-files scale
2026-06-11 | Real-time variable axes deferred to V2 | Not exposed by WPF; requires DirectWrite interop
2026-06-11 | Search by in-memory scan, no inverted index | Sufficient at 1M+ entries; simplicity first
2026-06-11 | Additive vault: no automatic removal of indexed entries on scan | Data safety; index/vault consistency without orphan management
2026-06-11 | Resume via append-only scan.partial journal + session key in scan.checkpoint | Robust to interrupted writes; independent of parallel processing order
2026-06-11 | Index physical order: header, strings, heavy blocks, entries | Single-pass write without memory buffering; header offsets for the reader
2026-06-12 | Code comments and documentation in English | Project language policy
2026-06-12 | Portable single-file exe, no installer; settings.txt next to the exe | Portability requirement
2026-06-12 | Family folder = typographic family (display name); effective style = ID 17 over ID 2 | Same-family fonts grouped in one folder (Windows Fonts behavior)
2026-06-12 | Index format v2: variable axes + OpenType feature tags persisted | V1 metadata; no migration, regeneration by rescan
2026-06-12 | WOFF2 transformed glyf/loca/hmtx reconstruction implemented per W3C spec | Required for WOFF2 preview; validated byte-consistent on real encoder output
2026-06-12 | Preview cache: one immutable folder per font | WPF snapshots folder font lists on first access; added files break later loads
2026-06-12 | Shaped preview via WPF text stack, raw GlyphRun preview for hole modes | Ligatures/features need shaping; hole visibility needs raw cmap mapping
2026-06-12 | Real-time axes via raw-vtable DirectWrite interop (IDWriteFactory6 → IDWriteFontResource → IDWriteFontFace5), outlines captured by a managed IDWriteGeometrySink | No dependency; vtable slots verified against SDK headers; note: mingw's IID for IDWriteFactory6 is wrong, official is F3744D80-21F7-42EB-B35D-995BC72FC223
2026-06-12 | Index format v3: validation CRC restricted to light sections, heavy blocks bounds-guarded at read | 1M-entry startup 3.15 s → 1.64 s measured; corruption in heavy blocks still contained
2026-06-12 | Search scan parallelized above 200k entries | 5.9 ms at 1M, 8.3 ms at 2M measured
2026-06-12 | Single-file index kept, no segmentation below ~5M entries | Profiled at 2M (load 3.34 s, RAM 1.12 GB including search keys); complexity not justified at validated scale
2026-06-12 | GSUB inspection limited to feature list + LookupType 4 ligatures (Extension resolved) | Covers the dedicated ligature view; contextual lookup contents out of scope
2026-06-12 | Determinate progress bar for index load (CRC bytes → entries → search keys) and scan (processed/discovered) | UX on large vaults
2026-06-12 | Release publish ships the single self-contained exe only (runtime embedded, no .pdb) | Portability requirement clarified: must run without any .NET installation
2026-06-12 | Scan optionally includes the Windows font folders (system + per-user), enabled by default | Import of installed fonts requested; additive vault and dedup make repeats safe
2026-06-12 | Built-in Fluent theme (ThemeMode=System, WPF0001 suppressed); first family auto-selected after filtering | Modern look requested; zero dependency; verified by screenshot in light of system dark mode
2026-06-12 | FontEntry members converted from public fields to properties | WPF bindings ignore fields silently (variant list showed empty version/glyph values)
2026-06-12 | Output paths without RID subfolder (build bin\Config\net10.0-windows\, publish …\publish\); FontVault.sln added | Requested layout; solution generated by dotnet CLI with full configuration mappings
2026-06-12 | Publish switched to framework-dependent single exe (~0.3 MB); .NET 10 Desktop Runtime required on the machine; RID applied only while publishing (`_IsPublishing`) | Clarified requirement: no runtime copy in the output (self-contained put ~300 runtime files in bin); the WPF markup compiler also mishandles RID-qualified intermediate paths (BG1002) — supersedes the self-contained decision
2026-06-12 | UI switched to English; vault defaults to the exe folder with automatic index load (load button removed); scan action renamed "Process"; source field above vault | Requested workflow simplification
2026-06-12 | Visual layer: Segoe Fluent Icons, card panels, per-family colored letter chips, per-format colored badges, search placeholder | "Hyper modern" look requested; no dependency (system icon font + Fluent resource brushes via DynamicResource)
2026-06-12 | Index and scan artifacts (idx, checkpoint, partial, log) moved next to the exe; vault = font folders only; default vault = "Vault" subfolder; silent migration from the older layout | Requested; keeps the vault a pure font store
2026-06-12 | Toolbar rework: Process on the vault row, Logs button (shell-opens scan.log), persistent completion message bottom-right, font count bottom-left with filtered ratio, legend popup on Families, colored icons, generated app icon | Requested UX/visual pass
2026-06-12 | Details panel no longer shows source paths (still stored in the index) | Requested; irrelevant once a font is vaulted
2026-06-12 | Preview cache hardening: vault-file existence check with actionable message; read-only attributes stripped at vault copy and in the preview cache | "Preview unavailable" reports traced to missing vault files / read-only sources, not to file naming (previews never load from vault paths)
2026-06-13 | Default vault changed to the relative ".\fonts-vault", resolved against the exe folder | Requested; portable layout moves as one unit
2026-06-13 | MTX-compressed EOT supported via t2embed.dll (TTLoadEmbeddedFont + GetFontData, serialized; placeholder load + name-table restore on name collision) | Supersedes the 2026-06-11 "MTX unsupported" decision; no external dependency (OS component); validated by a TTEmbedFont round-trip on real compressed data
2026-06-13 | Per-user Windows install/uninstall integration; content-based detection (.fv-CRC marker / size+CRC); uninstall restricted to FontVault installs | Requested; no admin rights; reversible and identifiable installs; validated by a full install/uninstall round-trip (file + registry)
2026-06-13 | Installed filter, family sorting (name / glyph count / variant count), outbound drag & drop (FileDrop: variant = 1 file, family = all variants) | Requested UX features
2026-06-13 | Product renamed FontVault (was FontsVault): namespaces, project/solution/icon files, exe, window title, index file fontvault.idx (silent migration from the old name), preview-cache folder, registry marker | Requested rename; vault folder name ".\fonts-vault" and ".fv" markers intentionally unchanged

# 15. Roadmap MVP / V1 / V2

**MVP — delivered**

- Data model, CRC32, string pool.
- OTF/TTF parsing (name, head, OS/2, maxp, cmap tables).
- Full scan pipeline (discovery → index), checkpoint, logging.
- Exact deduplication and best-version selection.
- Binary index, startup load, invalidation.
- GUI: virtualized family/variant lists, search, filters, file details.
- Free-text preview OTF/TTF (included-only / visible-holes modes).

**V1 — delivered**

- WOFF, WOFF2, EOT (non-MTX) container parsing; sfnt reconstruction for preview, including WOFF2 transformed glyf/loca/hmtx.
- Glyph viewer: Unicode blocks with coverage ratios, full view with missing glyphs, present-only view.
- Ligatures and OpenType features in preview (shaped mode); variable axes detected and displayed (named instances).
- Incremental import (additive scan into the existing vault), export (copy of selections).
- Tests on real corpus and real containers, resume/error hardening.
- Portable single-file packaging.

**V2 — delivered**

- Real-time variable axes: "Variable axes" tab, one slider per fvar axis, live outline rendering through DirectWrite interop at exact axis values.
- Dedicated ligature view and GSUB feature inspection: feature list with resolved lookup types, concrete LookupType 4 substitutions with rendered ligature glyphs.
- Large-scale work: index format v3 (light-section CRC, 2× faster startup), parallel search above 200k entries, RAM/I/O profile measured at 1M and 2M entries (see section 12); single-file index confirmed sufficient below ~5M entries.

**Beyond V2 (not planned)**

- Segmented index if vaults beyond ~5M entries materialize.
- Contextual GSUB lookup contents (types 5/6) in the inspection view.
