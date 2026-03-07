using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using ShareApp.Web.Services.Interfaces;

namespace ShareApp.Web.Services.Implementations
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;
        private readonly string _baseUrl;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _baseUrl = configuration["ApplicationSettings:BaseUrl"] ?? "https://localhost:7017";
        }

        public async Task SendTransactionCreatedEmailAsync(string recipientEmail, string recipientName, string itemTitle, string buyerName, decimal? amount)
        {
            var subject = "New Transaction - ShareApp";
            var body = $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h2>New Transaction Notification</h2>
                    <p>Hello {recipientName},</p>
                    <p>You have a new transaction for your item: <strong>{itemTitle}</strong></p>
                    <p>Buyer: {buyerName}</p>
                    {(amount.HasValue ? $"<p>Amount: ${amount.Value:F2}</p>" : "<p>This is a free item request.</p>")}
                    <p>Please log in to your account to view the transaction details and complete the exchange.</p>
                    <p><a href='{_baseUrl}/Transactions' style='background-color: #0d6efd; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>View Transactions</a></p>
                    <hr>
                    <p style='color: #666; font-size: 12px;'>ShareApp - Share Food, Build Community</p>
                </body>
                </html>
            ";

            await SendEmailAsync(recipientEmail, recipientName, subject, body);
        }

        public async Task SendTransactionCompletedEmailAsync(string recipientEmail, string recipientName, string itemTitle, decimal? amount)
        {
            var subject = "Transaction Completed - ShareApp";
            var body = $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h2>Transaction Completed</h2>
                    <p>Hello {recipientName},</p>
                    <p>The transaction for <strong>{itemTitle}</strong> has been marked as completed.</p>
                    {(amount.HasValue ? $"<p>Amount: ${amount.Value:F2}</p>" : "")}
                    <p>You can now leave a review for this transaction.</p>
                    <p><a href='{_baseUrl}/Transactions' style='background-color: #0d6efd; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>View Transactions</a></p>
                    <hr>
                    <p style='color: #666; font-size: 12px;'>ShareApp - Share Food, Build Community</p>
                </body>
                </html>
            ";

            await SendEmailAsync(recipientEmail, recipientName, subject, body);
        }

        public async Task SendNewMessageEmailAsync(string recipientEmail, string recipientName, string senderName, string messagePreview)
        {
            var subject = "New Message - ShareApp";
            var body = $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h2>New Message</h2>
                    <p>Hello {recipientName},</p>
                    <p>You have received a new message from <strong>{senderName}</strong>:</p>
                    <blockquote style='background-color: #f8f9fa; padding: 15px; border-left: 4px solid #0d6efd;'>
                        {messagePreview}
                    </blockquote>
                    <p><a href='{_baseUrl}/Messages' style='background-color: #0d6efd; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>View Messages</a></p>
                    <hr>
                    <p style='color: #666; font-size: 12px;'>ShareApp - Share Food, Build Community</p>
                </body>
                </html>
            ";

            await SendEmailAsync(recipientEmail, recipientName, subject, body);
        }

        public async Task SendReviewReceivedEmailAsync(string recipientEmail, string recipientName, int rating, string? comment, string reviewerName)
        {
            var stars = new string('⭐', rating);
            var subject = "New Review Received - ShareApp";
            var body = $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h2>New Review Received</h2>
                    <p>Hello {recipientName},</p>
                    <p><strong>{reviewerName}</strong> has left you a review:</p>
                    <p style='font-size: 24px;'>{stars} ({rating}/5)</p>
                    {(!string.IsNullOrEmpty(comment) ? $"<blockquote style='background-color: #f8f9fa; padding: 15px; border-left: 4px solid #0d6efd;'>{comment}</blockquote>" : "")}
                    <p><a href='{_baseUrl}/Profile' style='background-color: #0d6efd; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>View Profile</a></p>
                    <hr>
                    <p style='color: #666; font-size: 12px;'>ShareApp - Share Food, Build Community</p>
                </body>
                </html>
            ";

            await SendEmailAsync(recipientEmail, recipientName, subject, body);
        }

        public async Task SendPaymentConfirmationEmailAsync(string recipientEmail, string recipientName, string itemTitle, decimal amount, string transactionId)
        {
            var subject = "Payment Confirmation - ShareApp";
            var body = $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h2>Payment Confirmation</h2>
                    <p>Hello {recipientName},</p>
                    <p>Your payment has been successfully processed!</p>
                    <div style='background-color: #f8f9fa; padding: 20px; border-radius: 5px; margin: 20px 0;'>
                        <p><strong>Item:</strong> {itemTitle}</p>
                        <p><strong>Amount:</strong> ${amount:F2}</p>
                        <p><strong>Transaction ID:</strong> {transactionId}</p>
                    </div>
                    <p>The seller will be notified and will arrange the exchange with you.</p>
                    <p><a href='{_baseUrl}/Transactions' style='background-color: #0d6efd; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>View Transaction</a></p>
                    <hr>
                    <p style='color: #666; font-size: 12px;'>ShareApp - Share Food, Build Community</p>
                </body>
                </html>
            ";

            await SendEmailAsync(recipientEmail, recipientName, subject, body);
        }

        public async Task SendWelcomeEmailAsync(string recipientEmail, string recipientName)
        {
            var subject = "Welcome to ShareApp!";
            var body = $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h2>Welcome to ShareApp!</h2>
                    <p>Hello {recipientName},</p>
                    <p>Thank you for joining ShareApp, the community-driven platform for sharing food with your neighbors!</p>
                    <h3>Get Started:</h3>
                    <ul>
                        <li><strong>Browse the Map:</strong> Discover food items shared by neighbors in your area</li>
                        <li><strong>List Items:</strong> Share food items you want to sell, give away, or barter</li>
                        <li><strong>Connect:</strong> Message other users and build your community</li>
                    </ul>
                    <p><a href='{_baseUrl}' style='background-color: #0d6efd; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>Explore ShareApp</a></p>
                    <hr>
                    <p style='color: #666; font-size: 12px;'>ShareApp - Share Food, Build Community</p>
                </body>
                </html>
            ";

            await SendEmailAsync(recipientEmail, recipientName, subject, body);
        }

        private async Task SendEmailAsync(string recipientEmail, string recipientName, string subject, string htmlBody)
        {
            try
            {
                // Get email configuration
                var smtpServer = _configuration["Email:SmtpServer"];
                var smtpPort = int.Parse(_configuration["Email:SmtpPort"] ?? "587");
                var senderEmail = _configuration["Email:SenderEmail"];
                var senderName = _configuration["Email:SenderName"] ?? "ShareApp";
                var username = _configuration["Email:Username"];
                var password = _configuration["Email:Password"];

                // Check if email is configured
                if (string.IsNullOrEmpty(smtpServer) || string.IsNullOrEmpty(senderEmail))
                {
                    _logger.LogWarning("Email service is not configured. Skipping email to {Email}", recipientEmail);
                    return;
                }

                // Create message
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(senderName, senderEmail));
                message.To.Add(new MailboxAddress(recipientName, recipientEmail));
                message.Subject = subject;

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = htmlBody
                };
                message.Body = bodyBuilder.ToMessageBody();

                // Send email
                using var client = new SmtpClient();
                await client.ConnectAsync(smtpServer, smtpPort, SecureSocketOptions.StartTls);

                if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                {
                    await client.AuthenticateAsync(username, password);
                }

                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                _logger.LogInformation("Email sent successfully to {Email}", recipientEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {Email}", recipientEmail);
                // Don't throw - email failures shouldn't break the application
            }
        }
    }
}
