#nullable enable

namespace TCFPreview.WinForms;

partial class MainForm
{
    private System.ComponentModel.IContainer? components;
    private TextBox sourcePathTextBox = null!;
    private Button browseSourceButton = null!;
    private TextBox destinationPathTextBox = null!;
    private Button browseDestinationButton = null!;
    private PictureBox previewPictureBox = null!;
    private Label filenameLabel = null!;
    private Label positionLabel = null!;
    private StatusLabel statusLabel = null!;
    private Button previousButton = null!;
    private Button nextButton = null!;
    private Button copyButton = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            previewPictureBox?.Image?.Dispose();
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        TableLayoutPanel rootLayout = new();
        TableLayoutPanel sourceLayout = new();
        TableLayoutPanel destinationLayout = new();
        TableLayoutPanel detailsLayout = new();
        FlowLayoutPanel actionLayout = new();
        Label sourceLabel = new();
        Label destinationLabel = new();
        sourcePathTextBox = new TextBox();
        browseSourceButton = new Button();
        destinationPathTextBox = new TextBox();
        browseDestinationButton = new Button();
        previewPictureBox = new PictureBox();
        filenameLabel = new Label();
        positionLabel = new Label();
        statusLabel = new StatusLabel();
        previousButton = new Button();
        nextButton = new Button();
        copyButton = new Button();
        rootLayout.SuspendLayout();
        sourceLayout.SuspendLayout();
        destinationLayout.SuspendLayout();
        detailsLayout.SuspendLayout();
        actionLayout.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)previewPictureBox).BeginInit();
        SuspendLayout();

        rootLayout.ColumnCount = 1;
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.Controls.Add(sourceLayout, 0, 0);
        rootLayout.Controls.Add(destinationLayout, 0, 1);
        rootLayout.Controls.Add(previewPictureBox, 0, 2);
        rootLayout.Controls.Add(detailsLayout, 0, 3);
        rootLayout.Controls.Add(statusLabel, 0, 4);
        rootLayout.Controls.Add(actionLayout, 0, 5);
        rootLayout.Dock = DockStyle.Fill;
        rootLayout.Padding = new Padding(12);
        rootLayout.RowCount = 6;
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        ConfigurePathLayout(sourceLayout, sourceLabel, sourcePathTextBox, browseSourceButton);
        sourceLabel.Text = "Source:";
        browseSourceButton.Text = "Browse...";
        browseSourceButton.Click += BrowseSourceButton_Click;

        ConfigurePathLayout(destinationLayout, destinationLabel, destinationPathTextBox, browseDestinationButton);
        destinationLabel.Text = "Destination:";
        browseDestinationButton.Text = "Browse...";
        browseDestinationButton.Click += BrowseDestinationButton_Click;

        previewPictureBox.BackColor = Color.Black;
        previewPictureBox.Dock = DockStyle.Fill;
        previewPictureBox.Margin = new Padding(0, 12, 0, 12);
        previewPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
        previewPictureBox.TabStop = false;

        detailsLayout.AutoSize = true;
        detailsLayout.ColumnCount = 2;
        detailsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        detailsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailsLayout.Controls.Add(filenameLabel, 0, 0);
        detailsLayout.Controls.Add(positionLabel, 1, 0);
        detailsLayout.Dock = DockStyle.Fill;
        filenameLabel.AutoEllipsis = true;
        filenameLabel.AutoSize = true;
        filenameLabel.Font = new Font(filenameLabel.Font, FontStyle.Bold);
        positionLabel.AutoSize = true;
        positionLabel.Margin = new Padding(12, 0, 0, 0);

        statusLabel.AutoEllipsis = true;
        statusLabel.AutoSize = true;
        statusLabel.AccessibleName = "Status";
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.LiveSetting =
            System.Windows.Forms.Automation.AutomationLiveSetting.Polite;
        statusLabel.Margin = new Padding(0, 8, 0, 8);

        actionLayout.AutoSize = true;
        actionLayout.Controls.Add(previousButton);
        actionLayout.Controls.Add(nextButton);
        actionLayout.Controls.Add(copyButton);
        actionLayout.Dock = DockStyle.Fill;
        actionLayout.FlowDirection = FlowDirection.LeftToRight;
        actionLayout.WrapContents = false;
        previousButton.Text = "Previous";
        previousButton.AutoSize = true;
        previousButton.Click += PreviousButton_Click;
        nextButton.Text = "Next";
        nextButton.AutoSize = true;
        nextButton.Click += NextButton_Click;
        copyButton.Text = "Copy";
        copyButton.AutoSize = true;
        copyButton.Margin = new Padding(18, 3, 3, 3);
        copyButton.Click += CopyButton_Click;

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(960, 700);
        Controls.Add(rootLayout);
        MinimumSize = new Size(640, 480);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "TCF Photo Preview";
        rootLayout.ResumeLayout(false);
        rootLayout.PerformLayout();
        sourceLayout.ResumeLayout(false);
        sourceLayout.PerformLayout();
        destinationLayout.ResumeLayout(false);
        destinationLayout.PerformLayout();
        detailsLayout.ResumeLayout(false);
        detailsLayout.PerformLayout();
        actionLayout.ResumeLayout(false);
        actionLayout.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)previewPictureBox).EndInit();
        ResumeLayout(false);
    }

    private static void ConfigurePathLayout(
        TableLayoutPanel layout,
        Label label,
        TextBox textBox,
        Button button)
    {
        layout.AutoSize = true;
        layout.ColumnCount = 3;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(textBox, 1, 0);
        layout.Controls.Add(button, 2, 0);
        layout.Dock = DockStyle.Fill;
        layout.Margin = new Padding(0, 0, 0, 6);
        label.Anchor = AnchorStyles.Left;
        label.AutoSize = true;
        label.Margin = new Padding(0, 0, 8, 0);
        textBox.Dock = DockStyle.Fill;
        textBox.ReadOnly = true;
        textBox.Margin = new Padding(0, 0, 8, 0);
        button.AutoSize = true;
    }
}
