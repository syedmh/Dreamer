#nullable enable

namespace HusayniaSMS.WinForms.Forms;

partial class ContactDialog
{
    private const int MaximumFieldCharacters = 4_096;
    private System.ComponentModel.IContainer? components = null;
    private TableLayoutPanel contactLayoutPanel = null!;
    private Label nameLabel = null!;
    private TextBox nameTextBox = null!;
    private Label numberLabel = null!;
    private TextBox numberTextBox = null!;
    private FlowLayoutPanel actionsFlowLayoutPanel = null!;
    private Button okButton = null!;
    private Button cancelButton = null!;
    private ErrorProvider validationErrors = null!;

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
        contactLayoutPanel = new TableLayoutPanel();
        nameLabel = new Label();
        nameTextBox = new TextBox();
        numberLabel = new Label();
        numberTextBox = new TextBox();
        actionsFlowLayoutPanel = new FlowLayoutPanel();
        cancelButton = new Button();
        okButton = new Button();
        validationErrors = new ErrorProvider(components);
        contactLayoutPanel.SuspendLayout();
        actionsFlowLayoutPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)validationErrors).BeginInit();
        SuspendLayout();
        //
        // contactLayoutPanel
        //
        contactLayoutPanel.ColumnCount = 3;
        contactLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        contactLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        contactLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32F));
        contactLayoutPanel.Controls.Add(nameLabel, 0, 0);
        contactLayoutPanel.Controls.Add(nameTextBox, 1, 0);
        contactLayoutPanel.Controls.Add(numberLabel, 0, 1);
        contactLayoutPanel.Controls.Add(numberTextBox, 1, 1);
        contactLayoutPanel.Controls.Add(actionsFlowLayoutPanel, 0, 2);
        contactLayoutPanel.Dock = DockStyle.Fill;
        contactLayoutPanel.Location = new Point(0, 0);
        contactLayoutPanel.Name = "contactLayoutPanel";
        contactLayoutPanel.Padding = new Padding(12);
        contactLayoutPanel.RowCount = 3;
        contactLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contactLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contactLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        contactLayoutPanel.Size = new Size(520, 190);
        contactLayoutPanel.TabIndex = 0;
        contactLayoutPanel.SetColumnSpan(actionsFlowLayoutPanel, 3);
        //
        // nameLabel
        //
        nameLabel.AccessibleName = "Contact name";
        nameLabel.Anchor = AnchorStyles.Left;
        nameLabel.AutoSize = true;
        nameLabel.Location = new Point(15, 18);
        nameLabel.Name = "nameLabel";
        nameLabel.Size = new Size(39, 15);
        nameLabel.TabIndex = 0;
        nameLabel.Text = "&Name";
        //
        // nameTextBox
        //
        nameTextBox.AccessibleName = "Contact name";
        nameTextBox.Dock = DockStyle.Fill;
        validationErrors.SetIconAlignment(nameTextBox, ErrorIconAlignment.MiddleRight);
        validationErrors.SetIconPadding(nameTextBox, 4);
        nameTextBox.Location = new Point(112, 15);
        nameTextBox.MaxLength = MaximumFieldCharacters;
        nameTextBox.Name = "nameTextBox";
        nameTextBox.Size = new Size(361, 23);
        nameTextBox.TabIndex = 1;
        nameTextBox.TextChanged += Draft_TextChanged;
        //
        // numberLabel
        //
        numberLabel.AccessibleName = "Contact number";
        numberLabel.Anchor = AnchorStyles.Left;
        numberLabel.AutoSize = true;
        numberLabel.Location = new Point(15, 47);
        numberLabel.Name = "numberLabel";
        numberLabel.Size = new Size(91, 15);
        numberLabel.TabIndex = 2;
        numberLabel.Text = "N&umber (E.164)";
        //
        // numberTextBox
        //
        numberTextBox.AccessibleName = "Contact number in E.164 format";
        numberTextBox.Dock = DockStyle.Fill;
        validationErrors.SetIconAlignment(numberTextBox, ErrorIconAlignment.MiddleRight);
        validationErrors.SetIconPadding(numberTextBox, 4);
        numberTextBox.Location = new Point(112, 44);
        numberTextBox.MaxLength = MaximumFieldCharacters;
        numberTextBox.Name = "numberTextBox";
        numberTextBox.Size = new Size(361, 23);
        numberTextBox.TabIndex = 3;
        numberTextBox.TextChanged += Draft_TextChanged;
        //
        // actionsFlowLayoutPanel
        //
        actionsFlowLayoutPanel.AutoSize = true;
        actionsFlowLayoutPanel.Controls.Add(cancelButton);
        actionsFlowLayoutPanel.Controls.Add(okButton);
        actionsFlowLayoutPanel.Dock = DockStyle.Fill;
        actionsFlowLayoutPanel.FlowDirection = FlowDirection.RightToLeft;
        actionsFlowLayoutPanel.Location = new Point(15, 73);
        actionsFlowLayoutPanel.Name = "actionsFlowLayoutPanel";
        actionsFlowLayoutPanel.Size = new Size(490, 102);
        actionsFlowLayoutPanel.TabIndex = 4;
        actionsFlowLayoutPanel.WrapContents = false;
        //
        // cancelButton
        //
        cancelButton.AccessibleName = "Cancel contact changes";
        cancelButton.AutoSize = true;
        cancelButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Location = new Point(411, 3);
        cancelButton.MinimumSize = new Size(0, 36);
        cancelButton.Name = "cancelButton";
        cancelButton.Padding = new Padding(12, 6, 12, 6);
        cancelButton.Size = new Size(76, 39);
        cancelButton.TabIndex = 1;
        cancelButton.Text = "Cancel";
        cancelButton.UseVisualStyleBackColor = true;
        //
        // okButton
        //
        okButton.AccessibleName = "Add contact";
        okButton.AutoSize = true;
        okButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        okButton.Enabled = false;
        okButton.Location = new Point(349, 3);
        okButton.MinimumSize = new Size(0, 36);
        okButton.Name = "okButton";
        okButton.Padding = new Padding(12, 6, 12, 6);
        okButton.Size = new Size(56, 39);
        okButton.TabIndex = 0;
        okButton.Text = "Add";
        okButton.UseVisualStyleBackColor = true;
        okButton.Click += OkButton_Click;
        //
        // validationErrors
        //
        validationErrors.BlinkStyle = ErrorBlinkStyle.NeverBlink;
        validationErrors.ContainerControl = this;
        //
        // ContactDialog
        //
        AcceptButton = okButton;
        AccessibleName = "Add Contact";
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        CancelButton = cancelButton;
        ClientSize = new Size(520, 190);
        Controls.Add(contactLayoutPanel);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        MinimumSize = new Size(540, 230);
        Name = "ContactDialog";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Add Contact";
        Shown += ContactDialog_Shown;
        contactLayoutPanel.ResumeLayout(false);
        contactLayoutPanel.PerformLayout();
        actionsFlowLayoutPanel.ResumeLayout(false);
        actionsFlowLayoutPanel.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)validationErrors).EndInit();
        ResumeLayout(false);
    }
}
