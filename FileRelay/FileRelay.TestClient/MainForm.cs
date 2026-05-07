using FileRelay.Client;
using FileRelay.Core;
using FileRelay.Core.Models;

namespace FileRelay.TestClient;

public partial class MainForm : Form
{
    // All active uploads: transferId (or temp key) → bytes/sec sample from last progress event
    private readonly Dictionary<string, double> _activeRates = new();
    private readonly object _ratesLock = new();

    // Shared throttle — rebuilt when bandwidth knob changes and an upload starts
    private BandwidthLimiter? _sharedThrottle;

    // Track in-flight CTS per upload so Cancel cancels all of them
    private readonly List<CancellationTokenSource> _activeCts = new();
    private readonly object _ctsLock = new();

    public MainForm()
    {
        InitializeComponent();
    }

    private void btnBrowse_Click(object sender, EventArgs e)
    {
        if (ofd.ShowDialog() == DialogResult.OK)
            txtFilePath.Text = ofd.FileName;
    }

    private async void btnStart_Click(object sender, EventArgs e)
    {
        bool anyActive;
        lock (_ctsLock) anyActive = _activeCts.Count > 0;

        if (anyActive)
        {
            // Cancel all active transfers
            lock (_ctsLock)
            {
                foreach (var c in _activeCts) c.Cancel();
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(txtFilePath.Text)) return;

        var filePath = txtFilePath.Text;
        var serverUrl = txtServerUrl.Text;
        var parallelConnections = (int)nudParallel.Value;
        var throttleMBps = (double)nudBandwidth.Value;

        // Rebuild shared throttle if bandwidth setting changed
        if (throttleMBps > 0)
        {
            _sharedThrottle?.Dispose();
            _sharedThrottle = new BandwidthLimiter(throttleMBps, parallelConnections);
        }
        else
        {
            _sharedThrottle?.Dispose();
            _sharedThrottle = null;
        }

        var cts = new CancellationTokenSource();
        lock (_ctsLock) _activeCts.Add(cts);

        SetUploading(true);

        var transferKey = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var file = new FileInfo(filePath);
            using var client = new FileRelayClient(new Uri(serverUrl), apiKey: "test-key-abc123");

            var uploadOptions = new UploadOptions
            {
                ParallelConnections = parallelConnections,
                Throttle            = _sharedThrottle,
                OnNegotiated        = negotiate => OnNegotiated(file, negotiate, transferKey),
                OnProgress          = p => OnProgress(transferKey, p),
            };

            await client.UploadFileAsync(file, uploadOptions, cts.Token);

            AppendLog($"[{transferKey}] Complete — {FormatBytes(file.Length)} transferred.");
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{transferKey}] Cancelled.");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendLog($"[{transferKey}] Auth failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Unauthorized", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"[{transferKey}] Error: {ex.Message}");
            MessageBox.Show($"Upload failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            lock (_ctsLock) _activeCts.Remove(cts);
            cts.Dispose();

            RemoveRate(transferKey);

            bool stillActive;
            lock (_ctsLock) stillActive = _activeCts.Count > 0;
            if (!stillActive) SetUploading(false);
        }
    }

    private void OnNegotiated(FileInfo file, TransferNegotiateResponse negotiate, string transferKey)
    {
        var chunkMB = negotiate.ChunkSizeMB;
        var needed = negotiate.ChunksNeeded.Count;
        var total = negotiate.TotalChunks;

        string msg;
        if (needed == total)
            msg = $"[{transferKey}] Uploading {file.Name} — {total} chunks × {chunkMB} MB ({FormatBytes(file.Length)})";
        else
            msg = $"[{transferKey}] Resuming {file.Name} — {needed}/{total} chunks remaining ({FormatBytes(file.Length)})";

        AppendLog(msg);
    }

    private void OnProgress(string transferKey, UploadProgress p)
    {
        UpdateRate(transferKey, p.TransferRateMBps);

        if (InvokeRequired)
        {
            Invoke(() => RenderProgress(transferKey, p));
            return;
        }
        RenderProgress(transferKey, p);
    }

    private void RenderProgress(string transferKey, UploadProgress p)
    {
        var rate = p.TransferRateMBps > 0 ? $"  {p.TransferRateMBps:F1} MB/s" : "";
        var eta  = p.EstimatedRemaining.HasValue ? $"  ETA {FormatEta(p.EstimatedRemaining.Value)}" : "";
        var line = $"[{transferKey}]  {FormatBytes(p.BytesSent)} / {FormatBytes(p.BytesTotal)}  ({p.Percent:F1}%){rate}{eta}";
        UpdateLastProgressLine(transferKey, line);
        RefreshOverallRate();
    }

    // Replaces the last progress line for this key, or appends if none exists
    private void UpdateLastProgressLine(string transferKey, string newLine)
    {
        var prefix = $"[{transferKey}]";
        var lines = rtbLog.Lines;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Contains(prefix) && lines[i].Contains("MB/s") || lines[i].Contains(prefix) && lines[i].Contains("ETA") || lines[i].Contains(prefix) && lines[i].Contains("%"))
            {
                // Replace this line in-place
                var start = rtbLog.GetFirstCharIndexFromLine(i);
                var length = (i + 1 < lines.Length)
                    ? rtbLog.GetFirstCharIndexFromLine(i + 1) - start - 1
                    : rtbLog.TextLength - start;
                rtbLog.Select(start, length);
                rtbLog.SelectedText = newLine;
                rtbLog.SelectionLength = 0;
                return;
            }
        }
        // No existing progress line — append a new one
        AppendLogRaw(newLine);
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        if (InvokeRequired) Invoke(() => AppendLogRaw(line));
        else AppendLogRaw(line);
    }

    private void AppendLogRaw(string line)
    {
        if (rtbLog.TextLength > 0) rtbLog.AppendText(Environment.NewLine);
        rtbLog.AppendText(line);
        rtbLog.ScrollToCaret();
    }

    private void UpdateRate(string key, double rateMBps)
    {
        lock (_ratesLock) _activeRates[key] = rateMBps;
    }

    private void RemoveRate(string key)
    {
        lock (_ratesLock) _activeRates.Remove(key);
        if (InvokeRequired) Invoke(RefreshOverallRate);
        else RefreshOverallRate();
    }

    private void RefreshOverallRate()
    {
        double total;
        lock (_ratesLock) total = _activeRates.Values.Sum();
        lblOverall.Text = total > 0 ? $"Overall: {total:F1} MB/s" : "";
    }

    private void SetUploading(bool uploading)
    {
        if (InvokeRequired) { Invoke(() => SetUploading(uploading)); return; }
        btnStart.Text = uploading ? "Cancel" : "Upload";
        nudParallel.Enabled  = !uploading;
        nudBandwidth.Enabled = !uploading;
        txtServerUrl.Enabled = !uploading;
        btnBrowse.Enabled    = !uploading;
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        lock (_ctsLock) foreach (var cts in _activeCts) cts.Cancel();
        _sharedThrottle?.Dispose();
    }

    private static string FormatEta(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";
        return $"{t.Seconds}s";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024          => $"{bytes / 1024.0:F1} KB",
        _                => $"{bytes} B"
    };
}
