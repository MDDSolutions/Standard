using FileRelay.Client;
using FileRelay.Core;

namespace FileRelay.TestClient;

public partial class MainForm : Form
{
    internal static BandwidthLimiter? SharedThrottle;
    internal static int GlobalActiveCount;            // Interlocked
    internal static int NextTransferNumber;           // Interlocked, 1-based
    internal static readonly Dictionary<string, double> GlobalRates = new();
    internal static readonly object GlobalRatesLock = new();
    internal static readonly object ThrottleLock = new();

    private readonly ClientSettings _settings;
    private readonly ToolTip _tip = new();

    public MainForm()
    {
        InitializeComponent();
        _settings = ClientSettings.Load();
        ApplySettings();
        WireSettingsEvents();
    }

    private void ApplySettings()
    {
        txtServerUrl.Text              = _settings.ServerUrl;
        txtAppId.Text                  = _settings.AppId;
        txtApiKey.Text                 = _settings.SeedKey;
        nudParallel.Value              = Math.Clamp(_settings.ParallelConnections, (int)nudParallel.Minimum, (int)nudParallel.Maximum);
        nudBandwidth.Value             = (decimal)Math.Clamp(_settings.ThrottleMBps, (double)nudBandwidth.Minimum, (double)nudBandwidth.Maximum);
        chkAllowUntrustedCert.Checked  = _settings.AllowUntrustedCert;
    }

    private void WireSettingsEvents()
    {
        txtServerUrl.Validated += (_, _) => { _settings.ServerUrl          = txtServerUrl.Text;         _settings.Save(); };
        txtAppId.Validated     += (_, _) => { _settings.AppId              = txtAppId.Text;              _settings.Save(); };
        txtApiKey.Validated    += (_, _) => { _settings.SeedKey             = txtApiKey.Text;             _settings.Save(); };
        nudParallel.Validated  += (_, _) => { _settings.ParallelConnections = (int)nudParallel.Value;   _settings.Save(); };
        nudBandwidth.Validated += (_, _) => { _settings.ThrottleMBps        = (double)nudBandwidth.Value; _settings.Save(); };
    }

    private void chkAllowUntrustedCert_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.AllowUntrustedCert = chkAllowUntrustedCert.Checked;
        _settings.Save();
    }

    private void btnNewTransfer_Click(object sender, EventArgs e)
    {
        new TransferForm(this).Show();
    }

    internal BandwidthLimiter? BeginUpload(double throttleMBps, int parallelConnections)
    {
        lock (ThrottleLock)
        {
            if (Interlocked.CompareExchange(ref GlobalActiveCount, 0, 0) == 0)
            {
                SharedThrottle?.Dispose();
                SharedThrottle = throttleMBps > 0
                    ? new BandwidthLimiter(throttleMBps, parallelConnections)
                    : null;
            }
        }
        Interlocked.Increment(ref GlobalActiveCount);
        UpdateControlState();
        return SharedThrottle;
    }

    internal void EndUpload()
    {
        Interlocked.Decrement(ref GlobalActiveCount);
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

    internal (string ServerUrl, int ParallelConnections, double ThrottleMBps, string? AppId, string? ApiKey, bool AllowUntrustedCert) GetSettings()
    {
        var appId = txtAppId.Text.Trim();
        var key   = txtApiKey.Text.Trim();
        return (txtServerUrl.Text, (int)nudParallel.Value, (double)nudBandwidth.Value,
                appId.Length > 0 ? appId : null,
                key.Length   > 0 ? key   : null,
                chkAllowUntrustedCert.Checked);
    }

    private void SetStatus(string text)
    {
        lblOverall.Text = text;
        _tip.SetToolTip(lblOverall, text);
    }

    private void RefreshOverallRate()
    {
        if (InvokeRequired) { Invoke(RefreshOverallRate); return; }
        double total;
        lock (GlobalRatesLock) total = GlobalRates.Values.Sum();
        SetStatus(total > 0 ? $"Overall: {total:F1} MB/s" : "");
    }

    private void UpdateControlState()
    {
        var active = Interlocked.CompareExchange(ref GlobalActiveCount, 0, 0) > 0;
        txtServerUrl.Enabled          = !active;
        txtAppId.Enabled              = !active;
        txtApiKey.Enabled             = !active;
        nudParallel.Enabled           = !active;
        nudBandwidth.Enabled          = !active;
        chkAllowUntrustedCert.Enabled = !active;
        btnTest.Enabled               = !active;
        btnRotateKey.Enabled          = !active;
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.UserClosing) return;
        if (Interlocked.CompareExchange(ref GlobalActiveCount, 0, 0) > 0)
        {
            var r = MessageBox.Show("Transfers are in progress. Exit anyway?", "FileRelay",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.No) { e.Cancel = true; return; }
        }
        SharedThrottle?.Dispose();
    }

    private async void btnRotateKey_Click(object sender, EventArgs e)
    {
        var (serverUrl, _, _, appId, seedKey, allowUntrustedCert) = GetSettings();
        btnRotateKey.Enabled = false;
        SetStatus("Rotating key...");
        try
        {
            using var client = new FileRelayClient(new Uri(serverUrl), appId: appId, apiKey: seedKey,
                allowUntrustedCertificate: allowUntrustedCert);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var newKey = await client.RotateKeyAsync(cts.Token);
            _settings.SeedKey = newKey;
            _settings.Save();
            txtApiKey.Text = newKey;
            SetStatus("Key rotated.");
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            SetStatus($"Rotate failed: {msg}");
        }
        finally
        {
            btnRotateKey.Enabled = true;
        }
    }

    private async void btnTest_Click(object sender, EventArgs e)
    {
        var (serverUrl, _, _, appId, apiKey, allowUntrustedCert) = GetSettings();
        btnTest.Enabled = false;
        SetStatus("Testing...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var info = await FileRelayClient.PingAsync(new Uri(serverUrl), appId, apiKey, cts.Token, allowUntrustedCert);
            var build = info.BuildTime.HasValue ? info.BuildTime.Value.ToString("yyyy-MM-dd HH:mm") : "unknown";
            var ver   = info.AssemblyVersion ?? "?";
            SetStatus($"Server build: {build}  v{ver}");
        }
        catch (Exception ex)
        {
            var msg = ex is OperationCanceledException ? "Timed out" : ex.Message;
            SetStatus($"Connection failed: {msg}");
        }
        finally
        {
            btnTest.Enabled = true;
        }
    }
}
