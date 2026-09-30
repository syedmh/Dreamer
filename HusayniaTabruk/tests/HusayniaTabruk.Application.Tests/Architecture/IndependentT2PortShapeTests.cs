using System.Reflection;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Tests.Architecture;

public sealed class IndependentT2PortShapeTests
{
    [Fact]
    public void ExplicitlyFrozenPortMembersRetainTheirContractShapes()
    {
        Assert.Equal(typeof(DateTimeOffset), typeof(IClock).GetProperty(nameof(IClock.UtcNow))?.PropertyType);

        var unitOfWork = Assert.Single(typeof(IUnitOfWork).GetMethods());
        Assert.Equal(nameof(IUnitOfWork.ExecuteAsync), unitOfWork.Name);
        Assert.True(unitOfWork.IsGenericMethodDefinition);
        Assert.Equal(typeof(ValueTask<>), unitOfWork.ReturnType.GetGenericTypeDefinition());
        Assert.Equal(typeof(Result<>), unitOfWork.ReturnType.GenericTypeArguments[0].GetGenericTypeDefinition());
        Assert.Equal(typeof(CancellationToken), unitOfWork.GetParameters()[1].ParameterType);

        var push = Assert.Single(typeof(IPushGateway).GetMethods());
        Assert.Equal(nameof(IPushGateway.SendAsync), push.Name);
        Assert.Equal(typeof(ValueTask<PushDeliveryResult>), push.ReturnType);
        Assert.Equal([typeof(PushMessage), typeof(CancellationToken)], push.GetParameters().Select(p => p.ParameterType));

        var issue = typeof(IStepUpVerifier).GetMethod(nameof(IStepUpVerifier.IssueAsync))!;
        Assert.Equal(typeof(ValueTask<Result<StepUpGrant>>), issue.ReturnType);
        Assert.Equal(
            [typeof(UserId), typeof(string), typeof(StepUpPurpose), typeof(CancellationToken)],
            issue.GetParameters().Select(p => p.ParameterType));

        var consume = typeof(IStepUpVerifier).GetMethod(nameof(IStepUpVerifier.ConsumeAsync))!;
        Assert.Equal(typeof(ValueTask<Result>), consume.ReturnType);
        Assert.Equal(
            [typeof(UserId), typeof(StepUpToken), typeof(StepUpPurpose), typeof(CancellationToken)],
            consume.GetParameters().Select(p => p.ParameterType));

        MethodInfo[] idempotencyMethods = typeof(IIdempotencyStore).GetMethods()
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                nameof(IIdempotencyStore.FindAsync),
                nameof(IIdempotencyStore.TryCompleteAsync),
                nameof(IIdempotencyStore.TryCreateProcessingAsync),
                nameof(IIdempotencyStore.TryFailAsync),
            ],
            idempotencyMethods.Select(method => method.Name).ToArray());
        Assert.Null(typeof(IIdempotencyStore).GetMethod("AddAsync"));

        MethodInfo find = typeof(IIdempotencyStore).GetMethod(nameof(IIdempotencyStore.FindAsync))!;
        Assert.Equal(typeof(ValueTask<IdempotencyReceipt>), find.ReturnType);
        Assert.Equal(
            [typeof(OrganizationId), typeof(MembershipId), typeof(IdempotencyKey), typeof(CancellationToken)],
            find.GetParameters().Select(parameter => parameter.ParameterType));

        MethodInfo create = typeof(IIdempotencyStore).GetMethod(nameof(IIdempotencyStore.TryCreateProcessingAsync))!;
        Assert.Equal(typeof(ValueTask<IdempotencyCreateResult>), create.ReturnType);
        Assert.Equal(
            [typeof(IdempotencyCreateRequest), typeof(CancellationToken)],
            create.GetParameters().Select(parameter => parameter.ParameterType));

        MethodInfo complete = typeof(IIdempotencyStore).GetMethod(nameof(IIdempotencyStore.TryCompleteAsync))!;
        Assert.Equal(typeof(ValueTask<IdempotencyTransitionResult>), complete.ReturnType);
        Assert.Equal(
            [typeof(IdempotencyRequest), typeof(string), typeof(CancellationToken)],
            complete.GetParameters().Select(parameter => parameter.ParameterType));

        MethodInfo fail = typeof(IIdempotencyStore).GetMethod(nameof(IIdempotencyStore.TryFailAsync))!;
        Assert.Equal(typeof(ValueTask<IdempotencyTransitionResult>), fail.ReturnType);
        Assert.Equal(
            [typeof(IdempotencyRequest), typeof(CancellationToken)],
            fail.GetParameters().Select(parameter => parameter.ParameterType));
    }
}
