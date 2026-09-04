#nullable enable

namespace HusayniaSMS.WinForms.Forms;

partial class MainForm
{
    private System.ComponentModel.IContainer? components;
    private Label safeDemoBanner = null!;
    private TextBox accountSidTextBox = null!;
    private TextBox authTokenTextBox = null!;
    private ComboBox senderModeComboBox = null!;
    private Label senderValueLabel = null!;
    private TextBox senderValueTextBox = null!;
    private Button saveSetupButton = null!;
    private Label setupStatusLabel = null!;
    private Button importButton = null!;
    private Button refreshButton = null!;
    private Button selectAllButton = null!;
    private Button clearSelectionButton = null!;
    private Label contactsStatusLabel = null!;
    private DataGridView contactsGrid = null!;
    private DataGridViewCheckBoxColumn selectedColumn = null!;
    private DataGridViewTextBoxColumn resultColumn = null!;
    private TextBox messageTextBox = null!;
    private Label messageCountLabel = null!;
    private Label messageErrorLabel = null!;
    private Button sendSelectedButton = null!;
    private Button sendAllButton = null!;
    private Button cancelButton = null!;
    private Label batchStatusLabel = null!;
    private Label applicationStatusLabel = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        safeDemoBanner = new Label();
        accountSidTextBox = new TextBox();
        authTokenTextBox = new TextBox();
        senderModeComboBox = new ComboBox();
        senderValueLabel = new Label();
        senderValueTextBox = new TextBox();
        saveSetupButton = new Button();
        setupStatusLabel = new Label();
        importButton = new Button();
        refreshButton = new Button();
        selectAllButton = new Button();
        clearSelectionButton = new Button();
        contactsStatusLabel = new Label();
        contactsGrid = new DataGridView();
        selectedColumn = new DataGridViewCheckBoxColumn();
        resultColumn = new DataGridViewTextBoxColumn();
        messageTextBox = new TextBox();
        messageCountLabel = new Label();
        messageErrorLabel = new Label();
        sendSelectedButton = new Button();
        sendAllButton = new Button();
        cancelButton = new Button();
        batchStatusLabel = new Label();
        applicationStatusLabel = new Label();

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(900, 650);
        Text = "Husaynia SMS";
        StartPosition = FormStartPosition.CenterScreen;

        ConfigureButtonSizing(
            saveSetupButton,
            importButton,
            refreshButton,
            selectAllButton,
            clearSelectionButton,
            sendSelectedButton,
            sendAllButton,
            cancelButton);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        safeDemoBanner.AutoSize = true;
        safeDemoBanner.Dock = DockStyle.Fill;
        safeDemoBanner.Font = new Font(Font, FontStyle.Bold);
        safeDemoBanner.ForeColor = Color.DarkGreen;
        safeDemoBanner.TextAlign = ContentAlignment.MiddleCenter;
        safeDemoBanner.Text = "SAFE DEMO — NO SMS WILL BE SENT";
        root.Controls.Add(safeDemoBanner, 0, 0);

