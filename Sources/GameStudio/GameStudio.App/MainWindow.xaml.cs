using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameStudio.Core.Formats;
using GameStudio.Core.Imaging;
using GameStudio.Core.Modernize;
using GameStudio.Core.Unreal;
using Microsoft.Win32;

namespace GameStudio.App;

public partial class MainWindow : Window
{
    // Same file GameStudio.Mcp writes its liveness stamp to; see Heartbeat.cs there.
    private static readonly string McpHeartbeatPath = Path.Combine(Path.GetTempPath(), "GameStudioMcp.heartbeat");
    private static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(8);

    private readonly DispatcherTimer _mcpStatusTimer;
    private GroArchive? _archive;
    private List<EntryRow> _allEntries = [];
    private TexFile? _previewTexture;
    private string? _previewName;
    private Process? _mcpProcess;
    private CancellationTokenSource? _job;
    private string? _lastUnrealOutput;

    public MainWindow()
    {
        InitializeComponent();

        txtVersion.Text = "v" + (GetType().Assembly.GetName().Version?.ToString(2) ?? "1.0");
        PopulateOptions();
        PopulateMcpTools();
        UpdateMcpConfigSnippet();

        _mcpStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _mcpStatusTimer.Tick += (_, _) => UpdateMcpStatus();
        _mcpStatusTimer.Start();
        UpdateMcpStatus();

        Closed += (_, _) =>
        {
            _mcpStatusTimer.Stop();
            _job?.Cancel();
            StopMcpProcess();
            _archive?.Dispose();
        };
    }

    private void PopulateOptions()
    {
        foreach (int factor in new[] { 1, 2, 3, 4, 6, 8 })
            cmbScale.Items.Add($"{factor}x");
        cmbScale.SelectedIndex = 3;

        foreach (string filter in Enum.GetNames<ResampleFilter>())
            cmbFilter.Items.Add(filter);
        cmbFilter.SelectedItem = nameof(ResampleFilter.Lanczos3);

        cmbKind.Items.Add("All kinds");
        foreach (string kind in Enum.GetNames<AssetKind>())
            cmbKind.Items.Add(kind);
        cmbKind.SelectedIndex = 0;
    }

    private void PopulateMcpTools() => listMcpTools.ItemsSource = new[]
    {
        new McpToolRow("engine_overview", "Summarize an engine installation: archives, asset counts by kind, total size."),
        new McpToolRow("list_archive", "List the entries of a .gro archive, filtered by kind or path substring."),
        new McpToolRow("texture_info", "Decode a .tex header: format version, dimensions, alpha, frame count."),
        new McpToolRow("export_texture", "Decode a .tex frame and write it out as a PNG."),
        new McpToolRow("modernize_textures", "Upscale textures and derive normal, roughness and occlusion maps."),
        new McpToolRow("export_unreal", "Build the Unreal Engine 5 import package from a modernized material set."),
    };

