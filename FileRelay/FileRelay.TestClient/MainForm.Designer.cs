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
        txtServerUrl = new TextBox();
        txtFilePath = new TextBox();
        btnBrowse = new Button();
        btnUpload = new Button();
        ofd = new OpenFileDialog();
        lblServer = new Label();
        lblFile = new Label();
        pbProgress = new ProgressBar();
        lblStatus = new Label();
        SuspendLayout();
        // 
        // txtServerUrl
        // 
        txtServerUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtServerUrl.Location = new Point(88, 12);
        txtServerUrl.Name = "txtServerUrl";
        txtServerUrl.Size = new Size(887, 31);
        txtServerUrl.TabIndex = 1;
        txtServerUrl.Text = "http://localhost:61488/";
        // 
        // txtFilePath
        // 
        txtFilePath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtFilePath.Location = new Point(88, 47);
        txtFilePath.Name = "txtFilePath";
        txtFilePath.ReadOnly = true;
        txtFilePath.Size = new Size(790, 31);
        txtFilePath.TabIndex = 3;
        // 
        // btnBrowse
        // 
        btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowse.Location = new Point(884, 44);
        btnBrowse.Name = "btnBrowse";
        btnBrowse.Size = new Size(91, 34);
        btnBrowse.TabIndex = 4;
        btnBrowse.Text = "Browse...";
        btnBrowse.Click += btnBrowse_Click;
        // 
        // btnUpload
        // 
        btnUpload.Location = new Point(88, 82);
        btnUpload.Name = "btnUpload";
        btnUpload.Size = new Size(100, 38);
        btnUpload.TabIndex = 5;
        btnUpload.Text = "Upload";
        btnUpload.Click += btnUpload_Click;
        // 
        // lblServer
        // 
        lblServer.Location = new Point(12, 15);
        lblServer.Name = "lblServer";
        lblServer.Size = new Size(70, 23);
        lblServer.TabIndex = 0;
        lblServer.Text = "Server URL:";
        // 
        // lblFile
        // 
        lblFile.Location = new Point(12, 50);
        lblFile.Name = "lblFile";
        lblFile.Size = new Size(70, 23);
        lblFile.TabIndex = 2;
        lblFile.Text = "File:";
        // 
        // pbProgress
        // 
        pbProgress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        pbProgress.Location = new Point(12, 126);
        pbProgress.Name = "pbProgress";
        pbProgress.Size = new Size(963, 23);
        pbProgress.TabIndex = 6;
        // 
        // lblStatus
        // 
        lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        lblStatus.Location = new Point(12, 152);
        lblStatus.Name = "lblStatus";
        lblStatus.Size = new Size(963, 88);
        lblStatus.TabIndex = 7;
        // 
        // MainForm
        // 
        ClientSize = new Size(987, 249);
        Controls.Add(lblServer);
        Controls.Add(txtServerUrl);
        Controls.Add(lblFile);
        Controls.Add(txtFilePath);
        Controls.Add(btnBrowse);
        Controls.Add(btnUpload);
        Controls.Add(pbProgress);
        Controls.Add(lblStatus);
        Name = "MainForm";
        Text = "FileRelay Test Client";
        ResumeLayout(false);
        PerformLayout();
    }

    private TextBox txtServerUrl = null!;
    private TextBox txtFilePath = null!;
    private Button btnBrowse = null!;
    private Button btnUpload = null!;
    private OpenFileDialog ofd = null!;
    private Label lblServer = null!;
    private Label lblFile = null!;
    private ProgressBar pbProgress = null!;
    private Label lblStatus = null!;
}
