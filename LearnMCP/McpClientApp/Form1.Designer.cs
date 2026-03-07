namespace McpClientApp;

partial class Form1
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1100, 750);
        Text = "MCP Protocol Explorer";
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(900, 600);

        // === Top panel: Server path + Connect ===
        var panelTop = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(6, 6, 6, 4)
        };
        panelTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panelTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panelTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panelTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panelTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        lblServer = new Label { Text = "Server Path:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 0) };
        txtServerPath = new TextBox { Dock = DockStyle.Fill, Text = @"C:\Users\syedhu\source\play\LearnMCP\McpServerDemo", Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 6, 3, 0) };
        btnConnect = new Button { Text = "Start Server", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, Height = 32, Margin = new Padding(6, 4, 3, 0) };
        btnDisconnect = new Button { Text = "Stop", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, Height = 32, Enabled = false, Margin = new Padding(3, 4, 3, 0) };
        lblStatus = new Label { Text = "Disconnected", ForeColor = Color.Gray, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(6, 8, 3, 0) };

        panelTop.Controls.Add(lblServer, 0, 0);
        panelTop.Controls.Add(txtServerPath, 1, 0);
        panelTop.Controls.Add(btnConnect, 2, 0);
        panelTop.Controls.Add(btnDisconnect, 3, 0);
        panelTop.Controls.Add(lblStatus, 4, 0);

        // === Left panel: Tool buttons ===
        var panelLeft = new Panel { Dock = DockStyle.Left, Width = 240 };

        var flowPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8, 6, 8, 6),
            AutoScroll = true
        };

        int bw = 210; // button width

        var lblTools = new Label { Text = "MCP Actions", Font = new Font("Segoe UI", 11, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 2, 0, 6) };

        btnInitialize = new Button { Text = "1. Initialize", Width = bw, Height = 32, Enabled = false, Margin = new Padding(0, 1, 0, 1) };
        btnListTools = new Button { Text = "2. List Tools", Width = bw, Height = 32, Enabled = false, Margin = new Padding(0, 1, 0, 1) };

        var lblSep = new Label { Text = "--- Call a Tool ---", ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0, 4, 0, 2) };

        btnCallTime = new Button { Text = "Get Current Time", Width = bw, Height = 32, Enabled = false, Margin = new Padding(0, 1, 0, 1) };
        btnCallCalc = new Button { Text = "Calculate 7 x 6", Width = bw, Height = 32, Enabled = false, Margin = new Padding(0, 1, 0, 1) };
        btnCallWeather = new Button { Text = "Get Weather (Tokyo)", Width = bw, Height = 32, Enabled = false, Margin = new Padding(0, 1, 0, 1) };

        var lblSep2 = new Label { Text = "--- Custom ---", ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0, 4, 0, 2) };

        txtCustomJson = new TextBox { Width = bw, Height = 60, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Paste raw JSON-RPC...", Margin = new Padding(0, 1, 0, 1) };
        btnSendCustom = new Button { Text = "Send Custom", Width = bw, Height = 32, Enabled = false, Margin = new Padding(0, 1, 0, 1) };
        btnClearLog = new Button { Text = "Clear Log", Width = bw, Height = 32, Margin = new Padding(0, 8, 0, 1) };

        flowPanel.Controls.AddRange(new Control[] { lblTools, btnInitialize, btnListTools, lblSep, btnCallTime, btnCallCalc, btnCallWeather, lblSep2, txtCustomJson, btnSendCustom, btnClearLog });
        panelLeft.Controls.Add(flowPanel);

        // === Main area: Message log ===
        rtbMessages = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White,
            Font = new Font("Cascadia Code", 10F),
            WordWrap = true
        };

        Controls.Add(rtbMessages);
        Controls.Add(panelLeft);
        Controls.Add(panelTop);
    }

    private Label lblServer;
    private TextBox txtServerPath;
    private Button btnConnect;
    private Button btnDisconnect;
    private Label lblStatus;
    private Button btnInitialize;
    private Button btnListTools;
    private Button btnCallTime;
    private Button btnCallCalc;
    private Button btnCallWeather;
    private TextBox txtCustomJson;
    private Button btnSendCustom;
    private Button btnClearLog;
    private RichTextBox rtbMessages;

    #endregion
}
