using System.ComponentModel;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.WinForms.Presentation;

namespace HusayniaSMS.WinForms.Forms;

public sealed partial class ContactDialog : Form
{
    private ContactDialogRequest? _request;
    private IContactDraftValidator? _validator;
    private bool _runtimeConfigured;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public ContactDialog()
    {
        InitializeComponent();
    }

    public ContactDialog(
        ContactDialogRequest request,
        IContactDraftValidator validator)
        : this()
    {
        ConfigureRuntime(request, validator);
    }

    public ContactDialogResult? ContactResult { get; private set; }

    internal TextBox NameTextBox => nameTextBox;
    internal TextBox NumberTextBox => numberTextBox;
    internal Button OkButton => okButton;
    internal Button CancelActionButton => cancelButton;
    internal ErrorProvider ValidationErrors => validationErrors;
    internal TableLayoutPanel ContactLayoutPanel => contactLayoutPanel;

    private void ConfigureRuntime(
        ContactDialogRequest request,
        IContactDraftValidator validator)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));

        Text = request.Mode == ContactDialogMode.Add ? "Add Contact" : "Edit Contact";
        AccessibleName = Text;
        okButton.Text = request.Mode == ContactDialogMode.Add ? "Add" : "Save";
        okButton.AccessibleName = request.Mode == ContactDialogMode.Add
            ? "Add contact"
            : "Save contact";
        nameTextBox.Text = request.InitialDraft.Name ?? string.Empty;
        numberTextBox.Text = request.InitialDraft.Number ?? string.Empty;

        _runtimeConfigured = true;
        ValidateDraft();
    }

    private void Draft_TextChanged(object? sender, EventArgs e)
    {
        if (_runtimeConfigured)
        {
            ValidateDraft();
        }
    }

    private void OkButton_Click(object? sender, EventArgs e)
    {
        if (!_runtimeConfigured)
        {
            DialogResult = DialogResult.None;
            return;
        }

        var validation = ValidateDraft();
        if (!validation.IsValid)
        {
            DialogResult = DialogResult.None;
            return;
        }

        ContactResult = new(validation.Name, validation.Number);
        DialogResult = DialogResult.OK;
    }

    private void ContactDialog_Shown(object? sender, EventArgs e)
    {
        if (_runtimeConfigured)
        {
            nameTextBox.Focus();
        }
    }

    private ContactDraftValidation ValidateDraft()
    {
        var request = _request ??
            throw new InvalidOperationException("The contact dialog is not runtime configured.");
        var validator = _validator ??
            throw new InvalidOperationException("The contact dialog is not runtime configured.");
        var validation = validator.Validate(
            new(nameTextBox.Text, numberTextBox.Text),
            request.CurrentRows,
            request.EditingOrdinal);
        var nameErrors = validation.Errors
            .Where(error => error is
                ContactErrorCode.NameRequired or
                ContactErrorCode.FormulaPrefixNotAllowed)
            .Select(ContactValidationMessages.GetMessage);
        var numberErrors = validation.Errors
            .Where(error => error is not
                ContactErrorCode.NameRequired and not
                ContactErrorCode.FormulaPrefixNotAllowed)
            .Select(ContactValidationMessages.GetMessage);
        var nameError = string.Join(Environment.NewLine, nameErrors);
        var numberError = string.Join(Environment.NewLine, numberErrors);
        validationErrors.SetError(nameTextBox, nameError);
        validationErrors.SetError(numberTextBox, numberError);
        nameTextBox.AccessibleDescription = nameError;
        numberTextBox.AccessibleDescription = numberError;
        okButton.Enabled = validation.IsValid;
        return validation;
    }
}
