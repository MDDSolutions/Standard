namespace FileRelay.TestClient;

partial class TransferForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null)) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        lblFile       = new Label();
        txtFilePath   = new TextBox();
        btnBrowse     = new Button();
        btnStart      = new Button();
        rtbLog        = new RichTextBox();
        ofd           = new OpenFileDialog();
        lblFaultChunk = new Label();
        nudFaultChunk = new NumericUpDown();
        btnFault      = new Button();
        ((System.ComponentModel.ISupportInitialize)nudFaultChunk).BeginInit();

        SuspendLayout();

        // lblFile
        lblFile.Location  = new Point(12, 15);
        lblFile.Size      = new Size(40, 23);
        lblFile.Text      = "File:";
        lblFile.TextAlign = ContentAlignment.MiddleRight;

        // txtFilePath
        txtFilePath.Anchor   = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtFilePath.Location = new Point(56, 12);
        txtFilePath.ReadOnly = true;
        txtFilePath.Size     = new Size(536, 31);

        // btnBrowse
        btnBrowse.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowse.Location = new Point(598, 9);
        btnBrowse.Size     = new Size(94, 34);
        btnBrowse.Text     = "Browse...";
        btnBrowse.Click   += btnBrowse_Click;

        // btnStart
        btnStart.Location = new Point(12, 50);
        btnStart.Size     = new Size(100, 34);
        btnStart.Text     = "Upload";
        btnStart.Click   += btnStart_Click;

        // lblFaultChunk
        lblFaultChunk.Location  = new Point(124, 57);
        lblFaultChunk.Size      = new Size(70, 23);
        lblFaultChunk.Text      = "Fault chunk:";
        lblFaultChunk.TextAlign = ContentAlignment.MiddleRight;

        // nudFaultChunk
        nudFaultChunk.Location = new Point(198, 54);
        nudFaultChunk.Size     = new Size(60, 31);
        nudFaultChunk.Minimum  = 0;
        nudFaultChunk.Maximum  = 9999;

        // btnFault
        btnFault.Location = new Point(264, 50);
        btnFault.Size     = new Size(110, 34);
        btnFault.Text     = "Inject Fault";
        btnFault.Click   += btnFault_Click;

        // rtbLog
        rtbLog.Anchor      = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        rtbLog.BackColor   = SystemColors.Window;
        rtbLog.Font        = new Font("Consolas", 9f);
        rtbLog.Location    = new Point(12, 92);
        rtbLog.ReadOnly    = true;
        rtbLog.ScrollBars  = RichTextBoxScrollBars.Vertical;
        rtbLog.Size        = new Size(680, 296);
        rtbLog.Text        = "";
        rtbLog.WordWrap    = false;

        // TransferForm
        AutoScaleDimensions = new SizeF(8F, 20F);
        AutoScaleMode       = AutoScaleMode.Font;
        ClientSize          = new Size(704, 400);
        MinimumSize         = new Size(500, 300);
        Controls.Add(lblFile);
        Controls.Add(txtFilePath);
        Controls.Add(btnBrowse);
        Controls.Add(btnStart);
        Controls.Add(lblFaultChunk);
        Controls.Add(nudFaultChunk);
        Controls.Add(btnFault);
        Controls.Add(rtbLog);
        Name         = "TransferForm";
        Text         = "Transfer";
        FormClosing += TransferForm_FormClosing;

        ((System.ComponentModel.ISupportInitialize)nudFaultChunk).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }

    private Label          lblFile       = null!;
    private TextBox        txtFilePath   = null!;
    private Button         btnBrowse     = null!;
    private Button         btnStart      = null!;
    private RichTextBox    rtbLog        = null!;
    private OpenFileDialog ofd           = null!;
    private Label          lblFaultChunk = null!;
    private NumericUpDown  nudFaultChunk = null!;
    private Button         btnFault      = null!;
}
