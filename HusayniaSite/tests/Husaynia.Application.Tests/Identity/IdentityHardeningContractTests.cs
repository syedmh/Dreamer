using System.Reflection;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;

namespace Husaynia.Application.Tests.Identity;

public sealed class IdentityHardeningContractTests
{
    private static readonly Assembly ApplicationAssembly = typeof(IUserAdministration).Assembly;

    [Fact]
    public void AuditFinalizerHasExactFrozenSignature()
    {
        var contract = RequireType("Husaynia.Application.Identity.IIdentityAuditFinalizer");

        Assert.True(contract.IsInterface);
        var method = Assert.Single(contract.GetMethods());
        Assert.Equal("FinalizeOnceAsync", method.Name);
        Assert.Equal(typeof(Task), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Collection(
            parameters,
            parameter => AssertParameter<IdentityAuditDescriptor>(parameter, "descriptor"),
            parameter => AssertParameter<PrivilegedAttemptOutcome>(parameter, "outcome"),
            parameter => AssertParameter<IReadOnlyDictionary<string, string?>>(parameter, "details"),
            parameter =>
            {
                AssertParameter<Func<CancellationToken, Task>?>(parameter, "completePersistenceAsync");
                Assert.True(parameter.IsOptional);
                Assert.Null(parameter.DefaultValue);
            });
    }

    [Fact]
    public void AnonymousRateLimiterHasExactFrozenSignature()
    {
        var decisionType = RequireType("Husaynia.Application.Identity.IdentityRateLimitDecision");
        var contract = RequireType("Husaynia.Application.Identity.IIdentityAnonymousRateLimiter");

        Assert.True(contract.IsInterface);
        var method = Assert.Single(contract.GetMethods());
        Assert.Equal("AttemptAsync", method.Name);
        Assert.Equal(typeof(Task<>).MakeGenericType(decisionType), method.ReturnType);

        Assert.Collection(
            method.GetParameters(),
            parameter => AssertParameter<string>(parameter, "endpointFamily"),
            parameter => AssertParameter<ReadOnlyMemory<byte>>(parameter, "clientFingerprint"),
            parameter => AssertParameter<CancellationToken>(parameter, "cancellationToken"));
    }

    [Fact]
    public void RateLimitDecisionPreservesImmutableConstructorValues()
    {
        var decisionType = RequireType("Husaynia.Application.Identity.IdentityRateLimitDecision");
        var constructor = Assert.Single(decisionType.GetConstructors());
        var windowEnd = new DateTimeOffset(2026, 8, 21, 12, 34, 56, TimeSpan.Zero);

        Assert.Collection(
            constructor.GetParameters(),
            parameter => AssertParameter<bool>(parameter, "IsAllowed"),
            parameter => AssertParameter<DateTimeOffset>(parameter, "WindowEndsAtUtc"),
            parameter => AssertParameter<int>(parameter, "RequestCount"),
            parameter => AssertParameter<int>(parameter, "PermitLimit"));

        var decision = constructor.Invoke([true, windowEnd, 3, 5]);

        Assert.Equal(true, decisionType.GetProperty("IsAllowed")!.GetValue(decision));
        Assert.Equal(windowEnd, decisionType.GetProperty("WindowEndsAtUtc")!.GetValue(decision));
        Assert.Equal(3, decisionType.GetProperty("RequestCount")!.GetValue(decision));
        Assert.Equal(5, decisionType.GetProperty("PermitLimit")!.GetValue(decision));
        Assert.All(
            decisionType.GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.Contains(
                typeof(System.Runtime.CompilerServices.IsExternalInit),
                property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()));
    }

    [Fact]
    public void AuditFinalizationExceptionHasSanitizedConstructorShape()
    {
        var exceptionType = RequireType(
            "Husaynia.Application.Identity.IdentityAuditFinalizationException");

        Assert.Equal(typeof(Exception), exceptionType.BaseType);
        var constructor = Assert.Single(exceptionType.GetConstructors());
        var innerException = new InvalidOperationException("private dependency detail");

        Assert.Collection(
            constructor.GetParameters(),
            parameter => AssertParameter<string>(parameter, "message"),
            parameter =>
            {
                AssertParameter<Exception?>(parameter, "innerException");
                Assert.True(parameter.IsOptional);
                Assert.Null(parameter.DefaultValue);
            });

        var exception = Assert.IsAssignableFrom<Exception>(
            constructor.Invoke(["Identity audit finalization failed.", innerException]));

        Assert.Equal("Identity audit finalization failed.", exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }

    private static Type RequireType(string fullName)
    {
        var type = ApplicationAssembly.GetType(fullName);
        Assert.NotNull(type);
        return type;
    }

    private static void AssertParameter<T>(ParameterInfo parameter, string expectedName)
    {
        Assert.Equal(expectedName, parameter.Name);
        Assert.Equal(typeof(T), parameter.ParameterType);
    }
}
