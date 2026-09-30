using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;
using Husaynia.Domain.Forms;

namespace Husaynia.Application.Tests.Forms;

public sealed class FormSubmissionValidationTests
{
    [Fact]
    public void ValidatesEveryBoundedFieldKindAndProducesStableCanonicalHash()
    {
        var definition = SyntheticDefinition();
        var command = new FormSubmissionCommand(
            definition.FormKey,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "  Test Person  ",
                ["email"] = "person@example.test",
                ["telephone"] = "+1 (555) 010-1000",
                ["message"] = "Synthetic message",
                ["amount"] = "25.50",
                ["topic"] = "general",
                ["consent"] = "true",
            },
            "test-consent-v1");
        var validator = new FormSubmissionValidator(new FormsOptions());

        var first = validator.Validate(command, definition);
        var second = validator.Validate(command, definition);

        Assert.True(first.IsValid);
        Assert.Empty(first.Errors);
        Assert.Equal("Test Person", first.Values["name"]);
        Assert.Equal(32, first.CanonicalPayloadHash.Length);
        Assert.Equal(first.CanonicalPayloadHash, second.CanonicalPayloadHash);
    }

    [Fact]
    public void RejectsUnknownMalformedAndOversizedFieldsWithAccessibleErrors()
    {
        var definition = SyntheticDefinition();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = new('x', 201),
            ["email"] = "not-an-email",
            ["telephone"] = "abc",
            ["message"] = "ok",
            ["amount"] = "-1",
            ["topic"] = "unknown",
            ["consent"] = "false",
            ["unexpected"] = "value",
        };
        var validator = new FormSubmissionValidator(new FormsOptions());

        var result = validator.Validate(
            new FormSubmissionCommand(definition.FormKey, fields, "wrong-consent"),
            definition);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.FieldKey == "name");
        Assert.Contains(result.Errors, error => error.FieldKey == "email");
        Assert.Contains(result.Errors, error => error.FieldKey == "unexpected");
        Assert.Contains(result.Errors, error => error.Code == "consent_version_mismatch");
    }

    [Fact]
    public void SyntheticPledgeFixtureUsesOnlyGenericVersionedRules()
    {
        var versionId = Guid.NewGuid();
        var definition = new FormDefinitionView(
            Guid.NewGuid(),
            "test-pledge-v1",
            "Synthetic pledge",
            versionId,
            1,
            "test-pledge-consent-v1",
            [
                new FormFieldView(
                    Guid.NewGuid(),
                    versionId,
                    "amount",
                    "Synthetic amount",
                    FormFieldKind.Decimal,
                    true,
                    null,
                    null,
                    1,
                    1_000,
                    FormPatternKind.None,
                    [],
                    1,
                    FormPrivacyClass.Standard),
                Field(
                    versionId,
                    "consent",
                    FormFieldKind.Consent,
                    2,
                    min: null,
                    max: null),
            ],
            new RowVersion(new byte[8]));
        var result = new FormSubmissionValidator(new FormsOptions()).Validate(
            new FormSubmissionCommand(
                definition.FormKey,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["amount"] = "100.00",
                    ["consent"] = "true",
                },
                definition.ConsentVersion),
            definition);

        Assert.True(result.IsValid);
        Assert.Equal(["amount", "consent"], result.Values.Keys);
    }

    private static FormDefinitionView SyntheticDefinition()
    {
        var definitionId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        return new FormDefinitionView(
            definitionId,
            "test-contact-v1",
            "Synthetic contact",
            versionId,
            1,
            "test-consent-v1",
            [
                Field(versionId, "name", FormFieldKind.Text, 1, min: 1, max: 200),
                Field(versionId, "email", FormFieldKind.Email, 2, min: 3, max: 320, FormPatternKind.Email),
                Field(versionId, "telephone", FormFieldKind.Telephone, 3, min: 7, max: 40, FormPatternKind.Telephone),
                Field(versionId, "message", FormFieldKind.TextArea, 4, min: 1, max: 2_000),
                new FormFieldView(
                    Guid.NewGuid(),
                    versionId,
                    "amount",
                    "Amount",
                    FormFieldKind.Decimal,
                    true,
                    null,
                    null,
                    0,
                    100,
                    FormPatternKind.None,
                    [],
                    5,
                    FormPrivacyClass.Standard),
                new FormFieldView(
                    Guid.NewGuid(),
                    versionId,
                    "topic",
                    "Topic",
                    FormFieldKind.Choice,
                    true,
                    null,
                    null,
                    null,
                    null,
                    FormPatternKind.None,
                    ["general", "other"],
                    6,
                    FormPrivacyClass.Standard),
                Field(versionId, "consent", FormFieldKind.Consent, 7, min: null, max: null),
            ],
            new RowVersion(new byte[8]));
    }

    private static FormFieldView Field(
        Guid versionId,
        string key,
        FormFieldKind kind,
        int order,
        int? min,
        int? max,
        FormPatternKind pattern = FormPatternKind.None) =>
        new(
            Guid.NewGuid(),
            versionId,
            key,
            key,
            kind,
            true,
            min,
            max,
            null,
            null,
            pattern,
            [],
            order,
            FormPrivacyClass.Standard);
}
