using FluentValidation;
using WillVault.Application.DTOs.Vault;

namespace WillVault.Application.Validators;

public class CreateVaultItemRequestValidator : AbstractValidator<CreateVaultItemRequest>
{
    public CreateVaultItemRequestValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(250).WithMessage("Title must not exceed 250 characters.");

        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Description must not exceed 2000 characters.")
            .When(x => x.Description is not null);

        RuleFor(x => x.ItemType)
            .IsInEnum().WithMessage("A valid item type is required.");

        RuleFor(x => x.ContentText)
            .MaximumLength(50000).WithMessage("Content text must not exceed 50000 characters.")
            .When(x => x.ContentText is not null);
    }
}
