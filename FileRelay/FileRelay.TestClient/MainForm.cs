using FileRelay.Client;

namespace FileRelay.TestClient;

public partial class MainForm : Form
{
    public MainForm()
    {
        InitializeComponent();
    }

    private void btnBrowse_Click(object sender, EventArgs e)
    {
        if (ofd.ShowDialog() == DialogResult.OK)
            txtFilePath.Text = ofd.FileName;
    }

    private async void btnUpload_Click(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtFilePath.Text)) return;

        btnUpload.Enabled = false;
        pbProgress.Value = 0;
        lblStatus.Text = "Uploading file...";

        try
        {
            var file = new FileInfo(txtFilePath.Text);
            var started = DateTime.UtcNow;
            using var client = new FileRelayClient(new Uri(txtServerUrl.Text));
            await client.UploadFileAsync(
                file,
                new UploadOptions
                {
                    ParallelConnections = 4,
                    OnProgress = UpdateProgress
                });

            var elapsed = DateTime.UtcNow - started;
            var rateMBps = elapsed.TotalSeconds > 0 ? file.Length / 1_048_576.0 / elapsed.TotalSeconds : 0;
            pbProgress.Value = 100;
            lblStatus.Text = $"Complete in {FormatEta(elapsed)} at {rateMBps:F1} MB/s";
        }
        catch (Exception ex)
        {
            lblStatus.Text = $"Error: {ex.Message}";
            MessageBox.Show($"Upload failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            btnUpload.Enabled = true;
        }
    }

    private void UpdateProgress(UploadProgress p)
    {
        if (InvokeRequired)
        {
            Invoke(() => UpdateProgress(p));
            return;
        }

        pbProgress.Value = (int)Math.Min(p.Percent, 100);

        var rate = p.TransferRateMBps > 0 ? $"  {p.TransferRateMBps:F1} MB/s" : "";
        var eta  = p.EstimatedRemaining.HasValue ? $"  ETA {FormatEta(p.EstimatedRemaining.Value)}" : "";
        lblStatus.Text = $"{FormatBytes(p.BytesSent)} / {FormatBytes(p.BytesTotal)}{rate}{eta}";
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
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
