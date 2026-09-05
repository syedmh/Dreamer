using HusayniaSMS.Core.Contacts;
using HusayniaSMS.WinForms.Forms;
using HusayniaSMS.WinForms.Presentation;
using System.Runtime.ExceptionServices;

namespace HusayniaSMS.Tests.Presentation;

[TestClass]
public sealed class ContactDialogTests
{
    [TestMethod]
    public void AddDialogHasAccessibleFieldsLimitsAndDisabledOkUntilValid() =>
        RunSta(() =>
        {
            using var dialog = Create(
                ContactDialogMode.Add,
                new(null, null),
                [],
                null);

            Assert.AreEqual("Add Contact", dialog.Text);
            Assert.AreEqual("Contact name", dialog.NameTextBox.AccessibleName);
            Assert.AreEqual(
                "Contact number in E.164 format",
                dialog.NumberTextBox.AccessibleName);
            Assert.AreEqual(4_096, dialog.NameTextBox.MaxLength);
            Assert.AreEqual(4_096, dialog.NumberTextBox.MaxLength);
            Assert.IsFalse(dialog.OkButton.Enabled);
            Assert.AreEqual("Name is required.",
                dialog.ValidationErrors.GetError(dialog.NameTextBox));
            Assert.AreEqual("Number is required.",
                dialog.ValidationErrors.GetError(dialog.NumberTextBox));
            Assert.AreEqual(
                "Name is required.",
                dialog.NameTextBox.AccessibilityObject.Description);
            Assert.AreEqual(
                "Number is required.",
                dialog.NumberTextBox.AccessibilityObject.Description);
            Assert.AreSame(dialog.OkButton, dialog.AcceptButton);
            Assert.AreSame(dialog.CancelActionButton, dialog.CancelButton);
            Assert.AreEqual(DialogResult.Cancel, dialog.CancelActionButton.DialogResult);
        });

    [TestMethod]
    public void DialogShowsInvalidAndDuplicateErrorsThenReturnsTrimmedValidResult() =>
        RunSta(() =>
        {
            var rows = new[]
            {
                new ContactRow(1, "Existing", "+15550100100", Array.Empty<ContactErrorCode>())
            };
            using var dialog = Create(
                ContactDialogMode.Add,
                new("", ""),
                rows,
                null);

            dialog.NameTextBox.Text = " New ";
            dialog.NumberTextBox.Text = "not-a-number";
            Assert.IsFalse(dialog.OkButton.Enabled);
            Assert.AreEqual(
                "Number must be in E.164 format, for example +15550100100.",
                dialog.ValidationErrors.GetError(dialog.NumberTextBox));

            dialog.NumberTextBox.Text = " +15550100100 ";
            Assert.IsFalse(dialog.OkButton.Enabled);
            Assert.AreEqual(
                "Another contact already uses this number.",
                dialog.ValidationErrors.GetError(dialog.NumberTextBox));

            dialog.NumberTextBox.Text = " +15550100101 ";
            Assert.IsTrue(dialog.OkButton.Enabled);
            Assert.AreEqual(
                string.Empty,
                dialog.NameTextBox.AccessibilityObject.Description);
            Assert.AreEqual(
                string.Empty,
                dialog.NumberTextBox.AccessibilityObject.Description);
            dialog.Show();
            Application.DoEvents();
            dialog.OkButton.PerformClick();
            Assert.AreEqual(DialogResult.OK, dialog.DialogResult);
            Assert.AreEqual(new ContactDialogResult("New", "+15550100101"), dialog.ContactResult);
        });

    [TestMethod]
    public void EditDialogExcludesSelfButStillRejectsAnotherOrdinal() =>
        RunSta(() =>
        {
            var rows = new[]
            {
                new ContactRow(1, "First", "+15550100100", Array.Empty<ContactErrorCode>()),
                new ContactRow(2, "Second", "+15550100101", Array.Empty<ContactErrorCode>())
            };
            using var dialog = Create(
                ContactDialogMode.Edit,
                new(" First ", " +15550100100 "),
                rows,
                1);

            Assert.AreEqual("Edit Contact", dialog.Text);
            Assert.IsTrue(dialog.OkButton.Enabled);
            dialog.NumberTextBox.Text = "+15550100101";
            Assert.IsFalse(dialog.OkButton.Enabled);
            dialog.NumberTextBox.Text = "+15550100100";
            Assert.IsTrue(dialog.OkButton.Enabled);
        });

