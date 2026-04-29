using FluentValidation;
using WillVault.Application.DTOs.Recipients;

namespace WillVault.Application.Validators;

public class CreateRecipientRequestValidator : AbstractValidator<CreateRecipientRequest>
{
    public CreateRecipientRequestValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(200).WithMessage("Full name must not exceed 200 characters.");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.")
            .MaximumLength(256).WithMessage("Email must not exceed 256 characters.");

        RuleFor(x => x.Phone)
            .MaximumLength(20).WithMessage("Phone number must not exceed 20 characters.")
            .When(x => x.Phone is not null);

        RuleFor(x => x.Relationship)
            .MaximumLength(100).WithMessage("Relationship must not exceed 100 characters.")
            .When(x => x.Relationship is not null);
    }
}
