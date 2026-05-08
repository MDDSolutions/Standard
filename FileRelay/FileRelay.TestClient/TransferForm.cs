using FileRelay.Client;
using FileRelay.Core.Models;

namespace FileRelay.TestClient;

public partial class TransferForm : Form
{
    private readonly MainForm _hub;
    private CancellationTokenSource? _cts;
    private UploadProgress? _lastProgress;

    public TransferForm(MainForm hub)
    {
        _hub = hub;
        InitializeComponent();
    }

    private void btnBrowse_Click(object sender, EventArgs e)
    {
        if (ofd.ShowDialog() == DialogResult.OK)
        {
            txtFilePath.Text = ofd.FileName;
            Text = $"Transfer — {Path.GetFileName(ofd.FileName)}";
        }
    }

    private async void btnStart_Click(object sender, EventArgs e)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            return;
        }

        if (string.IsNullOrWhiteSpace(txtFilePath.Text)) return;

        var filePath = txtFilePath.Text;
        var (serverUrl, parallelConnections, throttleMBps, apiKey, allowUntrustedCert) = _hub.GetSettings();

        var throttle    = _hub.BeginUpload(throttleMBps, parallelConnections);
        var transferNum = Interlocked.Increment(ref MainForm.NextTransferNumber);
        var key         = $"T{transferNum}";

        _cts = new CancellationTokenSource();
        SetUploading(true);

        try
        {
            var file = new FileInfo(filePath);
            using var client = new FileRelayClient(new Uri(serverUrl), apiKey: apiKey, allowUntrustedCertificate: allowUntrustedCert);

            var maxRetries = 5;
            await client.UploadFileAsync(file, new UploadOptions
            {
                ParallelConnections = parallelConnections,
                Throttle            = throttle,
                MaxRetries          = maxRetries,
                OnNegotiated        = n  => OnNegotiated(file, n, key),
                OnProgress          = p  => OnProgress(key, p),
                OnRetry             = (ex, attempt, delay) =>
                {
                    var reason = ex is OperationCanceledException ? "Connection timed out" : ex.Message;
                    AppendLog($"[{key}] {reason} — retry {attempt}/{maxRetries} in {(int)delay.TotalSeconds}s");
                },
            }, _cts.Token);

            AppendLog($"[{key}] Complete — {FormatBytes(file.Length)} transferred.");
        }
        catch (OperationCanceledException)
        {
            var chunkInfo = _lastProgress != null
                ? $" ({_lastProgress.ChunksDone}/{_lastProgress.ChunksTotal} chunks confirmed)"
                : "";
            AppendLog($"[{key}] Cancelled.{chunkInfo}");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendLog($"[{key}] Auth failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Unauthorized", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"[{key}] Error: {ex.Message}");
            MessageBox.Show($"Upload failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _hub.EndUpload();
            _hub.ClearRate(key);
            _cts?.Dispose();
            _cts = null;
            _lastProgress = null;
            SetUploading(false);
        }
    }

    private void OnNegotiated(FileInfo file, TransferNegotiateResponse negotiate, string key)
    {
        var needed  = negotiate.ChunksNeeded.Count;
        var total   = negotiate.TotalChunks;
        var chunkMB = negotiate.ChunkSizeMB;

        var msg = needed == total
            ? $"[{key}] Uploading {file.Name} — {total} chunks × {chunkMB} MB ({FormatBytes(file.Length)})"
            : $"[{key}] Resuming {file.Name} — {needed}/{total} chunks remaining ({FormatBytes(file.Length)})";

        AppendLog(msg);
    }

    private void OnProgress(string key, UploadProgress p)
    {
        _lastProgress = p;
        _hub.ReportRate(key, p.TransferRateMBps);

        if (InvokeRequired) Invoke(() => RenderProgress(key, p));
        else RenderProgress(key, p);
    }

    private void RenderProgress(string key, UploadProgress p)
    {
        var rate = p.TransferRateMBps > 0 ? $"  {p.TransferRateMBps:F1} MB/s" : "";
        var eta  = p.EstimatedRemaining.HasValue ? $"  ETA {FormatEta(p.EstimatedRemaining.Value)}" : "";
        var line = $"[{key}]  {FormatBytes(p.BytesSent)} / {FormatBytes(p.BytesTotal)}  ({p.Percent:F1}%){rate}{eta}";
        UpdateLastProgressLine(key, line);
    }

    private void UpdateLastProgressLine(string key, string newLine)
    {
        var lines = rtbLog.Lines;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var l = lines[i];
            if (l.StartsWith($"[{key}]") && (l.Contains('%') || l.Contains("MB/s") || l.Contains("ETA")))
            {
                var start  = rtbLog.GetFirstCharIndexFromLine(i);
                var length = i + 1 < lines.Length
                    ? rtbLog.GetFirstCharIndexFromLine(i + 1) - start - 1
                    : rtbLog.TextLength - start;
                rtbLog.Select(start, length);
                rtbLog.SelectedText    = newLine;
                rtbLog.SelectionLength = 0;
                return;
            }
        }
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

    private void SetUploading(bool uploading)
    {
        if (InvokeRequired) { Invoke(() => SetUploading(uploading)); return; }
        btnStart.Text     = uploading ? "Cancel" : "Upload";
        btnBrowse.Enabled = !uploading;
    }

    private void TransferForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _cts?.Cancel();
    }

    private static string FormatEta(TimeSpan t)
    {
        if (t.TotalHours   >= 1) return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
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
