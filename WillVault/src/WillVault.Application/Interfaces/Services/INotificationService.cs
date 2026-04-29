namespace WillVault.Application.Interfaces.Services;

/// <summary>
/// Abstraction for sending notifications across multiple channels (email, SMS, push).
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Sends an email to the specified recipient.
    /// </summary>
    /// <param name="to">The recipient email address.</param>
    /// <param name="subject">The email subject line.</param>
    /// <param name="body">The email body content (HTML or plain text).</param>
    /// <param name="attachments">Optional file attachments as name-content pairs.</param>
    Task SendEmailAsync(string to, string subject, string body,
        IReadOnlyDictionary<string, byte[]>? attachments = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an SMS message to the specified phone number.
    /// </summary>
    /// <param name="to">The recipient phone number in E.164 format.</param>
    /// <param name="message">The SMS message text.</param>
    Task SendSmsAsync(string to, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a push notification to the specified device.
    /// </summary>
    /// <param name="deviceToken">The target device's push notification token.</param>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    Task SendPushAsync(string deviceToken, string title, string body, CancellationToken cancellationToken = default);
}
