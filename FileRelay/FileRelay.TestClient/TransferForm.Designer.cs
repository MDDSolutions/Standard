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
        lblFile   = new Label();
        txtFilePath = new TextBox();
        btnBrowse = new Button();
        btnStart  = new Button();
        rtbLog    = new RichTextBox();
        ofd       = new OpenFileDialog();

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
        Controls.Add(rtbLog);
        Name         = "TransferForm";
        Text         = "Transfer";
        FormClosing += TransferForm_FormClosing;

        ResumeLayout(false);
        PerformLayout();
    }

    private Label          lblFile     = null!;
    private TextBox        txtFilePath = null!;
    private Button         btnBrowse   = null!;
    private Button         btnStart    = null!;
    private RichTextBox    rtbLog      = null!;
    private OpenFileDialog ofd         = null!;
}
