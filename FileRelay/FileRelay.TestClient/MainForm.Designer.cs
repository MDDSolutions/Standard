namespace FileRelay.TestClient;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null)) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        pnlTop        = new Panel();
        lblServer     = new Label();
        txtServerUrl  = new TextBox();
        lblFile       = new Label();
        txtFilePath   = new TextBox();
        btnBrowse     = new Button();
        lblParallel   = new Label();
        nudParallel   = new NumericUpDown();
        lblBandwidth  = new Label();
        nudBandwidth  = new NumericUpDown();
        lblBwUnit     = new Label();
        btnStart      = new Button();
        lblOverall    = new Label();
        rtbLog        = new RichTextBox();
        ofd           = new OpenFileDialog();

        pnlTop.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)nudParallel).BeginInit();
        ((System.ComponentModel.ISupportInitialize)nudBandwidth).BeginInit();
        SuspendLayout();

        // pnlTop
        pnlTop.Dock    = DockStyle.Top;
        pnlTop.Height  = 112;
        pnlTop.Padding = new Padding(8, 8, 8, 4);
        pnlTop.Controls.Add(lblServer);
        pnlTop.Controls.Add(txtServerUrl);
        pnlTop.Controls.Add(lblFile);
        pnlTop.Controls.Add(txtFilePath);
        pnlTop.Controls.Add(btnBrowse);
        pnlTop.Controls.Add(lblParallel);
        pnlTop.Controls.Add(nudParallel);
        pnlTop.Controls.Add(lblBandwidth);
        pnlTop.Controls.Add(nudBandwidth);
        pnlTop.Controls.Add(lblBwUnit);
        pnlTop.Controls.Add(btnStart);
        pnlTop.Controls.Add(lblOverall);

        // row 0 — server url
        lblServer.Text     = "Server:";
        lblServer.Location = new Point(8, 14);
        lblServer.Size     = new Size(56, 23);
        lblServer.TextAlign = ContentAlignment.MiddleRight;

        txtServerUrl.Location = new Point(68, 11);
        txtServerUrl.Size     = new Size(730, 31);
        txtServerUrl.Text     = "http://localhost:61488/";
        txtServerUrl.Anchor   = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // row 1 — file picker
        lblFile.Text     = "File:";
        lblFile.Location = new Point(8, 50);
        lblFile.Size     = new Size(56, 23);
        lblFile.TextAlign = ContentAlignment.MiddleRight;

        txtFilePath.Location = new Point(68, 47);
        txtFilePath.Size     = new Size(630, 31);
        txtFilePath.ReadOnly = true;
        txtFilePath.Anchor   = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        btnBrowse.Text     = "Browse...";
        btnBrowse.Location = new Point(704, 44);
        btnBrowse.Size     = new Size(94, 34);
        btnBrowse.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowse.Click   += btnBrowse_Click;

        // row 2 — knobs + start + overall rate
        lblParallel.Text      = "Connections:";
        lblParallel.Location  = new Point(8, 86);
        lblParallel.Size      = new Size(88, 23);
        lblParallel.TextAlign = ContentAlignment.MiddleRight;

        nudParallel.Location  = new Point(100, 84);
        nudParallel.Size      = new Size(56, 31);
        nudParallel.Minimum   = 1;
        nudParallel.Maximum   = 32;
        nudParallel.Value     = 4;

        lblBandwidth.Text      = "Throttle:";
        lblBandwidth.Location  = new Point(168, 86);
        lblBandwidth.Size      = new Size(64, 23);
        lblBandwidth.TextAlign = ContentAlignment.MiddleRight;

        nudBandwidth.Location  = new Point(236, 84);
        nudBandwidth.Size      = new Size(72, 31);
        nudBandwidth.Minimum   = 0;
        nudBandwidth.Maximum   = 10000;
        nudBandwidth.Value     = 0;

        lblBwUnit.Text      = "MB/s (0=unlimited)";
        lblBwUnit.Location  = new Point(312, 86);
        lblBwUnit.Size      = new Size(148, 23);
        lblBwUnit.TextAlign = ContentAlignment.MiddleLeft;

        btnStart.Text     = "Upload";
        btnStart.Location = new Point(466, 82);
        btnStart.Size     = new Size(100, 34);
        btnStart.Click   += btnStart_Click;

        lblOverall.Text      = "";
        lblOverall.Location  = new Point(576, 86);
        lblOverall.Size      = new Size(222, 23);
        lblOverall.TextAlign = ContentAlignment.MiddleLeft;
        lblOverall.Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        lblOverall.Font      = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);

        // rtbLog — fills remaining space
        rtbLog.Dock      = DockStyle.Fill;
        rtbLog.ReadOnly  = true;
        rtbLog.BackColor = SystemColors.Window;
        rtbLog.Font      = new Font("Consolas", 9f);
        rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;
        rtbLog.WordWrap  = false;

        // MainForm
        ClientSize = new Size(820, 500);
        MinimumSize = new Size(640, 300);
        Controls.Add(rtbLog);
        Controls.Add(pnlTop);
        Name = "MainForm";
        Text = "FileRelay Test Client";
        FormClosing += MainForm_FormClosing;

        pnlTop.ResumeLayout(false);
        pnlTop.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)nudParallel).EndInit();
        ((System.ComponentModel.ISupportInitialize)nudBandwidth).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }

    private Panel           pnlTop       = null!;
    private Label           lblServer    = null!;
    private TextBox         txtServerUrl = null!;
    private Label           lblFile      = null!;
    private TextBox         txtFilePath  = null!;
    private Button          btnBrowse    = null!;
    private Label           lblParallel  = null!;
    private NumericUpDown   nudParallel  = null!;
    private Label           lblBandwidth = null!;
    private NumericUpDown   nudBandwidth = null!;
    private Label           lblBwUnit    = null!;
    private Button          btnStart     = null!;
    private Label           lblOverall   = null!;
    private RichTextBox     rtbLog       = null!;
    private OpenFileDialog  ofd          = null!;
}