    [TestMethod]
    public void DialogShowsFormulaPrefixErrorOnNameAndKeepsOkDisabled() =>
        RunSta(() =>
        {
            using var dialog = Create(
                ContactDialogMode.Add,
                new(null, "+15550100100"),
                [],
                null);

            dialog.NameTextBox.Text = "\t=HYPERLINK(\"https://example.invalid\")";

            Assert.IsFalse(dialog.OkButton.Enabled);
            Assert.AreEqual(
                "Name cannot begin with =, +, -, or @.",
                dialog.ValidationErrors.GetError(dialog.NameTextBox));
            Assert.AreEqual(
                string.Empty,
                dialog.ValidationErrors.GetError(dialog.NumberTextBox));
            Assert.AreEqual(
                "Name cannot begin with =, +, -, or @.",
                dialog.NameTextBox.AccessibilityObject.Description);
            Assert.AreEqual(
                string.Empty,
                dialog.NumberTextBox.AccessibilityObject.Description);
        });

    [TestMethod]
    public void ParameterlessDialogIsInertDisplayableAndDisposable() =>
        RunSta(() =>
        {
            using var dialog = new ContactDialog();

            Assert.IsFalse(dialog.OkButton.Enabled);
            Assert.IsNull(dialog.ContactResult);
            Assert.AreEqual(string.Empty, dialog.ValidationErrors.GetError(dialog.NameTextBox));
            Assert.AreEqual(string.Empty, dialog.ValidationErrors.GetError(dialog.NumberTextBox));

            dialog.Show();
            Application.DoEvents();
            Assert.IsTrue(dialog.Visible);
            dialog.Close();
            Application.DoEvents();
            Assert.IsFalse(dialog.Visible);
            Assert.IsNull(dialog.ContactResult);
        });

    [TestMethod]
    [DataRow(0d)]
    [DataRow(12d)]
    [DataRow(16d)]
    public void InvalidDialogReservesErrorIconsAndFitsAtNormalAndMinimumSize(double fontSize) =>
        RunSta(() =>
        {
            using var dialog = Create(
                ContactDialogMode.Add,
                new(null, null),
                [],
                null);
            if (fontSize > 0)
            {
                dialog.Font = new Font(dialog.Font.FontFamily, (float)fontSize);
            }

            dialog.Show();
            Application.DoEvents();
            var normalSize = dialog.Size;
            AssertDialogFits(dialog);

            dialog.Size = dialog.MinimumSize;
            dialog.PerformLayout();
            Application.DoEvents();
            AssertDialogFits(dialog);

            Assert.IsTrue(normalSize.Width >= dialog.MinimumSize.Width);
            Assert.IsTrue(dialog.ClientSize.Width >= 500);
            Assert.IsTrue(dialog.ClientSize.Height >= 170);
        });

    [TestMethod]
    [DataRow(0d)]
    [DataRow(12d)]
    [DataRow(16d)]
    public void DisplayedDialogButtonsUseSharedDpiSafeSizing(double fontSize) =>
        RunSta(() =>
        {
            using var dialog = Create(
                ContactDialogMode.Add,
                new("A", "+15550100100"),
                [],
                null);
            if (fontSize > 0)
            {
                dialog.Font = new Font(dialog.Font.FontFamily, (float)fontSize);
            }

            dialog.Show();
            dialog.PerformLayout();
            Application.DoEvents();

            foreach (var button in new[] { dialog.OkButton, dialog.CancelActionButton })
            {
                var text = TextRenderer.MeasureText(
                    button.Text,
                    button.Font,
                    Size.Empty,
                    TextFormatFlags.SingleLine);
                var preferred = button.GetPreferredSize(Size.Empty);
                Assert.IsTrue(dialog.DeviceDpi > 0);
                Assert.IsTrue(button.AutoSize);
                Assert.AreEqual(AutoSizeMode.GrowAndShrink, button.AutoSizeMode);
                Assert.AreEqual(36, button.MinimumSize.Height);
                Assert.AreEqual(new Padding(12, 6, 12, 6), button.Padding);
                Assert.IsFalse(string.IsNullOrWhiteSpace(button.AccessibleName));
                Assert.IsTrue(preferred.Width >= text.Width + button.Padding.Horizontal);
                Assert.IsTrue(preferred.Height >= text.Height + button.Padding.Vertical);
                Assert.IsTrue(button.Width >= preferred.Width);
                Assert.IsTrue(button.Height >= preferred.Height);
                Assert.IsTrue(
                    button.ClientSize.Width - button.Padding.Horizontal >= text.Width);
                Assert.IsTrue(
                    button.ClientSize.Height - button.Padding.Vertical >= text.Height);
            }
        });

