using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShareApp.Web.Data;
using ShareApp.Web.Entities;
using ShareApp.Web.Models.ViewModels;
using ShareApp.Web.Services.Interfaces;

namespace ShareApp.Web.Controllers;

[Authorize]
public class MessagesController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _emailService;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IEmailService emailService,
        ILogger<MessagesController> logger)
    {
        _context = context;
        _userManager = userManager;
        _emailService = emailService;
        _logger = logger;
    }

    // GET: Messages
    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(HttpContext.User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        // Get all conversations
        var sentMessages = await _context.Messages
            .Include(m => m.Receiver)
            .Include(m => m.Item)
            .Where(m => m.SenderId == userId)
            .ToListAsync();

        var receivedMessages = await _context.Messages
            .Include(m => m.Sender)
            .Include(m => m.Item)
            .Where(m => m.ReceiverId == userId)
            .ToListAsync();

        // Group by other user
        var conversations = new Dictionary<string, ConversationViewModel>();

        foreach (var message in sentMessages)
        {
            if (!conversations.ContainsKey(message.ReceiverId))
            {
                conversations[message.ReceiverId] = new ConversationViewModel
                {
                    OtherUserId = message.ReceiverId,
                    OtherUserName = $"{message.Receiver.FirstName} {message.Receiver.LastName}",
                    LastMessageDate = message.CreatedAt,
                    LastMessagePreview = message.Content.Length > 50
                        ? message.Content.Substring(0, 50) + "..."
                        : message.Content,
                    UnreadCount = 0,
                    ItemId = message.ItemId,
                    ItemTitle = message.Item?.Title
                };
            }
            else if (message.CreatedAt > conversations[message.ReceiverId].LastMessageDate)
            {
                conversations[message.ReceiverId].LastMessageDate = message.CreatedAt;
                conversations[message.ReceiverId].LastMessagePreview = message.Content.Length > 50
                    ? message.Content.Substring(0, 50) + "..."
                    : message.Content;
            }
        }

        foreach (var message in receivedMessages)
        {
            if (!conversations.ContainsKey(message.SenderId))
            {
                conversations[message.SenderId] = new ConversationViewModel
                {
                    OtherUserId = message.SenderId,
                    OtherUserName = $"{message.Sender.FirstName} {message.Sender.LastName}",
                    LastMessageDate = message.CreatedAt,
                    LastMessagePreview = message.Content.Length > 50
                        ? message.Content.Substring(0, 50) + "..."
                        : message.Content,
                    UnreadCount = !message.IsRead ? 1 : 0,
                    ItemId = message.ItemId,
                    ItemTitle = message.Item?.Title
                };
            }
            else
            {
                if (message.CreatedAt > conversations[message.SenderId].LastMessageDate)
                {
                    conversations[message.SenderId].LastMessageDate = message.CreatedAt;
                    conversations[message.SenderId].LastMessagePreview = message.Content.Length > 50
                        ? message.Content.Substring(0, 50) + "..."
                        : message.Content;
                }
                if (!message.IsRead)
                {
                    conversations[message.SenderId].UnreadCount++;
                }
            }
        }

        var viewModel = new MessagesInboxViewModel
        {
            Conversations = conversations.Values
                .OrderByDescending(c => c.LastMessageDate)
                .ToList(),
            TotalUnread = conversations.Values.Sum(c => c.UnreadCount)
        };

        return View(viewModel);
    }

    // GET: Messages/Conversation/userId
    public async Task<IActionResult> Conversation(string id, int? itemId = null)
    {
        var userId = _userManager.GetUserId(HttpContext.User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var otherUser = await _userManager.FindByIdAsync(id);
        if (otherUser == null)
        {
            return NotFound();
        }

        // Get all messages between users
        var messages = await _context.Messages
            .Include(m => m.Sender)
            .Include(m => m.Receiver)
            .Include(m => m.Item)
            .Where(m => (m.SenderId == userId && m.ReceiverId == id) ||
                       (m.SenderId == id && m.ReceiverId == userId))
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        // Mark received messages as read
        var unreadMessages = messages.Where(m => m.ReceiverId == userId && !m.IsRead).ToList();
        foreach (var message in unreadMessages)
        {
            message.IsRead = true;
            message.ReadAt = DateTime.UtcNow;
        }
        if (unreadMessages.Any())
        {
            await _context.SaveChangesAsync();
        }

        // Get item info if provided
        Item? item = null;
        if (itemId.HasValue)
        {
            item = await _context.Items.FindAsync(itemId.Value);
        }

        var viewModel = new ConversationDetailsViewModel
        {
            OtherUserId = id,
            OtherUserName = $"{otherUser.FirstName} {otherUser.LastName}",
            Messages = messages.Select(m => new MessageViewModel
            {
                Id = m.Id,
                SenderId = m.SenderId,
                SenderName = $"{m.Sender.FirstName} {m.Sender.LastName}",
                ReceiverId = m.ReceiverId,
                ReceiverName = $"{m.Receiver.FirstName} {m.Receiver.LastName}",
                Content = m.Content,
                IsRead = m.IsRead,
                CreatedAt = m.CreatedAt,
                ReadAt = m.ReadAt,
                ItemId = m.ItemId,
                ItemTitle = m.Item?.Title,
                IsSent = m.SenderId == userId
            }).ToList(),
            SendMessage = new SendMessageViewModel
            {
                ReceiverId = id,
                ReceiverName = $"{otherUser.FirstName} {otherUser.LastName}",
                ItemId = itemId,
                ItemTitle = item?.Title
            },
            ItemId = itemId,
            ItemTitle = item?.Title
        };

        return View(viewModel);
    }

    // POST: Messages/Send
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(SendMessageViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Invalid message data.";
            return RedirectToAction("Conversation", new { id = model.ReceiverId, itemId = model.ItemId });
        }

        var userId = _userManager.GetUserId(HttpContext.User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        // Verify receiver exists
        var receiver = await _userManager.FindByIdAsync(model.ReceiverId);
        if (receiver == null)
        {
            TempData["Error"] = "Receiver not found.";
            return RedirectToAction("Index");
        }

        // Create message
        var message = new Message
        {
            SenderId = userId,
            ReceiverId = model.ReceiverId,
            Content = model.Content,
            ItemId = model.ItemId,
            TransactionId = model.TransactionId,
            CreatedAt = DateTime.UtcNow,
            IsRead = false
        };

        _context.Messages.Add(message);

        // Create notification
        var notification = new Notification
        {
            UserId = model.ReceiverId,
            Type = NotificationType.Message,
            Title = "New Message",
            Message = $"You have a new message from {(await _userManager.FindByIdAsync(userId))?.FirstName}",
            RelatedEntityId = message.Id,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync();

        // Send email notification
        var sender = await _userManager.FindByIdAsync(userId);
        if (sender != null && !string.IsNullOrEmpty(receiver.Email))
        {
            var senderName = $"{sender.FirstName} {sender.LastName}";
            var receiverName = $"{receiver.FirstName} {receiver.LastName}";
            var messagePreview = model.Content.Length > 100
                ? model.Content.Substring(0, 100) + "..."
                : model.Content;
            await _emailService.SendNewMessageEmailAsync(receiver.Email, receiverName, senderName, messagePreview);
        }

        TempData["Success"] = "Message sent successfully!";
        return RedirectToAction("Conversation", new { id = model.ReceiverId, itemId = model.ItemId });
    }

    // GET: Messages/Compose?userId=&itemId=
    public async Task<IActionResult> Compose(string userId, int? itemId = null)
    {
        var currentUserId = _userManager.GetUserId(HttpContext.User);
        if (currentUserId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var receiver = await _userManager.FindByIdAsync(userId);
        if (receiver == null)
        {
            return NotFound();
        }

        Item? item = null;
        if (itemId.HasValue)
        {
            item = await _context.Items.FindAsync(itemId.Value);
        }

        var viewModel = new SendMessageViewModel
        {
            ReceiverId = userId,
            ReceiverName = $"{receiver.FirstName} {receiver.LastName}",
            ItemId = itemId,
            ItemTitle = item?.Title
        };

        return View(viewModel);
    }

    // POST: Messages/Compose
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Compose(SendMessageViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var userId = _userManager.GetUserId(HttpContext.User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var message = new Message
        {
            SenderId = userId,
            ReceiverId = model.ReceiverId,
            Content = model.Content,
            ItemId = model.ItemId,
            TransactionId = model.TransactionId,
            CreatedAt = DateTime.UtcNow
        };

        _context.Messages.Add(message);

        // Create notification
        var sender = await _userManager.FindByIdAsync(userId);
        var notification = new Notification
        {
            UserId = model.ReceiverId,
            Type = NotificationType.Message,
            Title = "New Message",
            Message = $"You have a new message from {sender?.FirstName}",
            RelatedEntityId = message.Id,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync();

        // Send email notification
        var receiver = await _userManager.FindByIdAsync(model.ReceiverId);
        if (sender != null && receiver != null && !string.IsNullOrEmpty(receiver.Email))
        {
            var senderName = $"{sender.FirstName} {sender.LastName}";
            var receiverName = $"{receiver.FirstName} {receiver.LastName}";
            var messagePreview = model.Content.Length > 100
                ? model.Content.Substring(0, 100) + "..."
                : model.Content;
            await _emailService.SendNewMessageEmailAsync(receiver.Email, receiverName, senderName, messagePreview);
        }

        TempData["Success"] = "Message sent successfully!";
        return RedirectToAction("Conversation", new { id = model.ReceiverId, itemId = model.ItemId });
    }

    // GET: API endpoint for unread count
    [HttpGet]
    public async Task<IActionResult> GetUnreadCount()
    {
        var userId = _userManager.GetUserId(HttpContext.User);
        if (userId == null)
        {
            return Json(new { count = 0 });
        }

        var unreadCount = await _context.Messages
            .CountAsync(m => m.ReceiverId == userId && !m.IsRead);

        return Json(new { count = unreadCount });
    }
}
