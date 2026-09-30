using System.Reflection;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Tests.Architecture;

public sealed class IndependentT2BoundaryTests
{
    [Fact]
    public void EveryStronglyTypedIdRejectsDefaultValueAccessAndRoundTripsCanonicalUuid()
    {
        Type[] identifierTypes =
        [
            typeof(UserId),
            typeof(OrganizationId),
            typeof(MembershipId),
            typeof(ServiceDateId),
            typeof(HelpNeedId),
            typeof(SignupId),
            typeof(ThreadId),
            typeof(MessageId),
            typeof(NotificationId),
            typeof(RoleChangeRequestId),
            typeof(AuditEventId),
            typeof(OutboxMessageId),
            typeof(DeviceId),
            typeof(IdempotencyKey),
        ];

        Guid value = Guid.Parse("5223a4ae-a182-481e-a39d-6b6d72b7d114");

        foreach (Type identifierType in identifierTypes)
        {
            MethodInfo create = identifierType.GetMethod("New", Type.EmptyTypes)!;
            MethodInfo from = identifierType.GetMethod("From", [typeof(Guid)])!;
            MethodInfo parse = identifierType.GetMethod("Parse", [typeof(string), typeof(IFormatProvider)])!;
            MethodInfo tryParse = identifierType.GetMethod("TryParse", [typeof(string), identifierType.MakeByRefType()])!;
            MethodInfo ensureValid = identifierType.GetMethod("EnsureValid", Type.EmptyTypes)!;
            PropertyInfo isValid = identifierType.GetProperty("IsValid")!;
            PropertyInfo rawValue = identifierType.GetProperty("Value")!;

            TargetInvocationException emptyException = Assert.Throws<TargetInvocationException>(
                () => from.Invoke(null, [Guid.Empty]));
            Assert.IsType<ArgumentException>(emptyException.InnerException);

            object defaultIdentifier = Activator.CreateInstance(identifierType)!;
            Assert.False((bool)isValid.GetValue(defaultIdentifier)!);
            TargetInvocationException defaultValueException = Assert.Throws<TargetInvocationException>(
                () => rawValue.GetValue(defaultIdentifier));
            Assert.IsType<InvalidOperationException>(defaultValueException.InnerException);
            TargetInvocationException defaultException = Assert.Throws<TargetInvocationException>(
                () => ensureValid.Invoke(defaultIdentifier, null));
            Assert.IsType<InvalidOperationException>(defaultException.InnerException);
            Assert.Throws<InvalidOperationException>(() => defaultIdentifier.ToString());

            object generated = create.Invoke(null, null)!;
            Guid generatedValue = (Guid)rawValue.GetValue(generated)!;
            Assert.Equal(7, generatedValue.Version);
            Assert.True((bool)isValid.GetValue(generated)!);

            object identifier = from.Invoke(null, [value])!;
            Assert.Equal(value, (Guid)rawValue.GetValue(identifier)!);
            Assert.Equal("5223a4ae-a182-481e-a39d-6b6d72b7d114", identifier.ToString());
            Assert.Equal(identifier, parse.Invoke(null, [identifier.ToString(), null]));

            object?[] validArguments = [identifier.ToString(), null];
            Assert.True((bool)tryParse.Invoke(null, validArguments)!);
            Assert.Equal(identifier, validArguments[1]);

            object?[] invalidArguments = ["not-a-uuid", null];
            Assert.False((bool)tryParse.Invoke(null, invalidArguments)!);
        }
    }

    [Fact]
    public void FrozenEnumMembersRetainTheirOrdinalValues()
    {
        Assert.Equal([0, 1, 2], Enum.GetValues<MembershipStatus>().Select(value => (int)value));
        Assert.Equal([0, 1], Enum.GetValues<OrganizationRole>().Select(value => (int)value));
        Assert.Equal([0], Enum.GetValues<PrivilegedPermission>().Select(value => (int)value));
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<ServiceDateStatus>().Select(value => (int)value));
        Assert.Equal([0, 1, 2], Enum.GetValues<HelpCategory>().Select(value => (int)value));
        Assert.Equal([0, 1], Enum.GetValues<HelpNeedStatus>().Select(value => (int)value));
        Assert.Equal([0, 1, 2], Enum.GetValues<SignupKind>().Select(value => (int)value));
        Assert.Equal([0, 1, 2, 3, 4, 5], Enum.GetValues<SignupStatus>().Select(value => (int)value));
        Assert.Equal([0, 1], Enum.GetValues<ThreadStatus>().Select(value => (int)value));
        Assert.Equal([0, 1], Enum.GetValues<MessageVisibility>().Select(value => (int)value));
    }

    [Fact]
    public void SharedLimitsIncludeFrozenSignupLabelBounds()
    {
        FieldInfo[] constants = typeof(ApplicationLimits).GetFields(BindingFlags.Public | BindingFlags.Static);

        Assert.Contains(
            constants,
            field => field.Name.Contains("SignupLabel", StringComparison.Ordinal)
                && Equals(field.GetRawConstantValue(), 80));
        Assert.Contains(
            constants,
            field => field.Name.Contains("SignupLabel", StringComparison.Ordinal)
                && Equals(field.GetRawConstantValue(), 320));
    }
}
