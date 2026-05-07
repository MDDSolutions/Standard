using FileRelay.Core;

namespace FileRelay.TestClient;

public partial class MainForm : Form
{
    // All shared state is static so TransferForm instances can reach it without
    // needing to pass a MainForm reference everywhere.
    internal static BandwidthLimiter? SharedThrottle;
    internal static int GlobalActiveCount;            // Interlocked
    internal static int NextTransferNumber;           // Interlocked, 1-based
    internal static readonly Dictionary<string, double> GlobalRates    = new();
    internal static readonly object                     GlobalRatesLock = new();
    internal static readonly object                     ThrottleLock    = new();

    public MainForm()
    {
        InitializeComponent();
    }

    private void btnNewTransfer_Click(object sender, EventArgs e)
    {
        new TransferForm(this).Show();
    }

    // Called by TransferForm when an upload starts.
    // Returns the throttle to use (may be null if unlimited).
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

    // Called by TransferForm when an upload ends (success, cancel, or error).
    internal void EndUpload()
    {
        Interlocked.Decrement(ref GlobalActiveCount);
        if (InvokeRequired) Invoke(UpdateControlState);
        else UpdateControlState();
    }

    // Called on each progress tick from any TransferForm.
    internal void ReportRate(string key, double rateMBps)
    {
        lock (GlobalRatesLock) GlobalRates[key] = rateMBps;
        RefreshOverallRate();
    }

    // Called when a transfer ends so its rate is removed from the aggregate.
    internal void ClearRate(string key)
    {
        lock (GlobalRatesLock) GlobalRates.Remove(key);
        RefreshOverallRate();
    }

    // Returns a snapshot of the current connection settings.
    internal (string ServerUrl, int ParallelConnections, double ThrottleMBps) GetSettings()
        => (txtServerUrl.Text, (int)nudParallel.Value, (double)nudBandwidth.Value);

    private void RefreshOverallRate()
    {
        if (InvokeRequired) { Invoke(RefreshOverallRate); return; }
        double total;
        lock (GlobalRatesLock) total = GlobalRates.Values.Sum();
        lblOverall.Text = total > 0 ? $"Overall: {total:F1} MB/s" : "";
    }

    private void UpdateControlState()
    {
        var active = Interlocked.CompareExchange(ref GlobalActiveCount, 0, 0) > 0;
        txtServerUrl.Enabled = !active;
        nudParallel.Enabled  = !active;
        nudBandwidth.Enabled = !active;
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
}
