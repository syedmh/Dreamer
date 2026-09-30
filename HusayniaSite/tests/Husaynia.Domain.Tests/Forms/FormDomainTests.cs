using Husaynia.Domain.Forms;

namespace Husaynia.Domain.Tests.Forms;

public sealed class FormDomainTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefinitionPublishesOnlyItsOwnVersion()
    {
        var definition = new FormDefinition("test-contact-v1", "Synthetic contact");
        var version = new FormDefinitionVersion(
            definition.Id,
            1,
            "test-destination",
            "test-template",
            "test-consent-v1",
            Now);

        definition.Publish(version);

        Assert.True(definition.IsEnabled);
        Assert.Equal(version.Id, definition.PublishedVersionId);
        Assert.Throws<InvalidOperationException>(
            () => definition.Publish(
                new FormDefinitionVersion(
                    Guid.NewGuid(),
                    1,
                    "test-destination",
                    "test-template",
                    "test-consent-v1",
                    Now)));
    }

    [Fact]
    public void FieldRejectsUnboundedRulesAndRawRegexIsNotPartOfTheModel()
    {
        var field = new FormField(
            Guid.NewGuid(),
            "email",
            "Email address",
            FormFieldKind.Email,
            required: true,
            minimumLength: 3,
            maximumLength: 320,
            minimumValue: null,
            maximumValue: null,
            FormPatternKind.Email,
            choices: [],
            order: 1,
            FormPrivacyClass.Contact,
            Now);

        Assert.Equal(FormPatternKind.Email, field.PatternKind);
        Assert.DoesNotContain(
            typeof(FormField).GetProperties(),
            property => property.Name.Contains("Regex", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FormField(
                Guid.NewGuid(),
                "message",
                "Message",
                FormFieldKind.TextArea,
                required: true,
                minimumLength: 0,
                maximumLength: 10_000,
                minimumValue: null,
                maximumValue: null,
                FormPatternKind.None,
                choices: [],
                order: 1,
                FormPrivacyClass.Standard,
                Now));
        Assert.Throws<ArgumentException>(
            () => new FormField(
                Guid.NewGuid(),
                "card-number",
                "Card number",
                FormFieldKind.Text,
                required: true,
                minimumLength: 1,
                maximumLength: 100,
                minimumValue: null,
                maximumValue: null,
                FormPatternKind.None,
                choices: [],
                order: 1,
                FormPrivacyClass.Standard,
                Now));
    }

    [Fact]
    public void LegalHoldFencesAnonymizationAndAttemptsFinalizeOnce()
    {
        var submission = new FormSubmission(
            Guid.NewGuid(),
            new byte[32],
            Enumerable.Repeat((byte)1, 32).ToArray(),
            Now,
            Now,
            Now.AddDays(30),
            "test-consent-v1");
        submission.PlaceLegalHold();

        Assert.Throws<InvalidOperationException>(() => submission.Anonymize(Now.AddDays(31)));

        submission.ReleaseLegalHold();
        submission.Anonymize(Now.AddDays(31));
        Assert.Equal(FormRetentionStatus.Anonymized, submission.RetentionStatus);

        var attempt = new FormDeliveryAttempt(
            submission.Id,
            Guid.NewGuid(),
            1,
            Now);
        attempt.CompleteFailure("sender_disabled", Now.AddMinutes(1));

        Assert.Equal(FormDeliveryAttemptOutcome.Failed, attempt.Outcome);
        Assert.Throws<InvalidOperationException>(
            () => attempt.CompleteFailure("second_result", Now.AddMinutes(2)));
    }
}