    private static ContactDialog Create(
        ContactDialogMode mode,
        ContactDraft draft,
        IReadOnlyList<ContactRow> rows,
        int? editingOrdinal) =>
        new(
            new(mode, draft, rows, editingOrdinal),
            new ContactDraftValidator(new E164PhoneNumberValidator()));

    private static void AssertDialogFits(ContactDialog dialog)
    {
        Assert.AreEqual(
            ErrorIconAlignment.MiddleRight,
            dialog.ValidationErrors.GetIconAlignment(dialog.NameTextBox));
        Assert.AreEqual(
            ErrorIconAlignment.MiddleRight,
            dialog.ValidationErrors.GetIconAlignment(dialog.NumberTextBox));
        Assert.AreEqual(4, dialog.ValidationErrors.GetIconPadding(dialog.NameTextBox));
        Assert.AreEqual(4, dialog.ValidationErrors.GetIconPadding(dialog.NumberTextBox));
        Assert.AreEqual(ErrorBlinkStyle.NeverBlink, dialog.ValidationErrors.BlinkStyle);
        Assert.AreSame(dialog, dialog.ValidationErrors.ContainerControl);

        foreach (var textBox in new[] { dialog.NameTextBox, dialog.NumberTextBox })
        {
            var icon = dialog.ValidationErrors.Icon;
            var origin = dialog.PointToClient(textBox.PointToScreen(Point.Empty));
            var iconBounds = new Rectangle(
                origin.X + textBox.Width +
                    dialog.ValidationErrors.GetIconPadding(textBox),
                origin.Y + ((textBox.Height - icon.Height) / 2),
                icon.Width,
                icon.Height);
            Assert.IsTrue(
                dialog.ClientRectangle.Contains(iconBounds),
                $"Error icon {iconBounds} for {textBox.Name} must fit within " +
                $"{dialog.ClientRectangle} at {dialog.DeviceDpi} DPI.");
            Assert.IsTrue(
                iconBounds.Left >= origin.X + textBox.Width,
                $"Error icon for {textBox.Name} must be reserved to the right of the editor.");
        }

        foreach (var controlName in new[]
        {
            "nameLabel",
            "nameTextBox",
            "numberLabel",
            "numberTextBox",
            "okButton",
            "cancelButton"
        })
        {
            var control = FindControl(dialog, controlName);
            Assert.IsNotNull(control, controlName);
            var origin = dialog.PointToClient(control.PointToScreen(Point.Empty));
            var bounds = new Rectangle(origin, control.Size);
            Assert.IsTrue(
                dialog.ClientRectangle.Contains(bounds),
                $"{controlName} {bounds} must fit within {dialog.ClientRectangle}.");
            Assert.IsTrue(control.Visible, controlName);
        }

        Assert.IsTrue(dialog.ContactLayoutPanel.ColumnCount == 3);
        var columnWidths = dialog.ContactLayoutPanel.GetColumnWidths();
        Assert.AreEqual(3, columnWidths.Length);
        Assert.IsTrue(
            columnWidths[2] >= dialog.ValidationErrors.Icon.Width +
                dialog.ValidationErrors.GetIconPadding(dialog.NameTextBox),
            $"The error gutter width {columnWidths[2]} must reserve the icon and padding.");
    }

    private static Control? FindControl(Control root, string name)
    {
        if (string.Equals(root.Name, name, StringComparison.Ordinal))
        {
            return root;
        }

        foreach (Control child in root.Controls)
        {
            var result = FindControl(child, name);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
