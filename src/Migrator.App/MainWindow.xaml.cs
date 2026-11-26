using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Migrator.Core.Abstractions;
using Migrator.Core.Config;
using Migrator.Core.Mapping;
using Migrator.Core.Migration;
using Migrator.Core.Models;
using Migrator.Optimizely;
using Migrator.Sitecore;
using Serilog;

namespace Migrator.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _logLines = [];
    private readonly ObservableCollection<MigrationItemResult> _results = [];
    private CancellationTokenSource? _cts;
    private MigrationRunResult? _lastRun;
    private readonly string _appRoot;

    public MainWindow()
    {
        InitializeComponent();
        _appRoot = AppContext.BaseDirectory;
        LogList.ItemsSource = _logLines;
        ResultsGrid.ItemsSource = _results;

        var candidates = new[]
        {
            Path.Combine(_appRoot, "Config", "mappings", "content-types.json"),
            Path.GetFullPath(Path.Combine(_appRoot, "..", "..", "..", "..", "Config", "mappings", "content-types.json")),
            Path.Combine(_appRoot, "Config", "mappings", "content-types.sample.json")
        };
        var defaultMapping = candidates.FirstOrDefault(File.Exists);
        if (defaultMapping is not null)
            MappingPath.Text = defaultMapping;

        AppendLog("Ready. Configure connections, load a 1:1 mapping file, then run a dry-run.");
        AnnounceSitecoreCredentials();
        AnnounceOptimizelyCredentials();
    }

    private void AnnounceSitecoreCredentials()
    {
        try
        {
            var credentials = SitecoreCredentialsLoader.Load(_appRoot);
            var user = string.IsNullOrWhiteSpace(credentials.Username) ? "(username empty)" : credentials.Username;
            SitecoreCredentialsSource.Text = $"Basic auth user '{user}' from {credentials.SourcePath}";
            AppendLog($"Sitecore credentials loaded for '{user}' from {credentials.SourcePath}.");
        }
        catch (Exception ex)
        {
            SitecoreCredentialsSource.Text = ex.Message;
            AppendLog(ex.Message);
        }
    }

    private void AnnounceOptimizelyCredentials()
    {
        try
        {
            var credentials = OptimizelyCredentialsLoader.Load(_appRoot);
            var idPreview = credentials.ClientId.Length <= 8
                ? credentials.ClientId
                : credentials.ClientId[..8] + "…";
            OptiCredentialsSource.Text = $"Client ID '{idPreview}' from {credentials.SourcePath}";
            AppendLog($"Optimizely credentials loaded (clientId '{idPreview}') from {credentials.SourcePath}.");
        }
        catch (Exception ex)
        {
            OptiCredentialsSource.Text = ex.Message;
            AppendLog(ex.Message);
        }
    }

    private SitecoreConnectionSettings ReadSitecoreSettings()
    {
        var authTag = (SitecoreAuthModeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString()
                      ?? "Basic";
        var authMode = Enum.TryParse<SitecoreAuthMode>(authTag, ignoreCase: true, out var parsed)
            ? parsed
            : SitecoreAuthMode.Basic;

        var transportTag = (SitecoreTransportCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString()
                           ?? "ItemService";
        var transport = Enum.TryParse<SitecoreTransport>(transportTag, ignoreCase: true, out var t)
            ? t
            : SitecoreTransport.ItemService;

        var credentials = SitecoreCredentialsLoader.Load(_appRoot);

        return new SitecoreConnectionSettings
        {
            BaseUrl = SitecoreBaseUrl.Text.Trim(),
            ApiKey = SitecoreApiKey.Text.Trim(),
            Database = SitecoreDatabase.Text.Trim(),
            Language = SitecoreLanguage.Text.Trim(),
            AuthMode = authMode,
            Transport = transport,
            Username = credentials.Username,
            Password = credentials.Password,
            IgnoreSslErrors = SitecoreIgnoreSsl.IsChecked == true
        };
    }

    private (HttpClient Http, ISitecoreClient Client) CreateSitecoreClient(SitecoreConnectionSettings settings)
    {
        var useLayout = settings.Transport != SitecoreTransport.ItemService;
        // Item Service and Layout Service both put sc_apikey on the query string.
        // Sending the same key as a header as well returns HTTP 400 on this CM.
        var http = SitecoreHttpFactory.Create(settings, TimeSpan.FromMinutes(5), includeApiKeyHeader: false);
        ISitecoreClient client = useLayout
            ? new SitecoreLayoutServiceClient(http, settings)
            : new SitecoreItemServiceClient(http, settings);
        return (http, client);
    }

    private OptimizelyConnectionSettings ReadOptiSettings()
    {
        var credentials = OptimizelyCredentialsLoader.Load(_appRoot);
        return new OptimizelyConnectionSettings
        {
            ApiBaseUrl = OptiApiBase.Text.Trim(),
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret,
            Locale = OptiLocale.Text.Trim(),
            Scope = OptiScope.Text.Trim()
        };
    }

    private PathSettings ReadPathSettings() => new()
    {
        SitecoreSourcePath = SitecoreSourcePath.Text.Trim(),
        OptimizelyTargetContainerKey = OptiContainerKey.Text.Trim(),
        OptimizelyTargetPathLabel = OptiTargetLabel.Text.Trim(),
        SingleItemOnly = SingleItemOnlyCheck.IsChecked == true
    };

    private async void TestSitecore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Testing Sitecore...");
            var settings = ReadSitecoreSettings();
            var (http, client) = CreateSitecoreClient(settings);
            using (http)
            {
            var result = await client.TestConnectionAsync();

            AppendLog(result.Success
                ? $"Sitecore OK: {result.Message}"
                : $"Sitecore FAILED: {result.Message}");
            if (!string.IsNullOrWhiteSpace(result.RequestUrl))
                AppendLog($"Request: {result.RequestUrl}");
            if (!string.IsNullOrWhiteSpace(result.ResponseSnippet))
                AppendLog($"Response: {result.ResponseSnippet}");

            StatusText.Text = result.Success ? "Sitecore OK" : "Sitecore failed";
            if (!result.Success)
            {
                MessageBox.Show(
                    result.Message +
                    (string.IsNullOrWhiteSpace(result.RequestUrl) ? string.Empty : $"\n\nURL:\n{result.RequestUrl}"),
                    "Sitecore connection failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            } // using http
        }
        catch (Exception ex)
        {
            AppendLog($"Sitecore test error: {ex.Message}");
            StatusText.Text = "Sitecore error";
            MessageBox.Show(ex.Message, "Sitecore connection error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void TestOptimizely_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Testing Optimizely...");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var client = new OptimizelyCmsClient(http, ReadOptiSettings());
            var ok = await client.TestConnectionAsync();
            AppendLog(ok ? "Optimizely OAuth OK." : "Optimizely OAuth FAILED.");
            StatusText.Text = ok ? "Optimizely OK" : "Optimizely failed";
        }
        catch (Exception ex)
        {
            AppendLog($"Optimizely test error: {ex.Message}");
            StatusText.Text = "Optimizely error";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BrowseMapping_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON mapping|*.json|All files|*.*",
            Title = "Select content type mapping file"
        };
        if (dlg.ShowDialog() == true)
            MappingPath.Text = dlg.FileName;
    }

    private void OpenSample_Click(object sender, RoutedEventArgs e)
    {
        var sample = MappingPath.Text;
        if (!File.Exists(sample))
        {
            MessageBox.Show("Mapping file not found. Browse to Config/mappings/content-types.sample.json",
                "Sample mapping", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(sample) { UseShellExecute = true });
    }

    private void ValidateMapping_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = MappingLoader.LoadFromFile(MappingPath.Text);
            var result = MappingValidator.Validate(config);
            var sb = new StringBuilder();
            sb.AppendLine(result.IsValid ? "VALID — 1:1 mapping structure OK." : "INVALID — fix errors before migrating.");
            foreach (var err in result.Errors)
                sb.AppendLine("ERROR: " + err);
            foreach (var warn in result.Warnings)
                sb.AppendLine("WARN: " + warn);
            sb.AppendLine($"Types: {config.Types.Count}");
            foreach (var t in config.Types)
                sb.AppendLine($"  • {t.SitecoreTemplateName} → {t.OptimizelyContentType} ({t.Fields.Count} fields)");

            MappingValidationOutput.Text = sb.ToString();
            AppendLog(result.IsValid ? "Mapping validation passed." : "Mapping validation failed.");
        }
        catch (Exception ex)
        {
            MappingValidationOutput.Text = "ERROR: " + ex.Message;
            AppendLog($"Mapping load error: {ex.Message}");
        }
    }

    private async void Migrate_Click(object sender, RoutedEventArgs e)
    {
        MappingConfig mapping;
        try
        {
            mapping = MappingLoader.LoadFromFile(MappingPath.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Mapping file", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var validation = MappingValidator.Validate(mapping);
        if (!validation.IsValid)
        {
            MessageBox.Show(string.Join(Environment.NewLine, validation.Errors),
                "Invalid mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dryRun = DryRunCheck.IsChecked == true;
        if (!dryRun)
        {
            var confirm = MessageBox.Show(
                "Dry run is OFF. This will CREATE/UPDATE content in Optimizely CMS SaaS.\n\nContinue?",
                "Confirm migration",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        _cts = new CancellationTokenSource();
        _results.Clear();
        SetBusy(true, dryRun ? "Dry run in progress..." : "Migrating...");
        MigrateButton.IsEnabled = false;
        CancelButton.IsEnabled = true;

        try
        {
            var sitecoreSettings = ReadSitecoreSettings();
            var (sitecoreHttp, sitecore) = CreateSitecoreClient(sitecoreSettings);
            using var _sitecoreHttp = sitecoreHttp;
            using var optiHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var opti = new OptimizelyCmsClient(optiHttp, ReadOptiSettings());
            var engine = new MigrationEngine(sitecore, opti);

            engine.Progress += (_, args) =>
            {
                Dispatcher.Invoke(() =>
                {
                    AppendLog(args.Message);
                    if (args.Total > 0)
                        ProgressBar.Value = Math.Min(100, args.Current * 100.0 / args.Total);
                    if (args.Item is not null)
                        _results.Add(args.Item);
                    StatusText.Text = args.Message;
                });
            };

            var idMapPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SitecoreToOptiMigrator",
                "idmap.db");

            var options = new MigrationOptions
            {
                Sitecore = ReadSitecoreSettings(),
                Optimizely = ReadOptiSettings(),
                Paths = ReadPathSettings(),
                Mapping = mapping,
                IdMapDatabasePath = idMapPath,
                DryRun = dryRun,
                ContinueOnError = ContinueOnErrorCheck.IsChecked == true
            };

            _lastRun = await engine.RunAsync(options, _cts.Token);

            AppendLog(
                $"Done. Success={_lastRun.Success} Created={_lastRun.Created} Updated={_lastRun.Updated} " +
                $"Skipped={_lastRun.Skipped} Failed={_lastRun.Failed} DryRun={_lastRun.DryRunCount}");
            StatusText.Text = _lastRun.Success ? "Completed" : "Completed with errors";
            ProgressBar.Value = 100;

            ShowRunSummary(_lastRun);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Migration cancelled.");
            StatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLog($"Migration error: {ex.Message}");
            StatusText.Text = "Error";
            MessageBox.Show(ex.Message, "Migration failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            MigrateButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling...";
    }

    private void ShowRunSummary(MigrationRunResult run)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"Created={run.Created}  Updated={run.Updated}  Skipped={run.Skipped}  Failed={run.Failed}  DryRun={run.DryRunCount}");

        if (run.ValidationErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Validation:");
            foreach (var err in run.ValidationErrors.Take(5))
                sb.AppendLine("• " + err);
        }

        var interesting = run.Items
            .Where(i => i.Status is MigrationItemStatus.Failed or MigrationItemStatus.Skipped or MigrationItemStatus.DryRun)
            .Take(8)
            .ToList();
        if (interesting.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Sample results:");
            foreach (var item in interesting)
                sb.AppendLine($"• [{item.Status}] {item.TemplateName}: {item.Message}");
        }

        if (run.Created == 0 && run.DryRunCount == 0 && run.Updated == 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                "Nothing was written. Check Sitecore path, mapping file (mvp_SitecoreProduct), " +
                "Opti Client ID/Secret, and that the container allows SitecoreProduct components.");
        }

        MessageBox.Show(
            sb.ToString(),
            run.Created > 0 || run.Updated > 0 ? "Migration finished" : "Migration wrote nothing",
            MessageBoxButton.OK,
            run.Failed > 0 || run.Created == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRun is null || _lastRun.Items.Count == 0)
        {
            MessageBox.Show("No migration results to export yet.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "CSV|*.csv",
            FileName = $"migration-report-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("Status,SitecoreId,Template,OptiType,OptiKey,Path,Message");
        foreach (var item in _lastRun.Items)
        {
            sb.AppendLine(string.Join(',',
                Csv(item.Status.ToString()),
                Csv(item.SitecoreId.ToString("D")),
                Csv(item.TemplateName),
                Csv(item.OptimizelyContentType),
                Csv(item.OptimizelyKey),
                Csv(item.SitecorePath),
                Csv(item.Message)));
        }

        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        AppendLog($"Report exported: {dlg.FileName}");
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        _logLines.Insert(0, line);
        Log.Information("{Message}", message);
        while (_logLines.Count > 500)
            _logLines.RemoveAt(_logLines.Count - 1);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        if (status is not null)
            StatusText.Text = status;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }
}
