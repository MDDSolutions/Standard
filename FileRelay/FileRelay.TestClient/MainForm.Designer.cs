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
        lblServer = new Label();
        txtServerUrl = new TextBox();
        lblAppId = new Label();
        txtAppId = new TextBox();
        lblApiKey = new Label();
        txtApiKey = new TextBox();
        btnTest = new Button();
        btnRotateKey = new Button();
        btnExpireGrace = new Button();
        chkAllowUntrustedCert = new CheckBox();
        lblParallel = new Label();
        nudParallel = new NumericUpDown();
        lblBandwidth = new Label();
        nudBandwidth = new NumericUpDown();
        lblBwUnit = new Label();
        lblRate = new Label();
        rtbStatus = new RichTextBox();
        btnNewTransfer = new Button();
        ((System.ComponentModel.ISupportInitialize)nudParallel).BeginInit();
        ((System.ComponentModel.ISupportInitialize)nudBandwidth).BeginInit();
        SuspendLayout();
        // 
        // lblServer
        // 
        lblServer.Location = new Point(15, 22);
        lblServer.Margin = new Padding(4, 0, 4, 0);
        lblServer.Name = "lblServer";
        lblServer.Size = new Size(80, 29);
        lblServer.TabIndex = 0;
        lblServer.Text = "Server:";
        lblServer.TextAlign = ContentAlignment.MiddleRight;
        // 
        // txtServerUrl
        // 
        txtServerUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtServerUrl.Location = new Point(100, 19);
        txtServerUrl.Margin = new Padding(4);
        txtServerUrl.Name = "txtServerUrl";
        txtServerUrl.Size = new Size(853, 31);
        txtServerUrl.TabIndex = 1;
        txtServerUrl.Text = "https://mdd-trident1:61489/";
        // 
        // lblAppId
        // 
        lblAppId.Location = new Point(15, 62);
        lblAppId.Margin = new Padding(4, 0, 4, 0);
        lblAppId.Name = "lblAppId";
        lblAppId.Size = new Size(80, 29);
        lblAppId.TabIndex = 2;
        lblAppId.Text = "App ID:";
        lblAppId.TextAlign = ContentAlignment.MiddleRight;
        // 
        // txtAppId
        // 
        txtAppId.Location = new Point(100, 59);
        txtAppId.Margin = new Padding(4);
        txtAppId.Name = "txtAppId";
        txtAppId.Size = new Size(380, 31);
        txtAppId.TabIndex = 3;
        // 
        // lblApiKey
        // 
        lblApiKey.Location = new Point(15, 102);
        lblApiKey.Margin = new Padding(4, 0, 4, 0);
        lblApiKey.Name = "lblApiKey";
        lblApiKey.Size = new Size(80, 29);
        lblApiKey.TabIndex = 4;
        lblApiKey.Text = "Seed Key:";
        lblApiKey.TextAlign = ContentAlignment.MiddleRight;
        // 
        // txtApiKey
        // 
        txtApiKey.Location = new Point(100, 99);
        txtApiKey.Margin = new Padding(4);
        txtApiKey.Name = "txtApiKey";
        txtApiKey.Size = new Size(380, 31);
        txtApiKey.TabIndex = 5;
        // 
        // btnTest
        // 
        btnTest.Location = new Point(488, 97);
        btnTest.Margin = new Padding(4);
        btnTest.Name = "btnTest";
        btnTest.Size = new Size(120, 35);
        btnTest.TabIndex = 6;
        btnTest.Text = "Test Connection";
        btnTest.Click += btnTest_Click;
        // 
        // btnRotateKey
        // 
        btnRotateKey.Location = new Point(616, 97);
        btnRotateKey.Margin = new Padding(4);
        btnRotateKey.Name = "btnRotateKey";
        btnRotateKey.Size = new Size(110, 35);
        btnRotateKey.TabIndex = 7;
        btnRotateKey.Text = "Rotate Key";
        btnRotateKey.Click += btnRotateKey_Click;
        // 
        // btnExpireGrace
        // 
        btnExpireGrace.Location = new Point(734, 97);
        btnExpireGrace.Margin = new Padding(4);
        btnExpireGrace.Name = "btnExpireGrace";
        btnExpireGrace.Size = new Size(110, 35);
        btnExpireGrace.TabIndex = 16;
        btnExpireGrace.Text = "Expire Grace";
        btnExpireGrace.Click += btnExpireGrace_Click;
        // 
        // chkAllowUntrustedCert
        // 
        chkAllowUntrustedCert.Location = new Point(616, 153);
        chkAllowUntrustedCert.Margin = new Padding(4);
        chkAllowUntrustedCert.Name = "chkAllowUntrustedCert";
        chkAllowUntrustedCert.Size = new Size(200, 29);
        chkAllowUntrustedCert.TabIndex = 8;
        chkAllowUntrustedCert.Text = "Allow untrusted cert";
        chkAllowUntrustedCert.CheckedChanged += chkAllowUntrustedCert_CheckedChanged;
        // 
        // lblParallel
        // 
        lblParallel.Location = new Point(15, 150);
        lblParallel.Margin = new Padding(4, 0, 4, 0);
        lblParallel.Name = "lblParallel";
        lblParallel.Size = new Size(128, 36);
        lblParallel.TabIndex = 9;
        lblParallel.Text = "Connections:";
        lblParallel.TextAlign = ContentAlignment.MiddleRight;
        // 
        // nudParallel
        // 
        nudParallel.Location = new Point(148, 150);
        nudParallel.Margin = new Padding(4);
        nudParallel.Maximum = new decimal(new int[] { 32, 0, 0, 0 });
        nudParallel.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
        nudParallel.Name = "nudParallel";
        nudParallel.Size = new Size(70, 31);
        nudParallel.TabIndex = 10;
        nudParallel.Value = new decimal(new int[] { 4, 0, 0, 0 });
        // 
        // lblBandwidth
        // 
        lblBandwidth.Location = new Point(232, 150);
        lblBandwidth.Margin = new Padding(4, 0, 4, 0);
        lblBandwidth.Name = "lblBandwidth";
        lblBandwidth.Size = new Size(85, 36);
        lblBandwidth.TabIndex = 11;
        lblBandwidth.Text = "Throttle:";
        lblBandwidth.TextAlign = ContentAlignment.MiddleRight;
        // 
        // nudBandwidth
        // 
        nudBandwidth.Location = new Point(322, 150);
        nudBandwidth.Margin = new Padding(4);
        nudBandwidth.Maximum = new decimal(new int[] { 10000, 0, 0, 0 });
        nudBandwidth.Name = "nudBandwidth";
        nudBandwidth.Size = new Size(90, 31);
        nudBandwidth.TabIndex = 12;
        // 
        // lblBwUnit
        // 
        lblBwUnit.Location = new Point(418, 150);
        lblBwUnit.Margin = new Padding(4, 0, 4, 0);
        lblBwUnit.Name = "lblBwUnit";
        lblBwUnit.Size = new Size(185, 36);
        lblBwUnit.TabIndex = 13;
        lblBwUnit.Text = "MB/s (0=unlimited)";
        lblBwUnit.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // lblRate
        // 
        lblRate.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        lblRate.Location = new Point(830, 150);
        lblRate.Margin = new Padding(4, 0, 4, 0);
        lblRate.Name = "lblRate";
        lblRate.Size = new Size(245, 36);
        lblRate.TabIndex = 14;
        lblRate.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // rtbStatus
        // 
        rtbStatus.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        rtbStatus.BackColor = SystemColors.Control;
        rtbStatus.BorderStyle = BorderStyle.None;
        rtbStatus.Location = new Point(15, 196);
        rtbStatus.Margin = new Padding(4);
        rtbStatus.Name = "rtbStatus";
        rtbStatus.ReadOnly = true;
        rtbStatus.ScrollBars = RichTextBoxScrollBars.Vertical;
        rtbStatus.Size = new Size(1071, 78);
        rtbStatus.TabIndex = 17;
        rtbStatus.TabStop = false;
        rtbStatus.Text = "";
        // 
        // btnNewTransfer
        // 
        btnNewTransfer.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnNewTransfer.Location = new Point(961, 15);
        btnNewTransfer.Margin = new Padding(4);
        btnNewTransfer.Name = "btnNewTransfer";
        btnNewTransfer.Size = new Size(125, 42);
        btnNewTransfer.TabIndex = 15;
        btnNewTransfer.Text = "New Transfer";
        btnNewTransfer.Click += btnNewTransfer_Click;
        // 
        // MainForm
        // 
        AutoScaleDimensions = new SizeF(10F, 25F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1101, 285);
        Controls.Add(lblServer);
        Controls.Add(txtServerUrl);
        Controls.Add(lblAppId);
        Controls.Add(txtAppId);
        Controls.Add(lblApiKey);
        Controls.Add(txtApiKey);
        Controls.Add(btnTest);
        Controls.Add(btnRotateKey);
        Controls.Add(btnExpireGrace);
        Controls.Add(chkAllowUntrustedCert);
        Controls.Add(lblParallel);
        Controls.Add(nudParallel);
        Controls.Add(lblBandwidth);
        Controls.Add(nudBandwidth);
        Controls.Add(lblBwUnit);
        Controls.Add(lblRate);
        Controls.Add(rtbStatus);
        Controls.Add(btnNewTransfer);
        Margin = new Padding(4);
        MaximizeBox = false;
        MinimumSize = new Size(620, 323);
        Name = "MainForm";
        Text = "FileRelay";
        FormClosing += MainForm_FormClosing;
        ((System.ComponentModel.ISupportInitialize)nudParallel).EndInit();
        ((System.ComponentModel.ISupportInitialize)nudBandwidth).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }

    private Label           lblServer             = null!;
    private TextBox         txtServerUrl          = null!;
    private Label           lblAppId              = null!;
    private TextBox         txtAppId              = null!;
    private Label           lblApiKey             = null!;
    private TextBox         txtApiKey             = null!;
    private Button          btnTest               = null!;
    private Button          btnRotateKey          = null!;
    private Button          btnExpireGrace        = null!;
    private CheckBox        chkAllowUntrustedCert = null!;
    private Label           lblParallel           = null!;
    private NumericUpDown   nudParallel           = null!;
    private Label           lblBandwidth          = null!;
    private NumericUpDown   nudBandwidth          = null!;
    private Label           lblBwUnit             = null!;
    private Label           lblRate               = null!;
    private RichTextBox     rtbStatus             = null!;
    private Button          btnNewTransfer        = null!;
}
