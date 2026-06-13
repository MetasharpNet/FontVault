using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FontVault.Core;
using FontVault.Fonts;
using FontVault.Scan;

namespace FontVault.UI;

public sealed class FamilyGroup : ObservableObject
{
    private static readonly Brush[] ChipPalette = CreatePalette(
        "#E11D48", "#D97706", "#65A30D", "#0D9488", "#0284C7", "#7C3AED", "#DB2777", "#475569");

    private static Brush[] CreatePalette(params string[] hex)
    {
        var brushes = new Brush[hex.Length];
        for (int i = 0; i < hex.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex[i]));
            brush.Freeze();
            brushes[i] = brush;
        }
        return brushes;
    }

    public FamilyGroup(string name) => Name = name;
    public string Name { get; }
    public List<FontEntry> Entries { get; } = new();
    public int Count => Entries.Count;

    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }
    public int MaxGlyphs
    {
        get
        {
            int max = 0;
            foreach (var e in Entries) if (e.GlyphCount > max) max = e.GlyphCount;
            return max;
        }
    }
    public string Letter => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "#";
    public Brush ChipBrush => ChipPalette[(Name.Length > 0 ? char.ToUpperInvariant(Name[0]) : '#') % ChipPalette.Length];
}

public sealed class DetailItem
{
    public DetailItem(string label, string value) { Label = label; Value = value; }
    public string Label { get; }
    public string Value { get; }
}

public sealed class BlockCoverage
{
    public BlockCoverage(UnicodeBlock block, int present) { Block = block; Present = present; }
    public UnicodeBlock Block { get; }
    public int Present { get; }
    public string Display => $"{Block.Name} — U+{Block.Start:X4}..U+{Block.End:X4} ({Present}/{Block.Size})";
}

/// <summary>One slider of the real-time variable-axis panel.</summary>
public sealed class AxisSliderVM : ObservableObject
{
    private readonly Action _changed;
    private double _value;

    public AxisSliderVM(VariableAxis axis, Action changed)
    {
        Axis = axis;
        _value = axis.Default;
        _changed = changed;
    }

    public VariableAxis Axis { get; }
    public string Tag => Axis.Tag;
    public double Min => Axis.Min;
    public double Max => Axis.Max;
    public string RangeText => $"{Axis.Min:0.##} – {Axis.Max:0.##} (default {Axis.Default:0.##})";

    public double Value
    {
        get => _value;
        set { if (Set(ref _value, value)) _changed(); }
    }

    public void Reset() => Value = Axis.Default;
}

public sealed class LigatureRow
{
    public string Components { get; init; } = "";
    public int GlyphIndex { get; init; }
    public string? FontPath { get; init; }
}

public sealed class MainViewModel : ObservableObject
{
    // Portable app: settings and all working files (index, scan artifacts) live next to the exe,
    // no installer, no registry. The vault folder holds font files only.
    private static readonly string ExeDir =
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

    private static readonly string SettingsPath = Path.Combine(ExeDir, "settings.txt");
    private static readonly string FavoritesPath = Path.Combine(ExeDir, "favorites.txt");
    private static readonly string IndexFilePath = Path.Combine(ExeDir, IndexFormat.FileName);

