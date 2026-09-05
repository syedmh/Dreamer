#nullable enable

namespace HusayniaSMS.WinForms.Forms;

partial class MainForm
{
    private System.ComponentModel.IContainer? components = null;
    private TableLayoutPanel rootLayoutPanel = null!;
    private Label safeDemoBanner = null!;
    private GroupBox setupGroupBox = null!;
    private TableLayoutPanel setupLayoutPanel = null!;
    private Label accountSidLabel = null!;
    private TextBox accountSidTextBox = null!;
    private Label senderModeLabel = null!;
    private ComboBox senderModeComboBox = null!;
    private Label authTokenLabel = null!;
    private TextBox authTokenTextBox = null!;
    private Label senderValueLabel = null!;
    private TextBox senderValueTextBox = null!;
    private Button saveSetupButton = null!;
    private Label setupStatusLabel = null!;
    private FlowLayoutPanel contactActionsFlowLayoutPanel = null!;
    private Button importButton = null!;
    private Button refreshButton = null!;
    private Button addContactButton = null!;
    private Button editContactButton = null!;
    private Button deleteSelectedButton = null!;
    private Button saveCsvButton = null!;
    private Button selectAllButton = null!;
    private Button clearSelectionButton = null!;
    private Label contactsStatusLabel = null!;
    private RecipientDataGridView contactsGrid = null!;
    private DataGridViewCheckBoxColumn selectedColumn = null!;
    private DataGridViewTextBoxColumn nameColumn = null!;
    private DataGridViewTextBoxColumn numberColumn = null!;
    private DataGridViewTextBoxColumn validationColumn = null!;
    private DataGridViewTextBoxColumn errorColumn = null!;
    private DataGridViewTextBoxColumn resultColumn = null!;
    private GroupBox messageGroupBox = null!;
    private TableLayoutPanel messageLayoutPanel = null!;
    private TextBox messageTextBox = null!;
    private Label messageCountLabel = null!;
    private Label messageErrorLabel = null!;
    private TableLayoutPanel footerLayoutPanel = null!;
    private FlowLayoutPanel sendActionsFlowLayoutPanel = null!;
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
        rootLayoutPanel = new TableLayoutPanel();
        safeDemoBanner = new Label();
        setupGroupBox = new GroupBox();
        setupLayoutPanel = new TableLayoutPanel();
        accountSidLabel = new Label();
        accountSidTextBox = new TextBox();
        senderModeLabel = new Label();
        senderModeComboBox = new ComboBox();
        authTokenLabel = new Label();
        authTokenTextBox = new TextBox();
        senderValueLabel = new Label();
        senderValueTextBox = new TextBox();
        saveSetupButton = new Button();
        setupStatusLabel = new Label();
        contactActionsFlowLayoutPanel = new FlowLayoutPanel();
        importButton = new Button();
        refreshButton = new Button();
        addContactButton = new Button();
        editContactButton = new Button();
        deleteSelectedButton = new Button();
        saveCsvButton = new Button();
        selectAllButton = new Button();
        clearSelectionButton = new Button();
        contactsStatusLabel = new Label();
        contactsGrid = new RecipientDataGridView();
        selectedColumn = new DataGridViewCheckBoxColumn();
        nameColumn = new DataGridViewTextBoxColumn();
        numberColumn = new DataGridViewTextBoxColumn();
        validationColumn = new DataGridViewTextBoxColumn();
        errorColumn = new DataGridViewTextBoxColumn();
        resultColumn = new DataGridViewTextBoxColumn();
        messageGroupBox = new GroupBox();
        messageLayoutPanel = new TableLayoutPanel();
        messageTextBox = new TextBox();
        messageCountLabel = new Label();
        messageErrorLabel = new Label();
        footerLayoutPanel = new TableLayoutPanel();
        sendActionsFlowLayoutPanel = new FlowLayoutPanel();
        sendSelectedButton = new Button();
        sendAllButton = new Button();
        cancelButton = new Button();
        batchStatusLabel = new Label();
        applicationStatusLabel = new Label();
        rootLayoutPanel.SuspendLayout();
        setupGroupBox.SuspendLayout();
        setupLayoutPanel.SuspendLayout();
        contactActionsFlowLayoutPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)contactsGrid).BeginInit();
        messageGroupBox.SuspendLayout();
        messageLayoutPanel.SuspendLayout();
        footerLayoutPanel.SuspendLayout();
        sendActionsFlowLayoutPanel.SuspendLayout();
        SuspendLayout();
        //
        // rootLayoutPanel
        //
        rootLayoutPanel.ColumnCount = 1;
        rootLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayoutPanel.Controls.Add(safeDemoBanner, 0, 0);
        rootLayoutPanel.Controls.Add(setupGroupBox, 0, 1);
        rootLayoutPanel.Controls.Add(contactActionsFlowLayoutPanel, 0, 2);
        rootLayoutPanel.Controls.Add(contactsGrid, 0, 3);
        rootLayoutPanel.Controls.Add(messageGroupBox, 0, 4);
        rootLayoutPanel.Controls.Add(footerLayoutPanel, 0, 5);
        rootLayoutPanel.Dock = DockStyle.Fill;
        rootLayoutPanel.Location = new Point(0, 0);
        rootLayoutPanel.Name = "rootLayoutPanel";
        rootLayoutPanel.Padding = new Padding(10);
        rootLayoutPanel.RowCount = 6;
        rootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 60F));
        rootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
        rootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayoutPanel.Size = new Size(1180, 760);
        rootLayoutPanel.TabIndex = 0;
        //
        // safeDemoBanner
        //
        safeDemoBanner.AutoSize = true;
        safeDemoBanner.Dock = DockStyle.Fill;
        safeDemoBanner.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        safeDemoBanner.ForeColor = Color.DarkGreen;
        safeDemoBanner.Location = new Point(13, 10);
        safeDemoBanner.Name = "safeDemoBanner";
        safeDemoBanner.Size = new Size(1154, 15);
        safeDemoBanner.TabIndex = 0;
        safeDemoBanner.Text = "SAFE DEMO — NO SMS WILL BE SENT";
        safeDemoBanner.TextAlign = ContentAlignment.MiddleCenter;
        safeDemoBanner.Visible = false;
        //
        // setupGroupBox
        //
        setupGroupBox.AutoSize = true;
        setupGroupBox.Controls.Add(setupLayoutPanel);
        setupGroupBox.Dock = DockStyle.Top;
        setupGroupBox.Location = new Point(13, 28);
        setupGroupBox.Name = "setupGroupBox";
        setupGroupBox.Size = new Size(1154, 116);
        setupGroupBox.TabIndex = 1;
        setupGroupBox.TabStop = false;
        setupGroupBox.Text = "Twilio setup";
        //
        // setupLayoutPanel
        //
        setupLayoutPanel.AutoSize = true;
        setupLayoutPanel.ColumnCount = 4;
        setupLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setupLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        setupLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setupLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        setupLayoutPanel.Controls.Add(accountSidLabel, 0, 0);
        setupLayoutPanel.Controls.Add(accountSidTextBox, 1, 0);
        setupLayoutPanel.Controls.Add(senderModeLabel, 2, 0);
        setupLayoutPanel.Controls.Add(senderModeComboBox, 3, 0);
        setupLayoutPanel.Controls.Add(authTokenLabel, 0, 1);
        setupLayoutPanel.Controls.Add(authTokenTextBox, 1, 1);
        setupLayoutPanel.Controls.Add(senderValueLabel, 2, 1);
        setupLayoutPanel.Controls.Add(senderValueTextBox, 3, 1);
        setupLayoutPanel.Controls.Add(saveSetupButton, 2, 2);
        setupLayoutPanel.Controls.Add(setupStatusLabel, 3, 2);
        setupLayoutPanel.Dock = DockStyle.Fill;
        setupLayoutPanel.Location = new Point(3, 19);
        setupLayoutPanel.Name = "setupLayoutPanel";
        setupLayoutPanel.RowCount = 3;
        setupLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        setupLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        setupLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        setupLayoutPanel.Size = new Size(1148, 94);
        setupLayoutPanel.TabIndex = 0;
        //
        // accountSidLabel
        //
        accountSidLabel.Anchor = AnchorStyles.Left;
        accountSidLabel.AutoSize = true;
        accountSidLabel.Location = new Point(3, 7);
        accountSidLabel.Name = "accountSidLabel";
        accountSidLabel.Size = new Size(74, 15);
        accountSidLabel.TabIndex = 0;
        accountSidLabel.Text = "Account SID";
        //
        // accountSidTextBox
        //
        accountSidTextBox.Dock = DockStyle.Fill;
        accountSidTextBox.Location = new Point(83, 3);
        accountSidTextBox.Name = "accountSidTextBox";
        accountSidTextBox.Size = new Size(413, 23);
        accountSidTextBox.TabIndex = 1;
        //
        // senderModeLabel
        //
        senderModeLabel.Anchor = AnchorStyles.Left;
        senderModeLabel.AutoSize = true;
        senderModeLabel.Location = new Point(502, 7);
        senderModeLabel.Name = "senderModeLabel";
        senderModeLabel.Size = new Size(75, 15);
        senderModeLabel.TabIndex = 2;
        senderModeLabel.Text = "Sender mode";
        //
        // senderModeComboBox
        //
        senderModeComboBox.Dock = DockStyle.Fill;
        senderModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        senderModeComboBox.FormattingEnabled = true;
        senderModeComboBox.Items.AddRange(new object[]
        {
            "From phone number",
            "Messaging Service SID"
        });
        senderModeComboBox.Location = new Point(583, 3);
        senderModeComboBox.Name = "senderModeComboBox";
        senderModeComboBox.Size = new Size(562, 23);
        senderModeComboBox.TabIndex = 3;
        senderModeComboBox.SelectedIndexChanged += SenderModeComboBox_SelectedIndexChanged;
        //
        // authTokenLabel
        //
        authTokenLabel.Anchor = AnchorStyles.Left;
        authTokenLabel.AutoSize = true;
        authTokenLabel.Location = new Point(3, 36);
        authTokenLabel.Name = "authTokenLabel";
        authTokenLabel.Size = new Size(67, 15);
        authTokenLabel.TabIndex = 4;
        authTokenLabel.Text = "Auth token";
        //
        // authTokenTextBox
        //
        authTokenTextBox.Dock = DockStyle.Fill;
        authTokenTextBox.Location = new Point(83, 32);
        authTokenTextBox.Name = "authTokenTextBox";
        authTokenTextBox.Size = new Size(413, 23);
        authTokenTextBox.TabIndex = 5;
        authTokenTextBox.UseSystemPasswordChar = true;
        //
        // senderValueLabel
        //
        senderValueLabel.Anchor = AnchorStyles.Left;
        senderValueLabel.AutoSize = true;
        senderValueLabel.Location = new Point(502, 36);
        senderValueLabel.Name = "senderValueLabel";
        senderValueLabel.Size = new Size(163, 15);
        senderValueLabel.TabIndex = 6;
        senderValueLabel.Text = "From phone number (E.164)";
        //
        // senderValueTextBox
        //
        senderValueTextBox.Dock = DockStyle.Fill;
        senderValueTextBox.Location = new Point(671, 32);
        senderValueTextBox.Name = "senderValueTextBox";
        senderValueTextBox.Size = new Size(474, 23);
        senderValueTextBox.TabIndex = 7;
        //
        // saveSetupButton
        //
        saveSetupButton.AutoSize = true;
        saveSetupButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        saveSetupButton.Location = new Point(502, 61);
        saveSetupButton.MinimumSize = new Size(0, 36);
        saveSetupButton.Name = "saveSetupButton";
        saveSetupButton.Padding = new Padding(12, 6, 12, 6);
        saveSetupButton.Size = new Size(98, 39);
        saveSetupButton.TabIndex = 8;
        saveSetupButton.Text = "Save setup";
        saveSetupButton.UseVisualStyleBackColor = true;
        saveSetupButton.Click += SaveSetupButton_Click;
        //
        // setupStatusLabel
        //
        setupStatusLabel.Anchor = AnchorStyles.Left;
        setupStatusLabel.AutoSize = true;
        setupStatusLabel.Location = new Point(671, 73);
        setupStatusLabel.Name = "setupStatusLabel";
        setupStatusLabel.Size = new Size(101, 15);
        setupStatusLabel.TabIndex = 9;
        setupStatusLabel.Text = "Setup incomplete.";
        //
        // contactActionsFlowLayoutPanel
        //
        contactActionsFlowLayoutPanel.AutoSize = true;
        contactActionsFlowLayoutPanel.Controls.Add(importButton);
        contactActionsFlowLayoutPanel.Controls.Add(refreshButton);
        contactActionsFlowLayoutPanel.Controls.Add(addContactButton);
        contactActionsFlowLayoutPanel.Controls.Add(editContactButton);
        contactActionsFlowLayoutPanel.Controls.Add(deleteSelectedButton);
        contactActionsFlowLayoutPanel.Controls.Add(saveCsvButton);
        contactActionsFlowLayoutPanel.Controls.Add(selectAllButton);
        contactActionsFlowLayoutPanel.Controls.Add(clearSelectionButton);
        contactActionsFlowLayoutPanel.Controls.Add(contactsStatusLabel);
        contactActionsFlowLayoutPanel.Dock = DockStyle.Fill;
        contactActionsFlowLayoutPanel.Location = new Point(13, 150);
        contactActionsFlowLayoutPanel.Name = "contactActionsFlowLayoutPanel";
        contactActionsFlowLayoutPanel.Size = new Size(1154, 45);
        contactActionsFlowLayoutPanel.TabIndex = 2;
        //
        // importButton
        //
        importButton.AutoSize = true;
        importButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        importButton.Location = new Point(3, 3);
        importButton.MinimumSize = new Size(0, 36);
        importButton.Name = "importButton";
        importButton.Padding = new Padding(12, 6, 12, 6);
        importButton.Size = new Size(97, 39);
        importButton.TabIndex = 0;
        importButton.Text = "Import CSV";
        importButton.UseVisualStyleBackColor = true;
        importButton.Click += ImportButton_Click;
        //
        // refreshButton
        //
        refreshButton.AutoSize = true;
        refreshButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        refreshButton.Location = new Point(106, 3);
        refreshButton.MinimumSize = new Size(0, 36);
        refreshButton.Name = "refreshButton";
        refreshButton.Padding = new Padding(12, 6, 12, 6);
        refreshButton.Size = new Size(80, 39);
        refreshButton.TabIndex = 1;
        refreshButton.Text = "Refresh";
        refreshButton.UseVisualStyleBackColor = true;
        refreshButton.Click += RefreshButton_Click;
        //
        // addContactButton
        //
        addContactButton.AccessibleName = "Add Contact";
        addContactButton.AutoSize = true;
        addContactButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        addContactButton.Location = new Point(192, 3);
        addContactButton.MinimumSize = new Size(0, 36);
        addContactButton.Name = "addContactButton";
        addContactButton.Padding = new Padding(12, 6, 12, 6);
        addContactButton.Size = new Size(102, 39);
        addContactButton.TabIndex = 2;
        addContactButton.Text = "Add Contact";
        addContactButton.UseVisualStyleBackColor = true;
        addContactButton.Click += AddContactButton_Click;
        //
        // editContactButton
        //
        editContactButton.AccessibleName = "Edit Contact";
        editContactButton.AutoSize = true;
        editContactButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        editContactButton.Location = new Point(300, 3);
        editContactButton.MinimumSize = new Size(0, 36);
        editContactButton.Name = "editContactButton";
        editContactButton.Padding = new Padding(12, 6, 12, 6);
        editContactButton.Size = new Size(101, 39);
        editContactButton.TabIndex = 3;
        editContactButton.Text = "Edit Contact";
        editContactButton.UseVisualStyleBackColor = true;
        editContactButton.Click += EditContactButton_Click;
        //
        // deleteSelectedButton
        //
        deleteSelectedButton.AccessibleName = "Delete Selected";
        deleteSelectedButton.AutoSize = true;
        deleteSelectedButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        deleteSelectedButton.Location = new Point(407, 3);
        deleteSelectedButton.MinimumSize = new Size(0, 36);
        deleteSelectedButton.Name = "deleteSelectedButton";
        deleteSelectedButton.Padding = new Padding(12, 6, 12, 6);
        deleteSelectedButton.Size = new Size(113, 39);
        deleteSelectedButton.TabIndex = 4;
        deleteSelectedButton.Text = "Delete Selected";
        deleteSelectedButton.UseVisualStyleBackColor = true;
        deleteSelectedButton.Click += DeleteSelectedButton_Click;
        //
        // saveCsvButton
        //
        saveCsvButton.AccessibleName = "Save CSV";
        saveCsvButton.AutoSize = true;
        saveCsvButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        saveCsvButton.Location = new Point(526, 3);
        saveCsvButton.MinimumSize = new Size(0, 36);
        saveCsvButton.Name = "saveCsvButton";
        saveCsvButton.Padding = new Padding(12, 6, 12, 6);
        saveCsvButton.Size = new Size(87, 39);
        saveCsvButton.TabIndex = 5;
        saveCsvButton.Text = "Save CSV";
        saveCsvButton.UseVisualStyleBackColor = true;
        saveCsvButton.Click += SaveCsvButton_Click;
        //
        // selectAllButton
        //
        selectAllButton.AutoSize = true;
        selectAllButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        selectAllButton.Location = new Point(619, 3);
        selectAllButton.MinimumSize = new Size(0, 36);
        selectAllButton.Name = "selectAllButton";
        selectAllButton.Padding = new Padding(12, 6, 12, 6);
        selectAllButton.Size = new Size(115, 39);
        selectAllButton.TabIndex = 6;
        selectAllButton.Text = "Select All Valid";
        selectAllButton.UseVisualStyleBackColor = true;
        selectAllButton.Click += SelectAllButton_Click;
        //
        // clearSelectionButton
        //
        clearSelectionButton.AutoSize = true;
        clearSelectionButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        clearSelectionButton.Location = new Point(740, 3);
        clearSelectionButton.MinimumSize = new Size(0, 36);
        clearSelectionButton.Name = "clearSelectionButton";
        clearSelectionButton.Padding = new Padding(12, 6, 12, 6);
        clearSelectionButton.Size = new Size(117, 39);
        clearSelectionButton.TabIndex = 7;
        clearSelectionButton.Text = "Clear Selection";
        clearSelectionButton.UseVisualStyleBackColor = true;
        clearSelectionButton.Click += ClearSelectionButton_Click;
        //
        // contactsStatusLabel
        //
        contactsStatusLabel.AutoSize = true;
        contactsStatusLabel.Location = new Point(863, 0);
        contactsStatusLabel.Name = "contactsStatusLabel";
        contactsStatusLabel.Padding = new Padding(12, 7, 0, 0);
        contactsStatusLabel.Size = new Size(79, 22);
        contactsStatusLabel.TabIndex = 8;
        contactsStatusLabel.Text = "0 contacts.";
        //
        // contactsGrid
        //
        contactsGrid.AllowUserToAddRows = false;
        contactsGrid.AllowUserToDeleteRows = false;
        contactsGrid.AllowUserToOrderColumns = false;
        contactsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        contactsGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        contactsGrid.Columns.AddRange(new DataGridViewColumn[]
        {
            selectedColumn,
            nameColumn,
            numberColumn,
            validationColumn,
            errorColumn,
            resultColumn
        });
        contactsGrid.Dock = DockStyle.Fill;
        contactsGrid.Location = new Point(13, 201);
        contactsGrid.MultiSelect = true;
        contactsGrid.Name = "contactsGrid";
        contactsGrid.RowHeadersVisible = false;
        contactsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        contactsGrid.Size = new Size(1154, 279);
        contactsGrid.TabIndex = 3;
        contactsGrid.CellValueChanged += ContactsGrid_CellValueChanged;
        contactsGrid.CurrentCellDirtyStateChanged += ContactsGrid_CurrentCellDirtyStateChanged;
        contactsGrid.SelectionChanged += ContactsGrid_SelectionChanged;
        //
        // selectedColumn
        //
        selectedColumn.FillWeight = 35F;
        selectedColumn.HeaderText = "Selected";
        selectedColumn.Name = "Selected";
        //
        // nameColumn
        //
        nameColumn.HeaderText = "Name";
        nameColumn.Name = "Name";
        nameColumn.ReadOnly = true;
        //
        // numberColumn
        //
        numberColumn.HeaderText = "Number";
        numberColumn.Name = "Number";
        numberColumn.ReadOnly = true;
        //
        // validationColumn
        //
        validationColumn.HeaderText = "Validation";
        validationColumn.Name = "Validation";
        validationColumn.ReadOnly = true;
        //
        // errorColumn
        //
        errorColumn.HeaderText = "Error";
        errorColumn.Name = "Error";
        errorColumn.ReadOnly = true;
        //
        // resultColumn
        //
        resultColumn.HeaderText = "Latest Result";
        resultColumn.Name = "LatestResult";
        resultColumn.ReadOnly = true;
        //
        // messageGroupBox
        //
        messageGroupBox.Controls.Add(messageLayoutPanel);
        messageGroupBox.Dock = DockStyle.Fill;
        messageGroupBox.Location = new Point(13, 486);
        messageGroupBox.Name = "messageGroupBox";
        messageGroupBox.Size = new Size(1154, 179);
        messageGroupBox.TabIndex = 4;
        messageGroupBox.TabStop = false;
        messageGroupBox.Text = "Message";
        //
        // messageLayoutPanel
        //
        messageLayoutPanel.ColumnCount = 1;
        messageLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        messageLayoutPanel.Controls.Add(messageTextBox, 0, 0);
        messageLayoutPanel.Controls.Add(messageCountLabel, 0, 1);
        messageLayoutPanel.Controls.Add(messageErrorLabel, 0, 2);
        messageLayoutPanel.Dock = DockStyle.Fill;
        messageLayoutPanel.Location = new Point(3, 19);
        messageLayoutPanel.Name = "messageLayoutPanel";
        messageLayoutPanel.RowCount = 3;
        messageLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        messageLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        messageLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        messageLayoutPanel.Size = new Size(1148, 157);
        messageLayoutPanel.TabIndex = 0;
        //
        // messageTextBox
        //
        messageTextBox.Dock = DockStyle.Fill;
        messageTextBox.Location = new Point(3, 3);
        messageTextBox.Multiline = true;
        messageTextBox.Name = "messageTextBox";
        messageTextBox.ScrollBars = ScrollBars.Vertical;
        messageTextBox.Size = new Size(1142, 118);
        messageTextBox.TabIndex = 0;
        messageTextBox.TextChanged += MessageTextBox_TextChanged;
        //
        // messageCountLabel
        //
        messageCountLabel.AutoSize = true;
        messageCountLabel.Location = new Point(3, 124);
        messageCountLabel.Name = "messageCountLabel";
        messageCountLabel.Size = new Size(121, 15);
        messageCountLabel.TabIndex = 1;
        messageCountLabel.Text = "0 / 1,600 characters";
        //
        // messageErrorLabel
        //
        messageErrorLabel.AutoSize = true;
        messageErrorLabel.Location = new Point(3, 139);
        messageErrorLabel.Name = "messageErrorLabel";
        messageErrorLabel.Size = new Size(0, 15);
        messageErrorLabel.TabIndex = 2;
        //
        // footerLayoutPanel
        //
        footerLayoutPanel.AutoSize = true;
        footerLayoutPanel.ColumnCount = 1;
        footerLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footerLayoutPanel.Controls.Add(sendActionsFlowLayoutPanel, 0, 0);
        footerLayoutPanel.Controls.Add(batchStatusLabel, 0, 1);
        footerLayoutPanel.Controls.Add(applicationStatusLabel, 0, 2);
        footerLayoutPanel.Dock = DockStyle.Fill;
        footerLayoutPanel.Location = new Point(13, 671);
        footerLayoutPanel.Name = "footerLayoutPanel";
        footerLayoutPanel.RowCount = 3;
        footerLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footerLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footerLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footerLayoutPanel.Size = new Size(1154, 76);
        footerLayoutPanel.TabIndex = 5;
        //
        // sendActionsFlowLayoutPanel
        //
        sendActionsFlowLayoutPanel.AutoSize = true;
        sendActionsFlowLayoutPanel.Controls.Add(sendSelectedButton);
        sendActionsFlowLayoutPanel.Controls.Add(sendAllButton);
        sendActionsFlowLayoutPanel.Controls.Add(cancelButton);
        sendActionsFlowLayoutPanel.Dock = DockStyle.Fill;
        sendActionsFlowLayoutPanel.Location = new Point(3, 3);
        sendActionsFlowLayoutPanel.Name = "sendActionsFlowLayoutPanel";
        sendActionsFlowLayoutPanel.Size = new Size(1148, 45);
        sendActionsFlowLayoutPanel.TabIndex = 0;
        //
        // sendSelectedButton
        //
        sendSelectedButton.AutoSize = true;
        sendSelectedButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        sendSelectedButton.Location = new Point(3, 3);
        sendSelectedButton.MinimumSize = new Size(0, 36);
        sendSelectedButton.Name = "sendSelectedButton";
        sendSelectedButton.Padding = new Padding(12, 6, 12, 6);
        sendSelectedButton.Size = new Size(111, 39);
        sendSelectedButton.TabIndex = 0;
        sendSelectedButton.Text = "Send Selected";
        sendSelectedButton.UseVisualStyleBackColor = true;
        sendSelectedButton.Click += SendSelectedButton_Click;
        //
        // sendAllButton
        //
        sendAllButton.AutoSize = true;
        sendAllButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        sendAllButton.Location = new Point(120, 3);
        sendAllButton.MinimumSize = new Size(0, 36);
        sendAllButton.Name = "sendAllButton";
        sendAllButton.Padding = new Padding(12, 6, 12, 6);
        sendAllButton.Size = new Size(107, 39);
        sendAllButton.TabIndex = 1;
        sendAllButton.Text = "Send All Valid";
        sendAllButton.UseVisualStyleBackColor = true;
        sendAllButton.Click += SendAllButton_Click;
        //
        // cancelButton
        //
        cancelButton.AutoSize = true;
        cancelButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        cancelButton.Location = new Point(233, 3);
        cancelButton.MinimumSize = new Size(0, 36);
        cancelButton.Name = "cancelButton";
        cancelButton.Padding = new Padding(12, 6, 12, 6);
        cancelButton.Size = new Size(76, 39);
        cancelButton.TabIndex = 2;
        cancelButton.Text = "Cancel";
        cancelButton.UseVisualStyleBackColor = true;
        cancelButton.Click += CancelButton_Click;
        //
        // batchStatusLabel
        //
        batchStatusLabel.AutoSize = true;
        batchStatusLabel.Location = new Point(3, 51);
        batchStatusLabel.Name = "batchStatusLabel";
        batchStatusLabel.Size = new Size(0, 15);
        batchStatusLabel.TabIndex = 1;
        //
        // applicationStatusLabel
        //
        applicationStatusLabel.AutoSize = true;
        applicationStatusLabel.Location = new Point(3, 66);
        applicationStatusLabel.Name = "applicationStatusLabel";
        applicationStatusLabel.Size = new Size(0, 15);
        applicationStatusLabel.TabIndex = 2;
        //
        // MainForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1180, 760);
        Controls.Add(rootLayoutPanel);
        MinimumSize = new Size(900, 650);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Husaynia SMS";
        FormClosing += MainForm_FormClosing;
        Shown += MainForm_Shown;
        rootLayoutPanel.ResumeLayout(false);
        rootLayoutPanel.PerformLayout();
        setupGroupBox.ResumeLayout(false);
        setupGroupBox.PerformLayout();
        setupLayoutPanel.ResumeLayout(false);
        setupLayoutPanel.PerformLayout();
        contactActionsFlowLayoutPanel.ResumeLayout(false);
        contactActionsFlowLayoutPanel.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)contactsGrid).EndInit();
        messageGroupBox.ResumeLayout(false);
        messageLayoutPanel.ResumeLayout(false);
        messageLayoutPanel.PerformLayout();
        footerLayoutPanel.ResumeLayout(false);
        footerLayoutPanel.PerformLayout();
        sendActionsFlowLayoutPanel.ResumeLayout(false);
        sendActionsFlowLayoutPanel.PerformLayout();
        ResumeLayout(false);
    }
}
