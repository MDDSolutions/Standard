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
        lblApiKey = new Label();
        txtApiKey = new TextBox();
        btnTest = new Button();
        lblParallel = new Label();
        nudParallel = new NumericUpDown();
        lblBandwidth = new Label();
        nudBandwidth = new NumericUpDown();
        lblBwUnit = new Label();
        lblOverall = new Label();
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
        // lblApiKey
        //
        lblApiKey.Location = new Point(15, 62);
        lblApiKey.Margin = new Padding(4, 0, 4, 0);
        lblApiKey.Name = "lblApiKey";
        lblApiKey.Size = new Size(80, 29);
        lblApiKey.TabIndex = 2;
        lblApiKey.Text = "API Key:";
        lblApiKey.TextAlign = ContentAlignment.MiddleRight;
        //
        // txtApiKey
        //
        txtApiKey.Location = new Point(100, 59);
        txtApiKey.Margin = new Padding(4);
        txtApiKey.Name = "txtApiKey";
        txtApiKey.Size = new Size(380, 31);
        txtApiKey.TabIndex = 3;
        //
        // btnTest
        //
        btnTest.Location = new Point(488, 57);
        btnTest.Margin = new Padding(4);
        btnTest.Name = "btnTest";
        btnTest.Size = new Size(120, 35);
        btnTest.TabIndex = 4;
        btnTest.Text = "Test Connection";
        btnTest.Click += btnTest_Click;
        //
        // lblParallel
        //
        lblParallel.Location = new Point(15, 110);
        lblParallel.Margin = new Padding(4, 0, 4, 0);
        lblParallel.Name = "lblParallel";
        lblParallel.Size = new Size(128, 36);
        lblParallel.TabIndex = 5;
        lblParallel.Text = "Connections:";
        lblParallel.TextAlign = ContentAlignment.MiddleRight;
        //
        // nudParallel
        //
        nudParallel.Location = new Point(148, 110);
        nudParallel.Margin = new Padding(4);
        nudParallel.Maximum = new decimal(new int[] { 32, 0, 0, 0 });
        nudParallel.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
        nudParallel.Name = "nudParallel";
        nudParallel.Size = new Size(70, 31);
        nudParallel.TabIndex = 6;
        nudParallel.Value = new decimal(new int[] { 4, 0, 0, 0 });
        //
        // lblBandwidth
        //
        lblBandwidth.Location = new Point(232, 110);
        lblBandwidth.Margin = new Padding(4, 0, 4, 0);
        lblBandwidth.Name = "lblBandwidth";
        lblBandwidth.Size = new Size(85, 36);
        lblBandwidth.TabIndex = 7;
        lblBandwidth.Text = "Throttle:";
        lblBandwidth.TextAlign = ContentAlignment.MiddleRight;
        //
        // nudBandwidth
        //
        nudBandwidth.Location = new Point(322, 110);
        nudBandwidth.Margin = new Padding(4);
        nudBandwidth.Maximum = new decimal(new int[] { 10000, 0, 0, 0 });
        nudBandwidth.Name = "nudBandwidth";
        nudBandwidth.Size = new Size(90, 31);
        nudBandwidth.TabIndex = 8;
        //
        // lblBwUnit
        //
        lblBwUnit.Location = new Point(418, 110);
        lblBwUnit.Margin = new Padding(4, 0, 4, 0);
        lblBwUnit.Name = "lblBwUnit";
        lblBwUnit.Size = new Size(185, 36);
        lblBwUnit.TabIndex = 9;
        lblBwUnit.Text = "MB/s (0=unlimited)";
        lblBwUnit.TextAlign = ContentAlignment.MiddleLeft;
        //
        // lblOverall
        //
        lblOverall.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        lblOverall.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        lblOverall.Location = new Point(611, 57);
        lblOverall.Margin = new Padding(4, 0, 4, 0);
        lblOverall.Name = "lblOverall";
        lblOverall.Size = new Size(475, 36);
        lblOverall.TabIndex = 10;
        lblOverall.TextAlign = ContentAlignment.MiddleRight;
        //
        // btnNewTransfer
        //
        btnNewTransfer.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnNewTransfer.Location = new Point(961, 15);
        btnNewTransfer.Margin = new Padding(4);
        btnNewTransfer.Name = "btnNewTransfer";
        btnNewTransfer.Size = new Size(125, 42);
        btnNewTransfer.TabIndex = 11;
        btnNewTransfer.Text = "New Transfer";
        btnNewTransfer.Click += btnNewTransfer_Click;
        //
        // MainForm
        //
        AutoScaleDimensions = new SizeF(10F, 25F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1101, 160);
        Controls.Add(lblServer);
        Controls.Add(txtServerUrl);
        Controls.Add(lblApiKey);
        Controls.Add(txtApiKey);
        Controls.Add(btnTest);
        Controls.Add(lblParallel);
        Controls.Add(nudParallel);
        Controls.Add(lblBandwidth);
        Controls.Add(nudBandwidth);
        Controls.Add(lblBwUnit);
        Controls.Add(lblOverall);
        Controls.Add(btnNewTransfer);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        Margin = new Padding(4);
        MaximizeBox = false;
        MinimumSize = new Size(620, 196);
        Name = "MainForm";
        Text = "FileRelay";
        FormClosing += MainForm_FormClosing;
        ((System.ComponentModel.ISupportInitialize)nudParallel).EndInit();
        ((System.ComponentModel.ISupportInitialize)nudBandwidth).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }

    private Label           lblServer      = null!;
    private TextBox         txtServerUrl   = null!;
    private Label           lblApiKey      = null!;
    private TextBox         txtApiKey      = null!;
    private Button          btnTest        = null!;
    private Label           lblParallel    = null!;
    private NumericUpDown   nudParallel    = null!;
    private Label           lblBandwidth   = null!;
    private NumericUpDown   nudBandwidth   = null!;
    private Label           lblBwUnit      = null!;
    private Label           lblOverall     = null!;
    private Button          btnNewTransfer = null!;
}