    private readonly HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);

    private IndexReader? _reader;
    private FontEntry[] _entries = Array.Empty<FontEntry>();
    private string[] _searchKeys = Array.Empty<string>();
    private List<UnicodeInterval> _selectedCoverage = new();
    private List<VariableAxis> _selectedAxes = new();
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _scanCts;
    private readonly DispatcherTimer _searchTimer;
    private int _previewRequest;
    private int _glyphRequest;
    private int _gsubRequest;

    public MainViewModel()
    {
        ScanCommand = new RelayCommand(() => _ = RunScanAsync(), () => !IsScanning);
        CancelScanCommand = new RelayCommand(() => _scanCts?.Cancel(), () => IsScanning);
        BrowseVaultCommand = new RelayCommand(() => Browse(p => VaultPath = p));
        BrowseSourceCommand = new RelayCommand(() => Browse(p => SourcePath = p));
        BrowseErrorCommand = new RelayCommand(() => Browse(p => ErrorPath = p));
        ExportEntryCommand = new RelayCommand(() => ExportEntries(SelectedEntry == null ? null : new[] { SelectedEntry }));
        ExportFamilyCommand = new RelayCommand(() => ExportEntries(SelectedFamily?.Entries));
        ResetAxesCommand = new RelayCommand(() => { foreach (var s in AxisSliders) s.Reset(); });
        OpenLogsCommand = new RelayCommand(OpenLogs);

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };

        LoadSettings();
        LoadFavorites();
        // Default folders: relative paths resolve against the exe folder, so the exe + settings +
        // source + vault move together. The index always lives next to the exe and loads automatically.
        if (_vaultPath.Length == 0)
            _vaultPath = @".\FontVault";
        if (_sourcePath.Length == 0)
        {
            _sourcePath = @".\FontSource";
            try { Directory.CreateDirectory(ResolvedSourcePath); } catch { /* best-effort */ }
        }
        if (_errorPath.Length == 0)
            _errorPath = @".\FontErrors";
        MigrateWorkFiles();
        ClearLog(); // fresh log each app session
        if (File.Exists(IndexFilePath))
            _ = LoadIndexAsync();
        else
            StatusText = "No index yet — pick a source folder and click Process.";
    }

    /// <summary>
    /// One-time migrations: the pre-rename index file name ("fonts" era), and older versions
    /// that stored the index and scan artifacts inside the vault.
    /// </summary>
    private void MigrateWorkFiles()
    {
        try
        {
            string legacyIndex = Path.Combine(ExeDir, "fonts" + "vault.idx"); // split literal: survives bulk renames
            if (!File.Exists(IndexFilePath) && File.Exists(legacyIndex))
                File.Move(legacyIndex, IndexFilePath);

            string vault = ResolvedVaultPath;
            if (File.Exists(IndexFilePath) || vault.Length == 0) return;
            if (string.Equals(Path.GetFullPath(vault), Path.GetFullPath(ExeDir), StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(Path.Combine(vault, IndexFormat.FileName))) return;
            foreach (var name in new[] { IndexFormat.FileName, "scan.checkpoint", "scan.partial", "scan.log" })
            {
                string oldPath = Path.Combine(vault, name);
                string newPath = Path.Combine(ExeDir, name);
                if (File.Exists(oldPath) && !File.Exists(newPath))
                    File.Move(oldPath, newPath);
            }
        }
        catch
        {
            // best-effort migration; a fresh Process rebuilds everything
        }
    }

    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand BrowseVaultCommand { get; }
    public RelayCommand BrowseSourceCommand { get; }
    public RelayCommand BrowseErrorCommand { get; }
    public RelayCommand ExportEntryCommand { get; }
    public RelayCommand ExportFamilyCommand { get; }
    public RelayCommand ResetAxesCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    private void OpenLogs()
    {
        string logPath = Path.Combine(ExeDir, "scan.log");
        if (!File.Exists(logPath))
        {
            StatusText = "No log file yet.";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "Could not open the log file: " + ex.Message;
        }
    }

    private string _vaultPath = "";
    public string VaultPath { get => _vaultPath; set => Set(ref _vaultPath, value); }

    /// <summary>Vault path with relative values (e.g. the default ".\fonts-vault") resolved against the exe folder.</summary>
    private string ResolvedVaultPath =>
        VaultPath.Length == 0 ? "" :
        Path.IsPathRooted(VaultPath) ? VaultPath :
        Path.GetFullPath(Path.Combine(ExeDir, VaultPath));

    private string _sourcePath = "";
    public string SourcePath { get => _sourcePath; set => Set(ref _sourcePath, value); }

    /// <summary>Source path with relative values resolved against the exe folder (like the vault).</summary>
    private string ResolvedSourcePath =>
        SourcePath.Length == 0 ? "" :
        Path.IsPathRooted(SourcePath) ? SourcePath :
        Path.GetFullPath(Path.Combine(ExeDir, SourcePath));

    private string _errorPath = "";
    /// <summary>Folder where fonts that fail to parse are copied during a scan.</summary>
    public string ErrorPath { get => _errorPath; set => Set(ref _errorPath, value); }

    private string ResolvedErrorPath =>
        ErrorPath.Length == 0 ? "" :
        Path.IsPathRooted(ErrorPath) ? ErrorPath :
        Path.GetFullPath(Path.Combine(ExeDir, ErrorPath));

    private bool _includeWindowsFonts = true;
    public bool IncludeWindowsFonts { get => _includeWindowsFonts; set => Set(ref _includeWindowsFonts, value); }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) { _searchTimer.Stop(); _searchTimer.Start(); }
        }
    }

    public string[] FormatFilters { get; } = { "All", "OTF", "TTF", "WOFF", "WOFF2", "EOT" };

    private string _selectedFormatFilter = "All";
    public string SelectedFormatFilter
    {
        get => _selectedFormatFilter;
        set { if (Set(ref _selectedFormatFilter, value)) ApplyFilter(); }
    }

    private bool _variableOnly;
    public bool VariableOnly
    {
        get => _variableOnly;
        set { if (Set(ref _variableOnly, value)) ApplyFilter(); }
    }

    private bool _installedOnly;
    public bool InstalledOnly
    {
        get => _installedOnly;
        set { if (Set(ref _installedOnly, value)) ApplyFilter(); }
    }

    private bool _favoritesOnly;
    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set { if (Set(ref _favoritesOnly, value)) ApplyFilter(); }
    }

    public string[] SortModes { get; } = { "Name", "Glyph count", "Variant count" };

    private string _selectedSortMode = "Name";
    public string SelectedSortMode
    {
        get => _selectedSortMode;
        set { if (Set(ref _selectedSortMode, value)) ApplyFilter(); }
    }

    private List<FamilyGroup> _families = new();
    public List<FamilyGroup> Families { get => _families; private set => Set(ref _families, value); }

    private FamilyGroup? _selectedFamily;
    public FamilyGroup? SelectedFamily
    {
        get => _selectedFamily;
        set
        {
            if (Set(ref _selectedFamily, value))
                SelectedEntry = value?.Entries.FirstOrDefault();
        }
    }

    private FontEntry? _selectedEntry;
    public FontEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (Set(ref _selectedEntry, value))
            {
                UpdateDetails();
                UpdatePreviewTarget(value);
                RefreshInstallUi();
            }
        }
    }

    // ---- Windows install state ----

    private bool _suppressInstallToggle;

    private bool _isSelectedInstalled;
    public bool IsSelectedInstalled
    {
        get => _isSelectedInstalled;
        set
        {
            if (Set(ref _isSelectedInstalled, value) && !_suppressInstallToggle)
                _ = ToggleInstallAsync(value);
        }
    }

    private bool _canToggleInstall;
    public bool CanToggleInstall { get => _canToggleInstall; private set => Set(ref _canToggleInstall, value); }

    private string _installToggleTooltip = "";
    public string InstallToggleTooltip { get => _installToggleTooltip; private set => Set(ref _installToggleTooltip, value); }

    private string _installToggleLabel = "Install in Windows";
    /// <summary>Action-oriented label so the toggle reads as install/uninstall, not a passive status.</summary>
    public string InstallToggleLabel { get => _installToggleLabel; private set => Set(ref _installToggleLabel, value); }

    /// <summary>Install state of the whole selected family (the toggle acts on every variant).</summary>
    private void RefreshInstallUi()
    {
        var entries = SelectedFamily?.Entries;
        int total = entries?.Count ?? 0;
        int installed = 0;
        if (entries != null) foreach (var e in entries) if (e.IsInstalled) installed++;
        bool allInstalled = total > 0 && installed == total;

        _suppressInstallToggle = true;
        IsSelectedInstalled = allInstalled;
        CanToggleInstall = total > 0;
        InstallToggleLabel = allInstalled ? "Installed (toggle)" : "Not installed (toggle)";
        InstallToggleTooltip = total == 0
            ? "Select a family to install or uninstall it."
            : allInstalled
                ? $"All {total} variant(s) installed — toggle off to uninstall the family for the current user."
                : installed > 0
                    ? $"{installed}/{total} variant(s) installed — toggle on to install the whole family."
                    : "Toggle on to install the whole family for the current user (no admin rights needed).";
        _suppressInstallToggle = false;
    }

    private async Task ToggleInstallAsync(bool install)
    {
        var family = SelectedFamily;
        if (family == null || family.Entries.Count == 0) return;
        var entries = family.Entries.ToList();
        string vaultRoot = ResolvedVaultPath;
        int done = 0, failed = 0;
        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                try
                {
                    if (install)
                    {
                        if (entry.IsInstalled) continue;
                        // Containers (WOFF/WOFF2/EOT) are installed as their rebuilt standalone sfnt.
                        string sfnt = PreviewCache.GetPreviewPath(Path.Combine(vaultRoot, entry.VaultRelPath), entry);
                        InstalledFontsService.Install(entry, sfnt);
                    }
                    else
                    {
                        if (!entry.IsInstalled) continue;
                        InstalledFontsService.Uninstall(entry);
                    }
                    done++;
                }
                catch
                {
                    failed++;
                }
            }
        });
        StatusText = install
            ? $"Installed {done} variant(s) of \"{family.Name}\"" + (failed > 0 ? $" — {failed} failed" : "")
            : $"Uninstalled {done} variant(s) of \"{family.Name}\"" + (failed > 0 ? $" — {failed} failed (system fonts need admin rights)" : "");
        RefreshInstallUi();
        UpdateDetails();
        RefreshVariantView();
        if (InstalledOnly) ApplyFilter();
    }

    /// <summary>Re-creates the variant rows: install indicators bind to non-INPC entry state.</summary>
    private void RefreshVariantView()
    {
        if (SelectedFamily != null)
            System.Windows.Data.CollectionViewSource.GetDefaultView(SelectedFamily.Entries)?.Refresh();
    }

    private async Task ComputeInstalledStatesAsync(FontEntry[] entries)
    {
        try
        {
            await Task.Run(() => InstalledFontsService.ComputeStates(entries));
        }
        catch
        {
            return; // detection is best effort
        }
        if (_entries != entries) return;
        RefreshVariantView();
        RefreshInstallUi();
        UpdateDetails();
        if (InstalledOnly) ApplyFilter();
    }

    /// <summary>Absolute vault file paths for an outbound drag (one variant, or every variant of a family).</summary>
    public string[] GetDragPaths(object? item)
    {
        string vault = ResolvedVaultPath;
        if (vault.Length == 0) return Array.Empty<string>();
        IEnumerable<FontEntry> list = item switch
        {
            FontEntry entry => new[] { entry },
            FamilyGroup group => group.Entries,
            _ => Array.Empty<FontEntry>(),
        };
        return list.Select(e => Path.Combine(vault, e.VaultRelPath)).Where(File.Exists).ToArray();
    }

    private List<DetailItem> _details = new();
    public List<DetailItem> Details { get => _details; private set => Set(ref _details, value); }

    // ---- Preview ----

    private string? _previewFontPath;
    public string? PreviewFontPath { get => _previewFontPath; private set => Set(ref _previewFontPath, value); }

    private FontFamily? _previewFontFamily;
    public FontFamily? PreviewFontFamily { get => _previewFontFamily; private set => Set(ref _previewFontFamily, value); }

    private FontWeight _previewFontWeight = FontWeights.Normal;
    public FontWeight PreviewFontWeight { get => _previewFontWeight; private set => Set(ref _previewFontWeight, value); }

    private FontStyle _previewFontStyle = FontStyles.Normal;
    public FontStyle PreviewFontStyle { get => _previewFontStyle; private set => Set(ref _previewFontStyle, value); }

    private string _previewText = "The quick brown fox jumps over the lazy dog.\nPortez ce vieux whisky au juge blond qui fume.\n0123456789 ÀÉÈÙÇŒÆ €$£ «» — fi fl ffi st";
    public string PreviewText { get => _previewText; set => Set(ref _previewText, value); }

    private double _previewSize = 36;
    public double PreviewSize { get => _previewSize; set => Set(ref _previewSize, value); }

    private bool _showMissingChars = true;
    public bool ShowMissingChars { get => _showMissingChars; set => Set(ref _showMissingChars, value); }

    private bool _shapedMode;
    public bool ShapedMode { get => _shapedMode; set => Set(ref _shapedMode, value); }

    private bool _featLiga = true;
    public bool FeatLiga { get => _featLiga; set => Set(ref _featLiga, value); }

    private bool _featDlig;
    public bool FeatDlig { get => _featDlig; set => Set(ref _featDlig, value); }

    private bool _featKern = true;
    public bool FeatKern { get => _featKern; set => Set(ref _featKern, value); }

    private bool _featSmcp;
    public bool FeatSmcp
    {
        get => _featSmcp;
        set { if (Set(ref _featSmcp, value)) OnPropertyChanged(nameof(CapitalsMode)); }
    }

    public FontCapitals CapitalsMode => FeatSmcp ? FontCapitals.SmallCaps : FontCapitals.Normal;

    // ---- Glyph viewer ----

    private List<BlockCoverage> _glyphBlocks = new();
    public List<BlockCoverage> GlyphBlocks { get => _glyphBlocks; private set => Set(ref _glyphBlocks, value); }

    private BlockCoverage? _selectedGlyphBlock;
    public BlockCoverage? SelectedGlyphBlock
    {
        get => _selectedGlyphBlock;
        set { if (Set(ref _selectedGlyphBlock, value)) RebuildGlyphRows(); }
    }

    private bool _glyphShowAll = true;
    public bool GlyphShowAll
    {
        get => _glyphShowAll;
        set { if (Set(ref _glyphShowAll, value)) RebuildGlyphRows(); }
    }

    private List<GlyphRow> _glyphRows = new();
    public List<GlyphRow> GlyphRows { get => _glyphRows; private set => Set(ref _glyphRows, value); }

    // ---- Variable axes (real-time, DirectWrite) ----

    private List<AxisSliderVM> _axisSliders = new();
    public List<AxisSliderVM> AxisSliders { get => _axisSliders; private set => Set(ref _axisSliders, value); }

    private bool _hasVariableAxes;
    public bool HasVariableAxes { get => _hasVariableAxes; private set => Set(ref _hasVariableAxes, value); }

    private IReadOnlyList<AxisValue> _currentAxisValues = Array.Empty<AxisValue>();
    public IReadOnlyList<AxisValue> CurrentAxisValues { get => _currentAxisValues; private set => Set(ref _currentAxisValues, value); }

    private void OnAxisChanged() =>
        CurrentAxisValues = AxisSliders.Select(s => new AxisValue(s.Tag, (float)s.Value)).ToArray();

    private void BuildAxisSliders()
    {
        AxisSliders = _selectedAxes.Select(a => new AxisSliderVM(a, OnAxisChanged)).ToList();
        HasVariableAxes = AxisSliders.Count > 0;
        OnAxisChanged();
    }

    // ---- GSUB inspection / ligatures ----

    private List<GsubFeatureInfo> _gsubFeatures = new();
    public List<GsubFeatureInfo> GsubFeatures { get => _gsubFeatures; private set => Set(ref _gsubFeatures, value); }

    private List<LigatureRow> _ligatures = new();
    public List<LigatureRow> Ligatures { get => _ligatures; private set => Set(ref _ligatures, value); }

    private async void BuildGsub(string previewPath)
    {
        int request = ++_gsubRequest;
        try
        {
            var (features, ligatures) = await Task.Run(() =>
            {
                byte[] bytes = File.ReadAllBytes(previewPath);
                return GsubInspector.Inspect(bytes);
            });
            if (request != _gsubRequest) return;
            GsubFeatures = features;
            Ligatures = ligatures
                .Select(l => new LigatureRow { Components = l.Components, GlyphIndex = l.LigatureGlyph, FontPath = previewPath })
                .ToList();
        }
        catch
        {
            if (request == _gsubRequest)
            {
                GsubFeatures = new List<GsubFeatureInfo>();
                Ligatures = new List<LigatureRow>();
            }
        }
    }

    // ---- Status / scan ----

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>Bottom-left counter: "N fonts" or "M / N fonts" while a filter is active.</summary>
    private string _countText = "";
    public string CountText { get => _countText; private set => Set(ref _countText, value); }

    private string _scanStatus = "";
    public string ScanStatus { get => _scanStatus; set => Set(ref _scanStatus, value); }

    // Shared progress bar: index load and scan.
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            // Whole-percent threshold: avoids flooding the dispatcher during tight loops.
            if (Math.Abs(value - _progressPercent) >= 1.0 || value <= 0 || value >= 100)
                Set(ref _progressPercent, value);
        }
    }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CancelScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private static void Browse(Action<string> assign)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog() == true) assign(dialog.FolderName);
    }

    private async Task LoadIndexAsync()
    {
        string indexPath = IndexFilePath;
        if (!File.Exists(indexPath))
        {
            StatusText = "No index yet — click Process to build it.";
            return;
        }

        StatusText = "Loading index…";
        IsBusy = true;
        ProgressPercent = 0;
        var progress = new Progress<double>(p => ProgressPercent = p);
        try
        {
            CloseIndex();
            var loaded = await Task.Run(() =>
            {
                IProgress<double> report = progress;
                var reader = IndexReader.Load(indexPath, new Progress<double>(p => report.Report(p * 90)));
                // Normalized search keys, precomputed once (§9).
                var keys = new string[reader.Entries.Length];
                for (int i = 0; i < reader.Entries.Length; i++)
                {
                    if ((i & 0xFFFF) == 0)
                        report.Report(90 + 10.0 * i / keys.Length);
                    var e = reader.Entries[i];
                    keys[i] = $"{e.WindowsDisplayName}\n{e.FamilyName}\n{e.Style}\n{e.TypographicSubfamily}\n{e.FullName}\n{e.PostScriptName}"
                        .ToLowerInvariant();
                }
                return (reader, keys);
            });
            _reader = loaded.reader;
            _entries = loaded.reader.Entries;
            _searchKeys = loaded.keys;
            StatusText = "";
            ApplyFilter();
            SaveSettings();
            _ = CheckInvalidationAsync(loaded.reader);
            _ = ComputeInstalledStatesAsync(loaded.reader.Entries);
        }
        catch (Exception ex)
        {
            StatusText = "Index load failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
        }
    }

    /// <summary>Invalidation (§5): background comparison between the index and the actual vault content.</summary>
    private async Task CheckInvalidationAsync(IndexReader reader)
    {
        string vault = ResolvedVaultPath;
        if (vault.Length == 0 || !Directory.Exists(vault)) return;
        try
        {
            var (count, bytes) = await Task.Run(() =>
            {
                int c = 0;
                long b = 0;
                var extensions = new[] { "*.otf", "*.ttf", "*.woff", "*.woff2", "*.eot" };
                foreach (var pattern in extensions)
                {
                    foreach (var f in Directory.EnumerateFiles(vault, pattern, SearchOption.AllDirectories))
                    {
                        c++;
                        b += new FileInfo(f).Length;
                    }
                }
                return (c, b);
            });
            if (_reader == reader && (count != reader.VaultFileCount || bytes != reader.VaultTotalBytes))
                StatusText = $"Index/vault mismatch detected ({count} files vs {reader.VaultFileCount} indexed) — run Process again.";
        }
        catch
        {
            // non-blocking invalidation check: silent failure
        }
    }

    private void CloseIndex()
    {
        SelectedFamily = null;
        SelectedEntry = null;
        Families = new List<FamilyGroup>();
        _entries = Array.Empty<FontEntry>();
        _searchKeys = Array.Empty<string>();
        CountText = "";
        _reader?.Dispose();
        _reader = null;
    }

    /// <summary>Scan sources: the user folder plus, when enabled, the system and per-user Windows font folders.</summary>
    private List<string> BuildScanSources()
    {
        var sources = new List<string>();
        string src = ResolvedSourcePath;
        if (src.Length > 0 && Directory.Exists(src))
            sources.Add(src);
        if (IncludeWindowsFonts)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (system.Length > 0 && Directory.Exists(system))
                sources.Add(system);
            string perUser = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\Fonts");
            if (Directory.Exists(perUser))
                sources.Add(perUser);
        }
        return sources;
    }

    private async Task RunScanAsync()
    {
        if (VaultPath.Length == 0) { StatusText = "Vault folder not set."; return; }
        if (SourcePath.Length > 0 && !Directory.Exists(ResolvedSourcePath)) { StatusText = "Invalid source folder."; return; }
        var sources = BuildScanSources();
        if (sources.Count == 0) { StatusText = "Nothing to process (empty source folder and Windows fonts disabled)."; return; }

        IsScanning = true;
        IsBusy = true;
        ProgressPercent = 0;
        ScanStatus = "Starting…";
        SaveSettings();
        ClearLog(); // empty the log at the start of each Process run

        // Reader ownership transferred to the scan (dispose guaranteed by the service).
        var reader = _reader;
        _reader = null;
        SelectedFamily = null;
        SelectedEntry = null;
        Families = new List<FamilyGroup>();
        _entries = Array.Empty<FontEntry>();
        _searchKeys = Array.Empty<string>();

        _scanCts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.PhaseTotal > 0)
            {
                ScanStatus = $"{p.Phase} — {p.PhaseProcessed:N0}/{p.PhaseTotal:N0}, {p.Copied} copied, {p.Errors} errors";
                ProgressPercent = Math.Min(100.0, p.PhaseProcessed * 100.0 / p.PhaseTotal);
            }
            else
            {
                ScanStatus = $"{p.Phase} — {p.Processed:N0}/{p.Discovered:N0} files, {p.Copied} copied, {p.Errors} errors";
                ProgressPercent = p.Discovered > 0 ? Math.Min(100.0, p.Processed * 100.0 / p.Discovered) : 0;
            }
        });

        string vault = ResolvedVaultPath;
        string? errorDir = ResolvedErrorPath.Length > 0 ? ResolvedErrorPath : null;
        try
        {
            var result = await Task.Run(() =>
                ScanService.Run(sources, vault, ExeDir, reader, progress, _scanCts.Token, errorDir));
            StatusText = "";
            ScanStatus = $"✔ Process completed — {result.Copied} added, {result.Errors} errors ({result.TotalEntries:N0} fonts indexed)";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "⏸ Process interrupted — click Process to resume from the checkpoint";
        }
        catch (Exception ex)
        {
            ScanStatus = "✖ Process failed";
            StatusText = "Processing failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            await LoadIndexAsync();
            IsBusy = false;
            ProgressPercent = 0;
        }
    }

    private async void ApplyFilter()
    {
        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;

        var entries = _entries;
        var keys = _searchKeys;
        string search = SearchText.Trim().ToLowerInvariant();
        FontExt? extFilter = SelectedFormatFilter switch
        {
            "OTF" => FontExt.Otf,
            "TTF" => FontExt.Ttf,
            "WOFF" => FontExt.Woff,
            "WOFF2" => FontExt.Woff2,
            "EOT" => FontExt.Eot,
            _ => null,
        };
        bool variableOnly = VariableOnly;
        bool installedOnly = InstalledOnly;
        bool favoritesOnly = FavoritesOnly;
        var favorites = new HashSet<string>(_favorites, StringComparer.OrdinalIgnoreCase);
        string sortMode = SelectedSortMode;

        try
        {
            var groups = await Task.Run(() =>
            {
                bool Match(int i)
                {
                    var e = entries[i];
                    if (variableOnly && !e.IsVariableFont) return false;
                    if (installedOnly && !e.IsInstalled) return false;
                    if (extFilter != null && e.Extension != extFilter) return false;
                    if (search.Length > 0 && !keys[i].Contains(search, StringComparison.Ordinal)) return false;
                    return true;
                }

                // Parallel partitioned scan above 200k entries (§9), sequential below.
                int n = entries.Length;
                List<FontEntry> matched;
                if (n >= 200_000)
                {
                    int parts = Math.Max(2, Environment.ProcessorCount);
                    var partial = new List<FontEntry>?[parts];
                    Parallel.For(0, parts, p =>
                    {
                        int from = (int)((long)n * p / parts);
                        int to = (int)((long)n * (p + 1) / parts);
                        var local = new List<FontEntry>();
                        for (int i = from; i < to; i++)
                        {
                            if ((i & 0x3FFF) == 0 && cts.Token.IsCancellationRequested) return;
                            if (Match(i)) local.Add(entries[i]);
                        }
                        partial[p] = local;
                    });
                    if (cts.Token.IsCancellationRequested) return null;
                    matched = new List<FontEntry>();
                    foreach (var part in partial)
                    {
                        if (part == null) return null;
                        matched.AddRange(part);
                    }
                }
                else
                {
                    matched = new List<FontEntry>();
                    for (int i = 0; i < n; i++)
                    {
                        if ((i & 0x3FFF) == 0 && cts.Token.IsCancellationRequested) return null;
                        if (Match(i)) matched.Add(entries[i]);
                    }
                }

                // Grouping by typographic family (Windows display name).
                var dict = new Dictionary<string, FamilyGroup>(StringComparer.OrdinalIgnoreCase);
                var result = new List<FamilyGroup>();
                foreach (var e in matched)
                {
                    string family = e.WindowsDisplayName;
                    if (!dict.TryGetValue(family, out var group))
                    {
                        dict[family] = group = new FamilyGroup(family) { IsFavorite = favorites.Contains(family) };
                        result.Add(group);
                    }
                    group.Entries.Add(e);
                }
                if (favoritesOnly) result.RemoveAll(g => !g.IsFavorite);
                switch (sortMode)
                {
                    case "Glyph count":
                        result.Sort((a, b) =>
                        {
                            int c = b.MaxGlyphs.CompareTo(a.MaxGlyphs);
                            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                        });
                        break;
                    case "Variant count":
                        result.Sort((a, b) =>
                        {
                            int c = b.Count.CompareTo(a.Count);
                            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                        });
                        break;
                    default:
                        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                        break;
                }
                return (List<FamilyGroup>?)result;
            }, cts.Token);

            if (groups != null && _filterCts == cts)
            {
                Families = groups;
                if (SelectedFamily == null && groups.Count > 0)
                    SelectedFamily = groups[0];

                bool filtered = search.Length > 0 || extFilter != null || variableOnly || installedOnly || favoritesOnly;
                int visible = 0;
                foreach (var g in groups) visible += g.Entries.Count;
                CountText = entries.Length == 0 ? ""
                    : filtered ? $"{visible:N0} / {entries.Length:N0} fonts"
                    : $"{entries.Length:N0} fonts";
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateDetails()
    {
        var e = SelectedEntry;
        _selectedCoverage = new List<UnicodeInterval>();
        _selectedAxes = new List<VariableAxis>();
        if (e == null)
        {
            Details = new List<DetailItem>();
            GlyphBlocks = new List<BlockCoverage>();
            GlyphRows = new List<GlyphRow>();
            return;
        }
        var items = new List<DetailItem>
        {
            new("Display name", e.WindowsDisplayName),
            new("Family", e.FamilyName),
            new("Typographic family", e.TypographicFamilyName),
            new("Style", e.Style),
            new("Typographic subfamily", e.TypographicSubfamily),
            new("Full name", e.FullName),
            new("PostScript name", e.PostScriptName),
            new("Version", e.Version),
            new("Glyphs", e.GlyphCount.ToString(CultureInfo.InvariantCulture)),
            new("Format", e.Format == FontFormat.Cff ? "OpenType CFF" : "TrueType"),
            new("Extension", FontEntry.ExtensionString(e.Extension)),
            new("Variable font", e.IsVariableFont ? "Yes" : "No"),
            new("File size", $"{e.FileSize:N0} bytes"),
            new("CRC32", e.Crc32.ToString("X8")),
            new("Scan date", new DateTime(e.ScanDateTicks, DateTimeKind.Utc).ToLocalTime().ToString("g")),
            new("Vault path", e.VaultRelPath),
            new("Installed in Windows", e.InstalledState switch
            {
                1 => "Yes (per-user, by FontVault)",
                2 => "Yes (system or external install)",
                _ => "No",
            }),
        };
        if (_reader != null)
        {
            try
            {
                var heavy = _reader.GetHeavy(e);
                _selectedCoverage = heavy.Coverage;
                _selectedAxes = heavy.Axes;
                int codepoints = 0;
                foreach (var iv in heavy.Coverage) codepoints += iv.Count;
                items.Add(new DetailItem("Unicode coverage", $"{codepoints:N0} codepoints, {heavy.Coverage.Count:N0} ranges"));
                if (heavy.Axes.Count > 0)
                    items.Add(new DetailItem("Variable axes", string.Join(Environment.NewLine,
                        heavy.Axes.Select(a => $"{a.Tag}: {a.Min:0.##} → {a.Max:0.##} (default {a.Default:0.##})"))));
                items.Add(new DetailItem("OpenType features", heavy.Features.Count == 0 ? "—" : string.Join(", ", heavy.Features)));
                items.Add(new DetailItem("Ligatures", heavy.HasLigatures ? "Yes" : "No"));
            }
            catch (Exception ex)
            {
                items.Add(new DetailItem("Extended details", "Read error: " + ex.Message));
            }
        }
        Details = items;
    }

    /// <summary>
    /// Resolves the previewable font file: direct vault file for OTF/TTF,
    /// rebuilt standalone sfnt (background, cached) for WOFF/WOFF2/EOT.
    /// </summary>
    private async void UpdatePreviewTarget(FontEntry? e)
    {
        int request = ++_previewRequest;
        if (e == null || VaultPath.Length == 0)
        {
            PreviewFontPath = null;
            PreviewFontFamily = null;
            GlyphBlocks = new List<BlockCoverage>();
            GlyphRows = new List<GlyphRow>();
            AxisSliders = new List<AxisSliderVM>();
            HasVariableAxes = false;
            GsubFeatures = new List<GsubFeatureInfo>();
            Ligatures = new List<LigatureRow>();
            return;
        }
        string abs = Path.Combine(ResolvedVaultPath, e.VaultRelPath);
        try
        {
            string path = await Task.Run(() => PreviewCache.GetPreviewPath(abs, e));
            if (request != _previewRequest) return;
            PreviewFontPath = path;
            // Shaped rendering: family loaded from the folder, weight/style mapped from the effective style.
            string dir = Path.GetDirectoryName(path)!;
            PreviewFontFamily = new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), $"./#{e.WindowsDisplayName}");
            PreviewFontWeight = MapWeight(e.EffectiveStyle);
            PreviewFontStyle = MapStyle(e.EffectiveStyle);
            RebuildGlyphBlocks();
            BuildAxisSliders();
            BuildGsub(path);
        }
        catch (Exception ex)
        {
            if (request == _previewRequest)
            {
                PreviewFontPath = null;
                PreviewFontFamily = null;
                StatusText = "Preview unavailable: " + ex.Message;
            }
        }
    }

    private static FontWeight MapWeight(string style)
    {
        string s = style.ToLowerInvariant();
        if (s.Contains("extrabold") || s.Contains("ultrabold")) return FontWeights.ExtraBold;
        if (s.Contains("semibold") || s.Contains("demibold") || s.Contains("demi")) return FontWeights.SemiBold;
        if (s.Contains("black") || s.Contains("heavy")) return FontWeights.Black;
        if (s.Contains("bold")) return FontWeights.Bold;
        if (s.Contains("extralight") || s.Contains("ultralight")) return FontWeights.ExtraLight;
        if (s.Contains("light")) return FontWeights.Light;
        if (s.Contains("medium")) return FontWeights.Medium;
        if (s.Contains("thin")) return FontWeights.Thin;
        return FontWeights.Normal;
    }

    private static FontStyle MapStyle(string style)
    {
        string s = style.ToLowerInvariant();
        return s.Contains("italic") || s.Contains("oblique") ? FontStyles.Italic : FontStyles.Normal;
    }

    // ---- Glyph viewer ----

    private void RebuildGlyphBlocks()
    {
        var coverage = _selectedCoverage;
        var blocks = new List<BlockCoverage>(UnicodeBlocks.All.Length);
        foreach (var block in UnicodeBlocks.All)
        {
            int present = CountCovered(coverage, block.Start, block.End);
            blocks.Add(new BlockCoverage(block, present));
        }
        GlyphBlocks = blocks;
        SelectedGlyphBlock = blocks.FirstOrDefault(b => b.Present > 0) ?? blocks.FirstOrDefault();
    }

    private async void RebuildGlyphRows()
    {
        int request = ++_glyphRequest;
        var block = SelectedGlyphBlock;
        var coverage = _selectedCoverage;
        string? fontPath = PreviewFontPath;
        bool showAll = GlyphShowAll;
        if (block == null)
        {
            GlyphRows = new List<GlyphRow>();
            return;
        }

        var rows = await Task.Run(() =>
        {
            var result = new List<GlyphRow>();
            var codepoints = new List<int>(GlyphGridRowControl.CellsPerRow);
            var present = new List<bool>(GlyphGridRowControl.CellsPerRow);
            void Flush()
            {
                if (codepoints.Count == 0) return;
                while (codepoints.Count < GlyphGridRowControl.CellsPerRow)
                {
                    codepoints.Add(-1);
                    present.Add(false);
                }
                result.Add(new GlyphRow { FontPath = fontPath, Codepoints = codepoints.ToArray(), Present = present.ToArray() });
                codepoints.Clear();
                present.Clear();
            }
            for (int cp = block.Block.Start; cp <= block.Block.End; cp++)
            {
                bool covered = IsCovered(coverage, cp);
                if (!showAll && !covered) continue;
                codepoints.Add(cp);
                present.Add(covered);
                if (codepoints.Count == GlyphGridRowControl.CellsPerRow) Flush();
            }
            Flush();
            return result;
        });
        if (request == _glyphRequest)
            GlyphRows = rows;
    }

    private static bool IsCovered(List<UnicodeInterval> sorted, int cp)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var iv = sorted[mid];
            if (cp < iv.Start) hi = mid - 1;
            else if (cp > iv.End) lo = mid + 1;
            else return true;
        }
        return false;
    }

    private static int CountCovered(List<UnicodeInterval> sorted, int start, int end)
    {
        int count = 0;
        foreach (var iv in sorted)
        {
            if (iv.End < start) continue;
            if (iv.Start > end) break;
            count += Math.Min(iv.End, end) - Math.Max(iv.Start, start) + 1;
        }
        return count;
    }

    // ---- Export ----

    private async void ExportEntries(IReadOnlyList<FontEntry>? entries)
    {
        if (entries == null || entries.Count == 0 || VaultPath.Length == 0)
        {
            StatusText = "Nothing selected to export.";
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Export folder" };
        if (dialog.ShowDialog() != true) return;
        string dest = dialog.FolderName;
        string vault = ResolvedVaultPath;
        var list = entries.ToList();

        var (copiedCount, errorCount) = await Task.Run(() =>
        {
            int ok = 0, err = 0;
            foreach (var e in list)
            {
                try
                {
                    string src = Path.Combine(vault, e.VaultRelPath);
                    string target = Path.Combine(dest, Path.GetFileName(e.VaultRelPath));
                    File.Copy(src, target, overwrite: false);
                    ok++;
                }
                catch
                {
                    err++;
                }
            }
            return (ok, err);
        });
        StatusText = $"Export: {copiedCount} file(s) copied to {dest}, {errorCount} error(s).";
    }

    // ---- Settings ----

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var lines = File.ReadAllLines(SettingsPath);
            if (lines.Length > 0) _vaultPath = lines[0];
            if (lines.Length > 1) _sourcePath = lines[1];
            if (lines.Length > 2) _includeWindowsFonts = lines[2] != "0";
            if (lines.Length > 3) _errorPath = lines[3];
            OnPropertyChanged(nameof(VaultPath));
            OnPropertyChanged(nameof(SourcePath));
            OnPropertyChanged(nameof(IncludeWindowsFonts));
            OnPropertyChanged(nameof(ErrorPath));
        }
        catch
        {
            // unreadable settings: defaults
        }
    }

    internal void SaveSettings()
    {
        try
        {
            File.WriteAllLines(SettingsPath, new[] { VaultPath, SourcePath, IncludeWindowsFonts ? "1" : "0", ErrorPath });
        }
        catch
        {
            // settings persistence is not critical (e.g. exe on a read-only medium)
        }
    }

    /// <summary>Empties the scan log (called on app start and at the start of each Process run).</summary>
    private static void ClearLog()
    {
        try { File.WriteAllText(Path.Combine(ExeDir, "scan.log"), string.Empty); }
        catch { /* best-effort */ }
    }

    // ---- Favorites (family names, persisted in favorites.txt) ----

    private void LoadFavorites()
    {
        try
        {
            if (!File.Exists(FavoritesPath)) return;
            foreach (var line in File.ReadAllLines(FavoritesPath))
            {
                string name = line.Trim();
                if (name.Length > 0) _favorites.Add(name);
            }
        }
        catch
        {
            // unreadable favorites: none
        }
    }

    private void SaveFavorites()
    {
        try { File.WriteAllLines(FavoritesPath, _favorites.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)); }
        catch { /* not critical */ }
    }

    /// <summary>Toggles the favorite flag of a family, persists it, and refreshes the view when filtering favorites.</summary>
    public void ToggleFavorite(FamilyGroup family)
    {
        if (family == null) return;
        if (_favorites.Remove(family.Name)) family.IsFavorite = false;
        else { _favorites.Add(family.Name); family.IsFavorite = true; }
        SaveFavorites();
        if (FavoritesOnly) ApplyFilter();
    }
}
