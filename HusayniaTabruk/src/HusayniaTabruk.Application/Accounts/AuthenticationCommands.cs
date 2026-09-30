using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Accounts;

public sealed record AcceptInvitationCommand(
    string Token,
    string DisplayName,
    string Password,
    DeviceId DeviceId);

public sealed record LoginCommand(
    string Email,
    string Password,
    DeviceId DeviceId);

public sealed record RefreshSessionCommand(
    string RefreshToken,
    DeviceId DeviceId);

public sealed record LogoutCommand(
    string RefreshToken,
    DeviceId DeviceId);

public sealed record StepUpCommand(
    string Password,
    string Purpose);