    private void UpdateMcpConfigSnippet()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "GameStudio.Mcp.exe");
        string root = string.IsNullOrWhiteSpace(txtMcpRoot.Text) ? "<engine folder>" : txtMcpRoot.Text;

        txtMcpConfig.Text = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["gamestudio-engine"] = new { command = exe, args = new[] { "--root", root } },
            },
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    // ===================================================================== navigation

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (viewDashboard is null) return; // fires once during InitializeComponent, before the views exist

        viewDashboard.Visibility = navDashboard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        viewAssets.Visibility = navAssets.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        viewModernize.Visibility = navModernize.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        viewUnreal.Visibility = navUnreal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        viewMcp.Visibility = navMcp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message, bool busy = false)
    {
        txtStatus.Text = message;
        statusDot.Fill = (Brush)FindResource(busy ? "WarningBrush" : "SuccessBrush");
    }

    // ===================================================================== engine + archives

    private void OnOpenEngineFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the folder containing the engine's .gro archives" };
        if (dialog.ShowDialog(this) != true) return;
        LoadEngineFolder(dialog.FolderName);
    }

    private void LoadEngineFolder(string root)
    {
        txtEngineRoot.Text = root;
        txtMcpRoot.Text = root;
        UpdateMcpConfigSnippet();

        var rows = new List<ArchiveRow>();
        foreach (string file in Directory.EnumerateFiles(root, "*.gro", SearchOption.TopDirectoryOnly).Order())
        {
            try
            {
                using var archive = GroArchive.Open(file);
                int textures = archive.EntriesOfKind(AssetKind.Texture).Count();
                rows.Add(new ArchiveRow(file, Path.GetFileName(file),
                    $"{archive.Entries.Count:N0} entries · {textures:N0} textures · {EntryRow.FormatSize(archive.TotalUncompressedBytes)}"));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                rows.Add(new ArchiveRow(file, Path.GetFileName(file), $"Unreadable: {ex.Message}"));
            }
        }

        listArchives.ItemsSource = rows;
        txtNoArchives.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(txtModernSource.Text)) txtModernSource.Text = root;
        SetStatus($"Found {rows.Count} archive(s) in {root}");
    }

    private void OnBrowseArchive(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) OpenArchive(path);
    }

    private void OnOpenArchive(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Serious Engine archive (*.gro)|*.gro|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) OpenArchive(dialog.FileName);
    }

    private void OpenArchive(string path)
    {
        try
        {
            _archive?.Dispose();
            _archive = GroArchive.Open(path);
            _allEntries = _archive.Entries.Select(entry => new EntryRow(entry)).ToList();

            txtArchiveName.Text = Path.GetFileName(path);
            ApplyFilter();
            ClearPreview();

            navAssets.IsChecked = true;
            SetStatus($"Opened {Path.GetFileName(path)} — {_allEntries.Count:N0} entries");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            MessageBox.Show(this, ex.Message, "Could not open archive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (listEntries is null) return;

        IEnumerable<EntryRow> rows = _allEntries;

        if (cmbKind.SelectedIndex > 0 && Enum.TryParse<AssetKind>((string)cmbKind.SelectedItem, out var kind))
            rows = rows.Where(r => r.Entry.Kind == kind);

        string needle = txtSearch.Text.Trim();
        if (needle.Length > 0)
            rows = rows.Where(r => r.Path.Contains(needle, StringComparison.OrdinalIgnoreCase));

        var filtered = rows.ToList();
        listEntries.ItemsSource = filtered;
        txtEntryCount.Text = filtered.Count == _allEntries.Count
            ? $"{filtered.Count:N0} entries"
            : $"{filtered.Count:N0} of {_allEntries.Count:N0} entries";
    }

    // ===================================================================== preview

    private void OnEntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (listEntries.SelectedItem is not EntryRow row || _archive is null)
        {
            ClearPreview();
            return;
        }

        txtPreviewTitle.Text = row.Name;

        if (row.Entry.Kind != AssetKind.Texture)
        {
            ShowPreviewMessage($"{row.Entry.Kind} · {EntryRow.FormatSize(row.Entry.Length)} — no preview for this asset kind yet");
            return;
        }

        try
        {
            using var stream = _archive.ReadEntry(row.Path);
            var texture = TexFile.Load(stream);

            if (texture.Frames.Count == 0)
            {
                ShowPreviewMessage(texture.IsEffectTexture
                    ? "Effect texture — its pixels are simulated at runtime, so nothing is stored on disk"
                    : "This texture stores no frame data");
                return;
            }

            _previewTexture = texture;
            _previewName = Path.GetFileNameWithoutExtension(row.Name);
            imgPreview.Source = ToBitmapSource(texture.Frames[0]);
            previewHost.Visibility = Visibility.Visible;
            previewEmpty.Visibility = Visibility.Collapsed;
            btnExportPng.IsEnabled = true;

            string frames = texture.IsAnimated ? $" · {texture.Frames.Count} frames" : string.Empty;
            txtPreviewDetail.Text = $"format v{texture.Version} · {texture.PixelWidth}×{texture.PixelHeight}"
                + (texture.HasAlpha ? " · alpha" : string.Empty) + frames;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            ShowPreviewMessage($"Could not decode: {ex.Message}");
        }
    }

    private void ShowPreviewMessage(string message)
    {
        _previewTexture = null;
        _previewName = null;
        previewHost.Visibility = Visibility.Collapsed;
        previewEmpty.Visibility = Visibility.Visible;
        btnExportPng.IsEnabled = false;
        txtPreviewDetail.Text = message;
    }

    private void ClearPreview()
    {
        txtPreviewTitle.Text = "No file selected";
        ShowPreviewMessage(string.Empty);
    }

    /// <summary>WPF wants BGRA; the decoder produces RGBA, so the two colour channels swap here.</summary>
    private static BitmapSource ToBitmapSource(RgbaImage image)
    {
        var bgra = new byte[image.Pixels.Length];
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            bgra[i] = image.Pixels[i + 2];
            bgra[i + 1] = image.Pixels[i + 1];
            bgra[i + 2] = image.Pixels[i];
            bgra[i + 3] = image.Pixels[i + 3];
        }

        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96,
            PixelFormats.Bgra32, null, bgra, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnExportPng(object sender, RoutedEventArgs e)
    {
        if (_previewTexture is null || _previewTexture.Frames.Count == 0) return;

        var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = (_previewName ?? "texture") + ".png",
        };
        if (dialog.ShowDialog(this) != true) return;

        PngWriter.Save(_previewTexture.Frames[0], dialog.FileName);
        SetStatus($"Exported {Path.GetFileName(dialog.FileName)}");
    }

    // ===================================================================== modernize

    private void OnPickModernizeSource(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Folder of .tex files to modernize" };
        if (dialog.ShowDialog(this) == true) txtModernSource.Text = dialog.FolderName;
    }

    private void OnPickModernizeOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Where to write the modernized material set" };
        if (dialog.ShowDialog(this) == true) txtModernOutput.Text = dialog.FolderName;
    }

    private void OnPickUpscaler(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) txtUpscaler.Text = dialog.FileName;
    }

    private async void OnRunModernize(object sender, RoutedEventArgs e)
    {
        string source = txtModernSource.Text.Trim();
        string output = txtModernOutput.Text.Trim();

        if (!Directory.Exists(source) && !File.Exists(source))
        {
            MessageBox.Show(this, "Pick a folder of .tex files, or a .gro archive.", "Nothing to modernize",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (output.Length == 0)
        {
            MessageBox.Show(this, "Pick an output folder first.", "No output folder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = new ModernizeSettings
        {
            GeneratePbr = chkPbr.IsChecked == true,
            EmitHeightMap = chkHeight.IsChecked == true,
            AllFrames = chkAllFrames.IsChecked == true,
            SkipUpToDate = false,
            Pbr = new PbrOptions(),
            Upscale = new UpscaleSettings
            {
                Factor = int.Parse(((string)cmbScale.SelectedItem).TrimEnd('x')),
                Filter = Enum.Parse<ResampleFilter>((string)cmbFilter.SelectedItem),
                Backend = txtUpscaler.Text.Trim().Length > 0 ? UpscaleBackend.External : UpscaleBackend.BuiltIn,
                ExecutablePath = txtUpscaler.Text.Trim() is { Length: > 0 } path ? path : null,
            },
        };

        _job = new CancellationTokenSource();
        BeginJob("Modernizing");

        var progress = new Progress<ModernizeProgress>(p =>
        {
            jobProgress.Value = p.Fraction * 100;
            txtJobLabel.Text = $"{p.Completed}/{p.Total}  {p.CurrentAsset}";
        });

        try
        {
            var pipeline = new ModernizePipeline(settings);
            var report = await Task.Run(() => Directory.Exists(source)
                ? pipeline.RunOnDirectory(source, output, progress, _job.Token)
                : pipeline.RunOnArchive(source, output, progress, _job.Token), _job.Token);

            txtModernResult.Text =
                $"{report.Textures.Count} converted · {report.Skipped.Count} skipped · {report.Failures.Count} failed\n"
                + $"manifest: {Path.Combine(output, "materials.json")}";
            modernResultCard.Visibility = Visibility.Visible;

            if (txtUnrealSource.Text.Length == 0) txtUnrealSource.Text = output;
            SetStatus($"Modernized {report.Textures.Count} texture(s)");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Modernization cancelled");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Modernization failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Modernization failed");
        }
        finally
        {
            EndJob();
        }
    }

    private void OnCancelJob(object sender, RoutedEventArgs e) => _job?.Cancel();

    private void BeginJob(string label)
    {
        btnRunModernize.IsEnabled = false;
        btnCancelModernize.IsEnabled = true;
        jobProgress.Value = 0;
        jobProgress.Visibility = Visibility.Visible;
        SetStatus(label + "…", busy: true);
    }

    private void EndJob()
    {
        btnRunModernize.IsEnabled = true;
        btnCancelModernize.IsEnabled = false;
        jobProgress.Visibility = Visibility.Collapsed;
        txtJobLabel.Text = string.Empty;
        _job?.Dispose();
        _job = null;
    }

    // ===================================================================== unreal

    private void OnPickUnrealSource(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Folder containing materials.json" };
        if (dialog.ShowDialog(this) == true) txtUnrealSource.Text = dialog.FolderName;
    }

    private void OnPickUnrealOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Where to write the Unreal import package" };
        if (dialog.ShowDialog(this) == true) txtUnrealOutput.Text = dialog.FolderName;
    }

    private void OnRunUnrealExport(object sender, RoutedEventArgs e)
    {
        string source = txtUnrealSource.Text.Trim();
        string output = txtUnrealOutput.Text.Trim();
        string manifestPath = Path.Combine(source, "materials.json");

        if (!File.Exists(manifestPath))
        {
            MessageBox.Show(this, $"No materials.json in '{source}'. Run a modernization pass first.",
                "Nothing to export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (output.Length == 0)
        {
            MessageBox.Show(this, "Pick an output folder first.", "No output folder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var report = JsonSerializer.Deserialize<ModernizeReport>(File.ReadAllText(manifestPath), Core.GameStudioJson.Options)
                ?? throw new InvalidDataException("materials.json could not be read.");

            var result = UnrealExporter.Export(report, output,
                new UnrealExportSettings { ContentRoot = txtContentRoot.Text.Trim() });

            _lastUnrealOutput = output;
            txtUnrealResult.Text = $"{result.MaterialCount} materials · {result.TextureCount} textures\n\n"
                + $"In the Unreal Editor, run:\npy \"{result.ScriptPath}\"";
            unrealResultCard.Visibility = Visibility.Visible;
            SetStatus($"Wrote Unreal import package for {result.MaterialCount} material(s)");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenUnrealFolder(object sender, RoutedEventArgs e)
    {
        if (_lastUnrealOutput is null || !Directory.Exists(_lastUnrealOutput)) return;
        Process.Start(new ProcessStartInfo(_lastUnrealOutput) { UseShellExecute = true });
    }

    // ===================================================================== mcp

    private void OnMcpBadgeClicked(object sender, MouseButtonEventArgs e) => navMcp.IsChecked = true;

    private void OnPickMcpRoot(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Workspace root the MCP server may access" };
        if (dialog.ShowDialog(this) != true) return;
        txtMcpRoot.Text = dialog.FolderName;
        UpdateMcpConfigSnippet();
    }

    private void OnCopyMcpConfig(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(txtMcpConfig.Text);
        SetStatus("MCP client configuration copied");
    }

    private void OnStartMcp(object sender, RoutedEventArgs e)
    {
        string root = txtMcpRoot.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Pick a workspace root first — the server confines every path to it.",
                "No workspace root", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string exe = Path.Combine(AppContext.BaseDirectory, "GameStudio.Mcp.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show(this, $"GameStudio.Mcp.exe was not found next to the app:\n{exe}",
                "Server not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _mcpProcess = Process.Start(new ProcessStartInfo(exe)
            {
                ArgumentList = { "--root", root },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            btnStartMcp.IsEnabled = false;
            btnStopMcp.IsEnabled = true;
            SetStatus("MCP server started");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "Could not start the MCP server", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnStopMcp(object sender, RoutedEventArgs e)
    {
        StopMcpProcess();
        btnStartMcp.IsEnabled = true;
        btnStopMcp.IsEnabled = false;
        SetStatus("MCP server stopped");
    }

    private void StopMcpProcess()
    {
        if (_mcpProcess is null) return;
        try
        {
            if (!_mcpProcess.HasExited) _mcpProcess.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already gone */ }
        _mcpProcess.Dispose();
        _mcpProcess = null;
    }

    /// <summary>
    /// Polls the server's heartbeat file. There is no connection to inspect — MCP talks over stdio
    /// to whichever client launched the server — so a periodically refreshed file is the simplest
    /// reliable cross-process signal.
    /// </summary>
    private void UpdateMcpStatus()
    {
        try
        {
            if (!File.Exists(McpHeartbeatPath))
            {
                SetMcpStatus(false, "not running");
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(McpHeartbeatPath));
            var stamped = DateTime.Parse(document.RootElement.GetProperty("utc").GetString()!).ToUniversalTime();

            if (DateTime.UtcNow - stamped >= HeartbeatStaleAfter)
            {
                SetMcpStatus(false, "stale (not responding)");
                return;
            }

            string mode = document.RootElement.TryGetProperty("mode", out var m) ? m.GetString() ?? "none" : "none";
            SetMcpStatus(true, mode != "none" ? $"online ({mode})" : "online");
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or KeyNotFoundException)
        {
            SetMcpStatus(false, "not running");
        }
    }

    private void SetMcpStatus(bool online, string text)
    {
        var brush = (Brush)FindResource(online ? "SuccessBrush" : "TextMutedBrush");
        txtMcpStatus.Text = "MCP: " + text;
        mcpStatusDot.Fill = brush;
        txtMcpPanelStatus.Text = char.ToUpperInvariant(text[0]) + text[1..];
        mcpPanelDot.Fill = brush;
    }
}
