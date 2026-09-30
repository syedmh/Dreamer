using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Domain.Threads;

namespace HusayniaTabruk.Application.Threads;

public sealed class ThreadService(
    IUnitOfWork unitOfWork,
    IServiceDateRepository serviceDateRepository,
    ISignupRepository signupRepository,
    IThreadRepository threadRepository,
    IMembershipRepository membershipRepository,
    IPrivilegedAccessWriter privilegedAccessWriter,
    IStepUpVerifier stepUpVerifier,
    ICurrentActor currentActor,
    IClock clock)
{
    private const string HiddenBody = "This message was hidden by a moderator.";
    private static readonly JsonSerializerOptions PrivilegedJsonOptions = CreatePrivilegedJsonOptions();

    public ValueTask<Result<ThreadMessagePage>> ListAsync(
        ServiceDateId serviceDateId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            token => ListCoreAsync(serviceDateId, cursor, pageSize, token),
            cancellationToken);

    public ValueTask<Result<VersionedThreadMessage>> PostAsync(
        PostThreadMessageCommand command,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            token => PostCoreAsync(command, expectedVersion, token),
            cancellationToken);

    public ValueTask<Result<VersionedThreadReport>> ReportAsync(
        ReportThreadMessageCommand command,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            token => ReportCoreAsync(command, expectedVersion, token),
            cancellationToken);

    public ValueTask<Result<VersionedThreadMessage>> HideAsync(
        HideThreadMessageCommand command,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            token => HideCoreAsync(command, expectedVersion, token),
            cancellationToken);

    public ValueTask<Result<VersionedThreadState>> LockAsync(
        LockThreadCommand command,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            token => LockCoreAsync(command, expectedVersion, token),
            cancellationToken);

    public ValueTask<Result<PrivilegedThreadMessagePage>> ReadPrivilegedAsync(
        ReadPrivilegedThreadPageCommand command,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken = default)
    {
        Result<ValidatedPrivilegedRequest> validation = ValidatePrivileged(command);
        return validation.IsFailure
            ? ValueTask.FromResult(
                Result.Failure<PrivilegedThreadMessagePage>(validation.Error))
            : unitOfWork.ExecuteAsync(
                token => ReadPrivilegedCoreAsync(validation.Value, stepUpToken, token),
                cancellationToken);
    }

    private async ValueTask<Result<ThreadMessagePage>> ListCoreAsync(
        ServiceDateId serviceDateId,
        string? cursor,
        int? requestedPageSize,
        CancellationToken cancellationToken)
    {
        Result<(int StartIndex, int PageSize)> page = ValidatePage(cursor, requestedPageSize);
        if (!serviceDateId.IsValid || page.IsFailure)
        {
            return Invalid<ThreadMessagePage>(
                page.IsFailure ? page.Error.Message : "The service date identifier is invalid.");
        }

        Result<OrdinaryContext> context =
            await LoadOrdinaryContextAsync(
                serviceDateId,
                ThreadAuthorizationRequirement.ParticipantOrManager,
                cancellationToken);
        if (context.IsFailure)
        {
            return Result.Failure<ThreadMessagePage>(context.Error);
        }

        Result access = context.Value.Thread.Thread.AuthorizeRead(
            context.Value.ActorMembership,
            context.Value.ServiceDate,
            context.Value.PrimaryContactSignup);
        if (access.IsFailure)
        {
            return Result.Failure<ThreadMessagePage>(access.Error);
        }

        ThreadMessage[] ordered = OrderedMessages(context.Value.Thread.Thread).ToArray();
        ThreadMessage[] items = ordered
            .Skip(page.Value.StartIndex)
            .Take(page.Value.PageSize)
            .ToArray();
        Result<IReadOnlyDictionary<MembershipId, string>> displays =
            await ResolveDisplaysAsync(items, cancellationToken);
        if (displays.IsFailure)
        {
            return Result.Failure<ThreadMessagePage>(displays.Error);
        }

        return Result.Success(
            new ThreadMessagePage(
                serviceDateId,
                context.Value.Thread.Thread.Status,
                context.Value.Thread.Thread.LockedAt,
                context.Value.Thread.LoadedVersion,
                items.Select(message => ToOrdinary(message, displays.Value)).ToArray(),
                NextCursor(page.Value.StartIndex, items.Length, ordered.Length)));
    }

    private async ValueTask<Result<VersionedThreadMessage>> PostCoreAsync(
        PostThreadMessageCommand command,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (command is null
            || !command.ServiceDateId.IsValid
            || !command.IdempotencyKey.IsValid
            || expectedVersion < 0)
        {
            return Invalid<VersionedThreadMessage>("The thread message request is invalid.");
        }

        Result<OrdinaryContext> context =
            await LoadOrdinaryContextAsync(
                command.ServiceDateId,
                ThreadAuthorizationRequirement.ParticipantOrManager,
                cancellationToken);
        if (context.IsFailure)
        {
            return Result.Failure<VersionedThreadMessage>(context.Error);
        }

        LoadedDateThread loaded = context.Value.Thread;
        Result access = loaded.Thread.AuthorizeRead(
            context.Value.ActorMembership,
            context.Value.ServiceDate,
            context.Value.PrimaryContactSignup);
        if (access.IsFailure)
        {
            return Result.Failure<VersionedThreadMessage>(access.Error);
        }

        if (expectedVersion != loaded.LoadedVersion)
        {
            ThreadMessage? duplicate = loaded.Thread.Messages.SingleOrDefault(
                message => message.ClientMessageId == command.IdempotencyKey);
            if (duplicate is null
                || duplicate.AuthorMembershipId != context.Value.ActorMembership.Id
                || duplicate.Body != command.Body)
            {
                return Stale<VersionedThreadMessage>();
            }

            return await RenderVersionedMessageAsync(
                duplicate,
                loaded.LoadedVersion,
                cancellationToken);
        }

        Result<MessagePosted> posted = loaded.Thread.Post(
            MessageId.New(),
            command.IdempotencyKey,
            command.Body,
            context.Value.ActorMembership,
            context.Value.ServiceDate,
            context.Value.PrimaryContactSignup,
            clock.UtcNow);
        if (posted.IsFailure)
        {
            return Result.Failure<VersionedThreadMessage>(posted.Error);
        }

        if (!posted.Value.WasDuplicate)
        {
            Result saved = await threadRepository.SaveAsync(
                loaded,
                ThreadPersistenceEffects.Empty,
                cancellationToken);
            if (saved.IsFailure)
            {
                return Result.Failure<VersionedThreadMessage>(saved.Error);
            }
        }

        return await RenderVersionedMessageAsync(
            posted.Value.Message,
            loaded.Thread.Version,
            cancellationToken);
    }

    private async ValueTask<Result<VersionedThreadReport>> ReportCoreAsync(
        ReportThreadMessageCommand command,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (command is null
            || !command.ServiceDateId.IsValid
            || !command.MessageId.IsValid
            || expectedVersion < 0)
        {
            return Invalid<VersionedThreadReport>("The thread report request is invalid.");
        }

        Result<OrdinaryContext> context =
            await LoadOrdinaryContextAsync(
                command.ServiceDateId,
                ThreadAuthorizationRequirement.ParticipantOrManager,
                cancellationToken);
        if (context.IsFailure)
        {
            return Result.Failure<VersionedThreadReport>(context.Error);
        }

        LoadedDateThread loaded = context.Value.Thread;
        Result access = loaded.Thread.AuthorizeRead(
            context.Value.ActorMembership,
            context.Value.ServiceDate,
            context.Value.PrimaryContactSignup);
        if (access.IsFailure)
        {
            return Result.Failure<VersionedThreadReport>(access.Error);
        }

        MessageReport? duplicate = loaded.Thread.Reports.SingleOrDefault(
            report => report.MessageId == command.MessageId
                && report.ReporterMembershipId == context.Value.ActorMembership.Id);
        string? normalizedComment = NormalizeOptional(command.Comment);
        if (expectedVersion != loaded.LoadedVersion)
        {
            return duplicate is not null
                && duplicate.Reason == command.Reason
                && duplicate.Comment == normalizedComment
                ? Result.Success(
                    new VersionedThreadReport(
                        loaded.LoadedVersion,
                        WasDuplicate: true))
                : Stale<VersionedThreadReport>();
        }

        if (duplicate is not null)
        {
            if (duplicate.Reason != command.Reason
                || duplicate.Comment != normalizedComment)
            {
                return Result.Failure<VersionedThreadReport>(
                    DomainError.Conflict(
                        ThreadApplicationErrorCodes.DuplicateReportMismatch,
                        "The existing report does not match this request."));
            }

            return Result.Success(
                new VersionedThreadReport(loaded.LoadedVersion, WasDuplicate: true));
        }

        Result<MessageReported> reported = loaded.Thread.Report(
            command.MessageId,
            command.Reason,
            command.Comment,
            context.Value.ActorMembership,
            context.Value.ServiceDate,
            context.Value.PrimaryContactSignup,
            clock.UtcNow);
        if (reported.IsFailure)
        {
            return Result.Failure<VersionedThreadReport>(reported.Error);
        }

        Result saved = await threadRepository.SaveAsync(
            loaded,
            ThreadPersistenceEffects.Empty,
            cancellationToken);
        return saved.IsFailure
            ? Result.Failure<VersionedThreadReport>(saved.Error)
            : Result.Success(
                new VersionedThreadReport(
                    loaded.Thread.Version,
                    reported.Value.WasDuplicate));
    }

    private async ValueTask<Result<VersionedThreadMessage>> HideCoreAsync(
        HideThreadMessageCommand command,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (command is null
            || !command.ServiceDateId.IsValid
            || !command.MessageId.IsValid
            || expectedVersion < 0)
        {
            return Invalid<VersionedThreadMessage>("The hide request is invalid.");
        }

        Result<OrdinaryContext> context =
            await LoadOrdinaryContextAsync(
                command.ServiceDateId,
                ThreadAuthorizationRequirement.ManagingFoodIncharge,
                cancellationToken);
        if (context.IsFailure)
        {
            return Result.Failure<VersionedThreadMessage>(context.Error);
        }

        LoadedDateThread loaded = context.Value.Thread;
        if (expectedVersion != loaded.LoadedVersion)
        {
            return Stale<VersionedThreadMessage>();
        }

        DateTimeOffset now = clock.UtcNow;
        Result<MessageHidden> hidden = loaded.Thread.Hide(
            command.MessageId,
            command.Reason,
            context.Value.ActorMembership,
            context.Value.ServiceDate,
            now);
        if (hidden.IsFailure)
        {
            return Result.Failure<VersionedThreadMessage>(hidden.Error);
        }

        Result saved = await threadRepository.SaveAsync(
            loaded,
            ModerationEffects(
                loaded.Thread,
                context.Value.ActorMembership.Id,
                command.MessageId,
                "thread.message.hidden",
                "hide",
                command.Reason.Trim(),
                now),
            cancellationToken);
        return saved.IsFailure
            ? Result.Failure<VersionedThreadMessage>(saved.Error)
            : await RenderVersionedMessageAsync(
                hidden.Value.Message,
                loaded.Thread.Version,
                cancellationToken);
    }

    private async ValueTask<Result<VersionedThreadState>> LockCoreAsync(
        LockThreadCommand command,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (command is null || !command.ServiceDateId.IsValid || expectedVersion < 0)
        {
            return Invalid<VersionedThreadState>("The lock request is invalid.");
        }

        Result<OrdinaryContext> context =
            await LoadOrdinaryContextAsync(
                command.ServiceDateId,
                ThreadAuthorizationRequirement.ManagingFoodIncharge,
                cancellationToken);
        if (context.IsFailure)
        {
            return Result.Failure<VersionedThreadState>(context.Error);
        }

        LoadedDateThread loaded = context.Value.Thread;
        if (expectedVersion != loaded.LoadedVersion)
        {
            return Stale<VersionedThreadState>();
        }

        DateTimeOffset now = clock.UtcNow;
        Result<ThreadLocked> locked = loaded.Thread.Lock(
            command.Reason,
            context.Value.ActorMembership,
            context.Value.ServiceDate,
            now);
        if (locked.IsFailure)
        {
            return Result.Failure<VersionedThreadState>(locked.Error);
        }

        if (!locked.Value.WasDuplicate)
        {
            Result saved = await threadRepository.SaveAsync(
                loaded,
                ModerationEffects(
                    loaded.Thread,
                    context.Value.ActorMembership.Id,
                    messageId: null,
                    "thread.locked",
                    "lock",
                    command.Reason.Trim(),
                    now),
                cancellationToken);
            if (saved.IsFailure)
            {
                return Result.Failure<VersionedThreadState>(saved.Error);
            }
        }

        return Result.Success(
            new VersionedThreadState(
                loaded.Thread.ServiceDateId,
                loaded.Thread.Status,
                loaded.Thread.LockedAt,
                loaded.Thread.Version));
    }

    private async ValueTask<Result<PrivilegedThreadMessagePage>> ReadPrivilegedCoreAsync(
        ValidatedPrivilegedRequest request,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken)
    {
        Result<ThreadPrivilegedAuthorizationContext> actor =
            await threadRepository.LockPrivilegedAuthorizationAsync(
                currentActor.UserId,
                currentActor.MembershipId,
                currentActor.OrganizationId,
                cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<PrivilegedThreadMessagePage>(actor.Error);
        }

        Result consumed = await stepUpVerifier.ConsumeAsync(
            actor.Value.UserId,
            stepUpToken,
            ThreadStepUpPurposes.PrivilegedRead,
            cancellationToken);
        if (consumed.IsFailure)
        {
            return Result.Failure<PrivilegedThreadMessagePage>(consumed.Error);
        }

        Result<LoadedDateThread> loaded = await threadRepository.GetByServiceDateAsync(
            actor.Value.OrganizationId,
            request.ServiceDateId,
            cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<PrivilegedThreadMessagePage>(loaded.Error);
        }

        ThreadMessage[] ordered = OrderedMessages(loaded.Value.Thread).ToArray();
        ThreadMessage[] items = ordered
            .Skip(request.StartIndex)
            .Take(request.PageSize)
            .ToArray();
        Result<IReadOnlyDictionary<MembershipId, string>> displays =
            await ResolveDisplaysAsync(items, cancellationToken);
        if (displays.IsFailure)
        {
            return Result.Failure<PrivilegedThreadMessagePage>(displays.Error);
        }

        PrivilegedThreadMessagePage response = new(
            loaded.Value.Thread.Id,
            loaded.Value.Thread.ServiceDateId,
            loaded.Value.Thread.Status,
            loaded.Value.Thread.LockedAt,
            items.Select(message => ToPrivileged(message, displays.Value)).ToArray(),
            NextCursor(request.StartIndex, items.Length, ordered.Length));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            ToHashContract(response),
            PrivilegedJsonOptions);
        RequestFingerprint pageHash = RequestFingerprint.FromSha256(
            Convert.ToHexString(SHA256.HashData(payload)));
        await privilegedAccessWriter.WriteAsync(
            new PrivilegedAccessEntry(
                AuditEventId.New(),
                actor.Value.OrganizationId,
                actor.Value.MembershipId,
                "thread",
                loaded.Value.Thread.Id.ToString(),
                request.Reason,
                request.Purpose,
                request.CaseId,
                request.Cursor,
                pageHash,
                clock.UtcNow),
            cancellationToken);
        return Result.Success(response);
    }

    private async ValueTask<Result<OrdinaryContext>> LoadOrdinaryContextAsync(
        ServiceDateId serviceDateId,
        ThreadAuthorizationRequirement requirement,
        CancellationToken cancellationToken)
    {
        _ = serviceDateRepository;
        _ = signupRepository;
        Result<ThreadOrdinaryAuthorizationContext> authorization =
            await threadRepository.LockOrdinaryAuthorizationAsync(
                currentActor.UserId,
                currentActor.MembershipId,
                currentActor.OrganizationId,
                serviceDateId,
                requirement,
                cancellationToken);
        if (authorization.IsFailure)
        {
            return authorization.Error.Type == ErrorType.DependencyUnavailable
                ? Result.Failure<OrdinaryContext>(authorization.Error)
                : Concealed<OrdinaryContext>();
        }

        Result<LoadedDateThread> thread = await threadRepository.GetByServiceDateAsync(
            authorization.Value.Actor.OrganizationId,
            serviceDateId,
            cancellationToken);
        if (thread.IsFailure)
        {
            return thread.Error.Type == ErrorType.DependencyUnavailable
                ? Result.Failure<OrdinaryContext>(thread.Error)
                : Concealed<OrdinaryContext>();
        }

        return Result.Success(
            new OrdinaryContext(
                authorization.Value.Actor,
                authorization.Value.ServiceDate,
                thread.Value,
                authorization.Value.ApprovedPrimarySignup));
    }

    private async ValueTask<Result<VersionedThreadMessage>> RenderVersionedMessageAsync(
        ThreadMessage message,
        long version,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyDictionary<MembershipId, string>> displays =
            await ResolveDisplaysAsync([message], cancellationToken);
        return displays.IsFailure
            ? Result.Failure<VersionedThreadMessage>(displays.Error)
            : Result.Success(
                new VersionedThreadMessage(ToOrdinary(message, displays.Value), version));
    }

    private async ValueTask<Result<IReadOnlyDictionary<MembershipId, string>>> ResolveDisplaysAsync(
        IReadOnlyCollection<ThreadMessage> messages,
        CancellationToken cancellationToken)
    {
        MembershipId[] ids = messages
            .Select(message => message.AuthorMembershipId)
            .Distinct()
            .OrderBy(id => id.Value)
            .ToArray();
        if (ids.Length == 0)
        {
            return Result.Success<IReadOnlyDictionary<MembershipId, string>>(
                new Dictionary<MembershipId, string>());
        }

        Result<IReadOnlyDictionary<MembershipId, string>> result =
            await membershipRepository.GetThreadSenderDisplaysAsync(
                currentActor.OrganizationId,
                ids,
                cancellationToken);
        if (result.IsFailure)
        {
            return result;
        }

        if (ids.Any(id =>
            !result.Value.TryGetValue(id, out string? display)
            || string.IsNullOrWhiteSpace(display)))
        {
            return SenderDisplayUnavailable();
        }

        return result;
    }

    private static Result<ValidatedPrivilegedRequest> ValidatePrivileged(
        ReadPrivilegedThreadPageCommand command)
    {
        if (command is null
            || !command.ServiceDateId.IsValid
            || string.IsNullOrWhiteSpace(command.Reason)
            || command.Reason.EnumerateRunes().Count() > ApplicationLimits.MaximumReasonUnicodeScalars
            || Encoding.UTF8.GetByteCount(command.Reason) > ApplicationLimits.MaximumReasonUtf8Bytes
            || !Enum.IsDefined(command.Purpose))
        {
            return Invalid<ValidatedPrivilegedRequest>(
                "The privileged thread read request is invalid.");
        }

        string caseId = command.CaseId?.Trim() ?? string.Empty;
        if (caseId.Length == 0
            || caseId.Length > ApplicationLimits.MaximumCaseIdAsciiCharacters
            || caseId.Any(character => character is < ' ' or > '~'))
        {
            return Invalid<ValidatedPrivilegedRequest>(
                "The privileged thread read case identifier is invalid.");
        }

        Result<(int StartIndex, int PageSize)> page =
            ValidatePage(command.Cursor, command.PageSize);
        return page.IsFailure
            ? Result.Failure<ValidatedPrivilegedRequest>(page.Error)
            : Result.Success(
                new ValidatedPrivilegedRequest(
                    command.ServiceDateId,
                    command.Reason,
                    command.Purpose,
                    caseId,
                    command.Cursor,
                    page.Value.StartIndex,
                    page.Value.PageSize));
    }

    private static Result<(int StartIndex, int PageSize)> ValidatePage(
        string? cursor,
        int? requestedPageSize)
    {
        if (requestedPageSize is <= 0 or > ApplicationLimits.MaximumPageSize)
        {
            return Invalid<(int, int)>(
                $"Page size must be from 1 through {ApplicationLimits.MaximumPageSize}.");
        }

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return Result.Success((0, requestedPageSize ?? ApplicationLimits.DefaultPageSize));
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(
                    decoded,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int value)
                && value >= 0
                ? Result.Success((value, requestedPageSize ?? ApplicationLimits.DefaultPageSize))
                : Invalid<(int, int)>("The cursor is invalid.");
        }
        catch (FormatException)
        {
            return Invalid<(int, int)>("The cursor is invalid.");
        }
    }

    private static IEnumerable<ThreadMessage> OrderedMessages(DateThread thread) =>
        thread.Messages
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id.Value);

    private static ThreadMessageSummary ToOrdinary(
        ThreadMessage message,
        IReadOnlyDictionary<MembershipId, string> displays) =>
        new(
            message.Id,
            displays[message.AuthorMembershipId],
            message.Visibility == MessageVisibility.Hidden ? HiddenBody : message.Body,
            message.Visibility,
            message.CreatedAt);

    private static PrivilegedThreadMessageSummary ToPrivileged(
        ThreadMessage message,
        IReadOnlyDictionary<MembershipId, string> displays) =>
        new(
            message.Id,
            displays[message.AuthorMembershipId],
            message.Body,
            message.Visibility,
            message.CreatedAt,
            message.HiddenAt);

    private static string? NextCursor(int startIndex, int count, int total)
    {
        int next = startIndex + count;
        return next < total
            ? Convert.ToBase64String(
                Encoding.UTF8.GetBytes(next.ToString(CultureInfo.InvariantCulture)))
            : null;
    }

    private static ThreadPersistenceEffects ModerationEffects(
        DateThread thread,
        MembershipId actorMembershipId,
        MessageId? messageId,
        string auditAction,
        string moderationAction,
        string reason,
        DateTimeOffset occurredAt) =>
        new(
            [],
            [
                new AuditEntry(
                    AuditEventId.New(),
                    thread.OrganizationId,
                    actorMembershipId,
                    auditAction,
                    messageId.HasValue ? "thread_message" : "thread",
                    messageId?.ToString() ?? thread.Id.ToString(),
                    reason,
                    "moderation",
                    Guid.CreateVersion7().ToString("D"),
                    beforeState: null,
                    afterState: null,
                    occurredAt),
            ],
            [],
            [],
            [
                new ThreadModerationWrite(
                    Guid.CreateVersion7(),
                    thread.OrganizationId,
                    thread.Id,
                    messageId,
                    actorMembershipId,
                    moderationAction,
                    reason,
                    occurredAt),
            ]);

    private static object ToHashContract(PrivilegedThreadMessagePage response) =>
        new
        {
            ThreadId = response.ThreadId.ToString(),
            ServiceDateId = response.ServiceDateId.ToString(),
            response.Status,
            response.LockedAt,
            Items = response.Items.Select(
                item => new
                {
                    Id = item.Id.ToString(),
                    item.SenderDisplayName,
                    item.Body,
                    item.Visibility,
                    item.CreatedAt,
                    item.HiddenAt,
                }).ToArray(),
            response.NextCursor,
        };

    private static JsonSerializerOptions CreatePrivilegedJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Result<T> Invalid<T>(string message) =>
        Result.Failure<T>(
            DomainError.Validation(
                ThreadApplicationErrorCodes.InvalidThreadRequest,
                message));

    private static Result<T> Stale<T>() =>
        Result.Failure<T>(
            DomainError.PreconditionFailed(
                ErrorCodes.StaleVersion,
                "The thread changed. Refresh and retry."));

    private static Result<T> Concealed<T>() =>
        Result.Failure<T>(ThreadErrorCodes.Concealed());

    private static Result<IReadOnlyDictionary<MembershipId, string>> SenderDisplayUnavailable() =>
        Result.Failure<IReadOnlyDictionary<MembershipId, string>>(
            DomainError.DependencyUnavailable(
                ThreadApplicationErrorCodes.ThreadSenderDisplayUnavailable,
                "Thread sender display names are temporarily unavailable."));

    private sealed record OrdinaryContext(
        Membership ActorMembership,
        ServiceDate ServiceDate,
        LoadedDateThread Thread,
        Signup? PrimaryContactSignup);

    private sealed record ValidatedPrivilegedRequest(
        ServiceDateId ServiceDateId,
        string Reason,
        PrivilegedAccessPurpose Purpose,
        string CaseId,
        string? Cursor,
        int StartIndex,
        int PageSize);
}
