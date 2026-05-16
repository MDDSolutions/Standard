using FileRelay.Client;
using FileRelay.Core;
using MDDFoundation;
using System.Net.Http.Json;

namespace FileRelay.TestClient;

public partial class MainForm : Form
{
    internal static int NextTransferNumber;           // Interlocked, 1-based
    internal static readonly Dictionary<string, double> GlobalRates = new();
    internal static readonly object GlobalRatesLock = new();

    private readonly ClientSettings _settings;
    private FileRelayClient? _client;

    internal FileRelayClient Client => _client ??= CreateClient();

    public MainForm()
    {
        InitializeComponent();
        _settings = ClientSettings.Load();
        ApplySettings();
        WireSettingsEvents();
    }

    private FileRelayClient CreateClient()
    {
        _client?.Dispose();
        var appId = _settings.AppId.Length > 0 ? _settings.AppId : null;
        var apiKey = _settings.SeedKey.Length > 0 ? _settings.SeedKey : null;
        _client = new FileRelayClient(new Uri(_settings.ServerUrl), appId: appId, apiKey: apiKey,
            allowUntrustedCertificate: _settings.AllowUntrustedCert)
        {
            ParallelConnections = _settings.ParallelConnections,
            ThrottleMBps = _settings.ThrottleMBps,
        };
        return _client;
    }

    private void InvalidateClient()
    {
        _client?.Dispose();
        _client = null;
    }

    private void ApplySettings()
    {
        txtServerUrl.Text = _settings.ServerUrl;
        txtAppId.Text = _settings.AppId;
        txtApiKey.Text = _settings.SeedKey;
        nudParallel.Value = Math.Clamp(_settings.ParallelConnections, (int)nudParallel.Minimum, (int)nudParallel.Maximum);
        nudBandwidth.Value = (decimal)Math.Clamp(_settings.ThrottleMBps, (double)nudBandwidth.Minimum, (double)nudBandwidth.Maximum);
        chkAllowUntrustedCert.Checked = _settings.AllowUntrustedCert;
    }

    private void WireSettingsEvents()
    {
        // Connection-level changes require a new client.
        txtServerUrl.Validated += (_, _) => { _settings.ServerUrl = txtServerUrl.Text; _settings.Save(); InvalidateClient(); };
        txtAppId.Validated += (_, _) => { _settings.AppId = txtAppId.Text; _settings.Save(); InvalidateClient(); };
        txtApiKey.Validated += (_, _) => { _settings.SeedKey = txtApiKey.Text; _settings.Save(); InvalidateClient(); };

        // Performance settings can be updated on the existing client.
        nudParallel.Validated += (_, _) =>
        {
            _settings.ParallelConnections = (int)nudParallel.Value;
            _settings.Save();
            if (_client != null) _client.ParallelConnections = (int)nudParallel.Value;
        };
        nudBandwidth.Validated += (_, _) =>
        {
            _settings.ThrottleMBps = (double)nudBandwidth.Value;
            _settings.Save();
            if (_client != null) _client.ThrottleMBps = (double)nudBandwidth.Value;
        };
    }

