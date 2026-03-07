using System.ComponentModel.DataAnnotations;

namespace ShareApp.Web.Models.ViewModels;

public class MessageViewModel
{
    public int Id { get; set; }
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string ReceiverId { get; set; } = string.Empty;
    public string ReceiverName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public int? ItemId { get; set; }
    public string? ItemTitle { get; set; }
    public bool IsSent { get; set; } // Is current user the sender
}

public class ConversationViewModel
{
    public string OtherUserId { get; set; } = string.Empty;
    public string OtherUserName { get; set; } = string.Empty;
    public string? LastMessagePreview { get; set; }
    public DateTime? LastMessageDate { get; set; }
    public int UnreadCount { get; set; }
    public int? ItemId { get; set; }
    public string? ItemTitle { get; set; }
}

public class SendMessageViewModel
{
    [Required]
    public string ReceiverId { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000, ErrorMessage = "Message cannot exceed 2000 characters")]
    public string Content { get; set; } = string.Empty;

    public int? ItemId { get; set; }
    public int? TransactionId { get; set; }

    // For display
    public string? ReceiverName { get; set; }
    public string? ItemTitle { get; set; }
}

public class MessagesInboxViewModel
{
    public List<ConversationViewModel> Conversations { get; set; } = new();
    public int TotalUnread { get; set; }
}

public class ConversationDetailsViewModel
{
    public string OtherUserId { get; set; } = string.Empty;
    public string OtherUserName { get; set; } = string.Empty;
    public List<MessageViewModel> Messages { get; set; } = new();
    public SendMessageViewModel SendMessage { get; set; } = new();
    public int? ItemId { get; set; }
    public string? ItemTitle { get; set; }
}
