using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;

namespace Husaynia.Application.Forms;

public sealed class FormDeliveryJobHandler(
    IFormDeliveryStore store,
    IOutboundMessageSender sender) : IJobHandler
{
    public const string DefinitionKey = "forms.delivery.v1";
    public const string Name = "forms.delivery";

    public string HandlerName => Name;

    public async Task<JobHandlerResult> ExecuteAsync(
        string payloadJson,
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!FormDeliveryJobPayload.TryParse(payloadJson, out var payload))
        {
            return JobHandlerResult.Failed("invalid_payload");
        }

        var attempt = await store.BeginAttemptAsync(
                payload.SubmissionId,
                payload.DefinitionVersionId,
                context,
                cancellationToken)
            .ConfigureAwait(false);
        if (attempt.IsFailure)
        {
            return JobHandlerResult.Failed(BoundError(attempt.Error.Code));
        }

        try
        {
            var send = await sender.SendAsync(
                    new OutboundMessage(
                        attempt.Success.DestinationKey,
                        attempt.Success.TemplateKey,
                        attempt.Success.Values),
                    cancellationToken)
                .ConfigureAwait(false);
            if (send.IsFailure)
            {
                var errorCode = BoundError(send.Error.Code);
                await store.CompleteAttemptAsync(
                        attempt.Success.AttemptId,
                        succeeded: false,
                        errorCode,
                        providerReceiptHash: null,
                        cancelled: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return JobHandlerResult.Failed(errorCode);
            }

            var receiptHash = SHA256.HashData(
                Encoding.UTF8.GetBytes(send.Success.ProviderMessageId));
            var completed = await store.CompleteAttemptAsync(
                    attempt.Success.AttemptId,
                    succeeded: true,
                    errorCode: null,
                    receiptHash,
                    cancelled: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return completed.IsSuccess && completed.Success
                ? JobHandlerResult.Succeeded
                : JobHandlerResult.Failed("attempt_finalize_failed");
        }
        catch (OperationCanceledException)
        {
            await store.CompleteAttemptAsync(
                    attempt.Success.AttemptId,
                    succeeded: false,
                    errorCode: "cancelled",
                    providerReceiptHash: null,
                    cancelled: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch
        {
            await store.CompleteAttemptAsync(
                    attempt.Success.AttemptId,
                    succeeded: false,
                    errorCode: "sender_failure",
                    providerReceiptHash: null,
                    cancelled: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return JobHandlerResult.Failed("sender_failure");
        }
    }

    private static string BoundError(string code) =>
        !string.IsNullOrWhiteSpace(code) &&
        code.Length <= 100 &&
        code.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? code
            : "sender_failure";
}

public readonly record struct FormDeliveryJobPayload(
    Guid SubmissionId,
    Guid DefinitionVersionId)
{
    public string Serialize() =>
        JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["submissionId"] = SubmissionId.ToString("N"),
            ["definitionVersionId"] = DefinitionVersionId.ToString("N"),
        });

    public static bool TryParse(string json, out FormDeliveryJobPayload payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_000)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 2,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 2 ||
                properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != 2)
            {
                return false;
            }

            var submission = properties.SingleOrDefault(
                property => property.NameEquals("submissionId"));
            var definition = properties.SingleOrDefault(
                property => property.NameEquals("definitionVersionId"));
            if (submission.Value.ValueKind != JsonValueKind.String ||
                definition.Value.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(submission.Value.GetString(), "N", out var submissionId) ||
                !Guid.TryParseExact(definition.Value.GetString(), "N", out var versionId))
            {
                return false;
            }

            payload = new FormDeliveryJobPayload(submissionId, versionId);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class FormsDeliveryJobCoordinator(IDurableJobStore store)
    : IFormsDeliveryJobCoordinator
{
    private readonly IDurableJobStore store =
        store ?? throw new ArgumentNullException(nameof(store));

    public Task<Result<Guid, JobStoreError>> RegisterAsync(CancellationToken cancellationToken) =>
        store.RegisterDefinitionAsync(
            new JobDefinitionRegistration(
                FormDeliveryJobHandler.DefinitionKey,
                FormDeliveryJobHandler.Name,
                MaximumAttempts: 5,
                InitialBackoff: TimeSpan.FromMinutes(1),
                MaximumBackoff: TimeSpan.FromHours(1)),
            cancellationToken);
}