    private void chkAllowUntrustedCert_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.AllowUntrustedCert = chkAllowUntrustedCert.Checked;
        _settings.Save();
        InvalidateClient();
    }

    private void btnNewTransfer_Click(object sender, EventArgs e)
    {
        new TransferForm(this).Show();
    }

    internal void BeginTransfer() => UpdateControlState();

    internal void EndTransfer()
    {
        if (InvokeRequired) Invoke(UpdateControlState);
        else UpdateControlState();
    }

    internal void ReportRate(string key, double rateMBps)
    {
        lock (GlobalRatesLock) GlobalRates[key] = rateMBps;
        RefreshOverallRate();
    }

    internal void ClearRate(string key)
    {
        lock (GlobalRatesLock) GlobalRates.Remove(key);
        RefreshOverallRate();
    }

    internal (string ServerUrl, string? AppId, string? ApiKey, bool AllowUntrustedCert) GetConnectionSettings()
    {
        var appId = txtAppId.Text.Trim();
        var apiKey = txtApiKey.Text.Trim();
        return (txtServerUrl.Text,
                appId.Length > 0 ? appId : null,
                apiKey.Length > 0 ? apiKey : null,
                chkAllowUntrustedCert.Checked);
    }

    private void AppendStatus(string text)
    {
        if (InvokeRequired) { Invoke(() => AppendStatus(text)); return; }
        if (rtbStatus.TextLength > 0) rtbStatus.AppendText(Environment.NewLine);
        rtbStatus.AppendText($"{DateTime.Now:HH:mm:ss}  {text}");
        rtbStatus.ScrollToCaret();
    }

    private void RefreshOverallRate()
    {
        if (InvokeRequired) { Invoke(RefreshOverallRate); return; }
        double total;
        lock (GlobalRatesLock) total = GlobalRates.Values.Sum();
        lblRate.Text = total > 0 ? $"Overall: {total:F1} MB/s" : "";
    }

    private void UpdateControlState()
    {
        var active = _client?.ActiveUploads > 0;
        txtServerUrl.Enabled = !active;
        txtAppId.Enabled = !active;
        txtApiKey.Enabled = !active;
        nudParallel.Enabled = !active;
        nudBandwidth.Enabled = !active;
        chkAllowUntrustedCert.Enabled = !active;
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.UserClosing) return;
        if (_client?.ActiveUploads > 0)
        {
            var r = MessageBox.Show("Transfers are in progress. Exit anyway?", "FileRelay",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.No) { e.Cancel = true; return; }
        }
        _client?.Dispose();
    }

    private async void btnExpireGrace_Click(object sender, EventArgs e)
    {
        var (serverUrl, _, _, allowUntrustedCert) = GetConnectionSettings();
        btnExpireGrace.Enabled = false;
        AppendStatus("Expiring grace period...");
        try
        {
            var handler = new HttpClientHandler();
            if (allowUntrustedCert)
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            using var http = new HttpClient(handler);
            var resp = await http.PostAsync(serverUrl.TrimEnd('/') + "/test/expire-grace", null);
            var body = await resp.Content.ReadFromJsonAsync<ExpireGraceResponse>();
            AppendStatus(body?.Message ?? (resp.IsSuccessStatusCode ? "Done." : $"Error {(int)resp.StatusCode}"));
        }
        catch (Exception ex)
        {
            AppendStatus($"Expire grace failed: {ex.Message}");
        }
        finally
        {
            btnExpireGrace.Enabled = true;
        }
    }

    private async void btnRotateKey_Click(object sender, EventArgs e)
    {
        btnRotateKey.Enabled = false;
        AppendStatus("Rotating key...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var newKey = await Client.RotateKeyAsync(cts.Token);
            _settings.SeedKey = newKey;
            _settings.Save();
            txtApiKey.Text = newKey;
            AppendStatus("Key rotated.");
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            AppendStatus($"Rotate failed: {msg}");
        }
        finally
        {
            btnRotateKey.Enabled = true;
        }
    }

    private async void btnTest_Click(object sender, EventArgs e)
    {
        var (serverUrl, appId, apiKey, allowUntrustedCert) = GetConnectionSettings();
        btnTest.Enabled = false;
        AppendStatus("Testing...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var info = await FileRelayClient.PingAsync(new Uri(serverUrl), appId, apiKey, cts.Token, allowUntrustedCert);
            var build = info.BuildTime.HasValue ? info.BuildTime.Value.ToString("yyyy-MM-dd HH:mm") : "unknown";
            var ver = info.AssemblyVersion ?? "?";
            AppendStatus($"Server build: {build}  v{ver}");
        }
        catch (Exception ex)
        {
            var msg = ex is OperationCanceledException ? "Timed out" : ex.Message;
            AppendStatus($"Connection failed: {msg}");
        }
        finally
        {
            btnTest.Enabled = true;
        }
    }

    private async void btnTestCopy_Click(object sender, EventArgs e)
    {
        var pqChunked = new FileCopyProgress
        {
            FileName = @"hhs_10148_x264.mp4",
            Callback = (x) => AppendStatus(x.ToString()),
            ProgressReportInterval = TimeSpan.FromSeconds(10),
            SourceFile = new FileInfo(@"C:\Capture\hhs_10148_x264.mp4"),
            Destinations =
            [ new FileInfo(@"\\mdd-qnap02\Other\Temp\hhs_10148_x264.mp4"),
              new FileInfo(@"\\mdd-qnap04\Other\Temp\hhs_10148_x264.mp4") ],
            Overwrite = true,
            Token = CancellationToken.None,
            MoveFile = false,
            HashMode = FileCopyHashMode.NoHash,
            Resumable = true,
            BufferSize = 1024 * 1024,
            ChunkSizeBytes = 50L * 1024 * 1024,
            ParallelChunks = 4,
            VerifyChunkWrites = false,
            ChunkStateFlushInterval = TimeSpan.FromSeconds(10),
            FullFlushChunkState = false,
            OperationDuring = "Chunked copying (QNAP02+QNAP04)",
            OperationComplete = "Chunked copy to QNAP02+QNAP04 complete"
        };

        await FileCopyExtensions.CopyToAsyncSequentialReadChunked(pqChunked);
    }
}

record ExpireGraceResponse(string? Message);