        var setupGroup = new GroupBox { Text = "Twilio setup", Dock = DockStyle.Top, AutoSize = true };
        var setup = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        setup.Controls.Add(new Label { Text = "Account SID", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        setup.Controls.Add(accountSidTextBox, 1, 0);
        setup.Controls.Add(new Label { Text = "Sender mode", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        senderModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        senderModeComboBox.Items.AddRange(["From phone number", "Messaging Service SID"]);
        senderModeComboBox.SelectedIndex = 0;
        senderModeComboBox.SelectedIndexChanged += SenderModeComboBox_SelectedIndexChanged;
        setup.Controls.Add(senderModeComboBox, 3, 0);
        setup.Controls.Add(new Label { Text = "Auth token", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        authTokenTextBox.UseSystemPasswordChar = true;
        setup.Controls.Add(authTokenTextBox, 1, 1);
        senderValueLabel.AutoSize = true;
        senderValueLabel.Anchor = AnchorStyles.Left;
        senderValueLabel.Text = "From phone number (E.164)";
        setup.Controls.Add(senderValueLabel, 2, 1);
        setup.Controls.Add(senderValueTextBox, 3, 1);
        saveSetupButton.Text = "Save setup";
        saveSetupButton.Click += SaveSetupButton_Click;
        setup.Controls.Add(saveSetupButton, 2, 2);
        setupStatusLabel.AutoSize = true;
        setupStatusLabel.Text = "Setup incomplete.";
        setup.Controls.Add(setupStatusLabel, 3, 2);
        accountSidTextBox.Dock = DockStyle.Fill;
        authTokenTextBox.Dock = DockStyle.Fill;
        senderModeComboBox.Dock = DockStyle.Fill;
        senderValueTextBox.Dock = DockStyle.Fill;
        setupGroup.Controls.Add(setup);
        root.Controls.Add(setupGroup, 0, 1);

        var contactActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        importButton.Text = "Import CSV";
        importButton.Click += ImportButton_Click;
        refreshButton.Text = "Refresh";
        refreshButton.Click += RefreshButton_Click;
        selectAllButton.Text = "Select All Valid";
        selectAllButton.Click += SelectAllButton_Click;
        clearSelectionButton.Text = "Clear Selection";
        clearSelectionButton.Click += ClearSelectionButton_Click;
        contactsStatusLabel.AutoSize = true;
        contactsStatusLabel.Padding = new Padding(12, 7, 0, 0);
        contactsStatusLabel.Text = "0 contacts.";
        contactActions.Controls.AddRange(
            [importButton, refreshButton, selectAllButton, clearSelectionButton, contactsStatusLabel]);
        root.Controls.Add(contactActions, 0, 2);

        contactsGrid.Dock = DockStyle.Fill;
        contactsGrid.AllowUserToAddRows = false;
        contactsGrid.AllowUserToDeleteRows = false;
        contactsGrid.AllowUserToOrderColumns = false;
        contactsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        contactsGrid.RowHeadersVisible = false;
        contactsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        selectedColumn.Name = "Selected";
        selectedColumn.HeaderText = "Selected";
        selectedColumn.FillWeight = 35;
        contactsGrid.Columns.Add(selectedColumn);
        contactsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", ReadOnly = true });
        contactsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Number", HeaderText = "Number", ReadOnly = true });
        contactsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Validation", HeaderText = "Validation", ReadOnly = true });
        contactsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Error", HeaderText = "Error", ReadOnly = true });
        resultColumn.Name = "LatestResult";
        resultColumn.HeaderText = "Latest Result";
        resultColumn.ReadOnly = true;
        contactsGrid.Columns.Add(resultColumn);
        root.Controls.Add(contactsGrid, 0, 3);

        var messageGroup = new GroupBox { Text = "Message", Dock = DockStyle.Fill };
        var messageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        messageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        messageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        messageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        messageTextBox.Multiline = true;
        messageTextBox.ScrollBars = ScrollBars.Vertical;
        messageTextBox.Dock = DockStyle.Fill;
        messageTextBox.TextChanged += MessageTextBox_TextChanged;
        messageCountLabel.AutoSize = true;
        messageCountLabel.Text = "0 / 1,600 characters";
        messageErrorLabel.AutoSize = true;
        messageLayout.Controls.Add(messageTextBox, 0, 0);
        messageLayout.Controls.Add(messageCountLabel, 0, 1);
        messageLayout.Controls.Add(messageErrorLabel, 0, 2);
        messageGroup.Controls.Add(messageLayout);
        root.Controls.Add(messageGroup, 0, 4);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        var sendActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        sendSelectedButton.Text = "Send Selected";
        sendSelectedButton.Click += SendSelectedButton_Click;
        sendAllButton.Text = "Send All Valid";
        sendAllButton.Click += SendAllButton_Click;
        cancelButton.Text = "Cancel";
        cancelButton.Click += CancelButton_Click;
        sendActions.Controls.AddRange([sendSelectedButton, sendAllButton, cancelButton]);
        batchStatusLabel.AutoSize = true;
        applicationStatusLabel.AutoSize = true;
        footer.Controls.Add(sendActions, 0, 0);
        footer.Controls.Add(batchStatusLabel, 0, 1);
        footer.Controls.Add(applicationStatusLabel, 0, 2);
        root.Controls.Add(footer, 0, 5);

        Shown += MainForm_Shown;
        FormClosing += MainForm_FormClosing;
        ResumeLayout(performLayout: true);
    }

    private static void ConfigureButtonSizing(params Button[] buttons)
    {
        foreach (var button in buttons)
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(0, 36);
            button.Padding = new Padding(12, 6, 12, 6);
        }
    }
}
