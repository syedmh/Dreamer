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
public class TransactionsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IItemService _itemService;
    private readonly IEmailService _emailService;
    private readonly ILogger<TransactionsController> _logger;

    public TransactionsController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IItemService itemService,
        IEmailService emailService,
        ILogger<TransactionsController> logger)
    {
        _context = context;
        _userManager = userManager;
        _itemService = itemService;
        _emailService = emailService;
        _logger = logger;
    }

    // GET: Transactions
    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var purchases = await _context.Transactions
            .Include(t => t.Item)
                .ThenInclude(i => i.Images)
            .Include(t => t.Seller)
            .Where(t => t.BuyerId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TransactionViewModel
            {
                Id = t.Id,
                ItemId = t.ItemId,
                ItemTitle = t.Item.Title,
                ItemImageUrl = t.Item.Images.Any() ? t.Item.Images.First().ImageUrl : null,
                SellerName = t.Seller.FirstName + " " + t.Seller.LastName,
                TransactionType = t.TransactionType,
                Amount = t.Amount,
                Status = t.Status,
                CreatedAt = t.CreatedAt,
                CompletedAt = t.CompletedAt,
                IsBuyer = true,
                IsSeller = false
            })
            .ToListAsync();

        var sales = await _context.Transactions
            .Include(t => t.Item)
                .ThenInclude(i => i.Images)
            .Include(t => t.Buyer)
            .Where(t => t.SellerId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TransactionViewModel
            {
                Id = t.Id,
                ItemId = t.ItemId,
                ItemTitle = t.Item.Title,
                ItemImageUrl = t.Item.Images.Any() ? t.Item.Images.First().ImageUrl : null,
                BuyerName = t.Buyer.FirstName + " " + t.Buyer.LastName,
                TransactionType = t.TransactionType,
                Amount = t.Amount,
                Status = t.Status,
                CreatedAt = t.CreatedAt,
                CompletedAt = t.CompletedAt,
                IsBuyer = false,
                IsSeller = true
            })
            .ToListAsync();

        var viewModel = new TransactionListViewModel
        {
            Purchases = purchases,
            Sales = sales
        };

        return View(viewModel);
    }

    // GET: Transactions/Details/5
    public async Task<IActionResult> Details(int id)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var transaction = await _context.Transactions
            .Include(t => t.Item)
                .ThenInclude(i => i.Images)
            .Include(t => t.Item.Location)
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == id
                && (t.BuyerId == userId || t.SellerId == userId));

        if (transaction == null)
        {
            return NotFound();
        }

        var viewModel = new TransactionDetailsViewModel
        {
            Id = transaction.Id,
            ItemId = transaction.ItemId,
            ItemTitle = transaction.Item.Title,
            ItemDescription = transaction.Item.Description,
            ItemImageUrl = transaction.Item.Images.Any() ? transaction.Item.Images.First().ImageUrl : null,
            BuyerId = transaction.BuyerId,
            BuyerName = transaction.Buyer.FirstName + " " + transaction.Buyer.LastName,
            BuyerEmail = transaction.Buyer.Email ?? string.Empty,
            SellerId = transaction.SellerId,
            SellerName = transaction.Seller.FirstName + " " + transaction.Seller.LastName,
            SellerEmail = transaction.Seller.Email ?? string.Empty,
            TransactionType = transaction.TransactionType,
            Amount = transaction.Amount,
            Status = transaction.Status,
            StripePaymentIntentId = transaction.StripePaymentIntentId,
            StripeChargeId = transaction.StripeChargeId,
            CreatedAt = transaction.CreatedAt,
            CompletedAt = transaction.CompletedAt,
            CancelledAt = transaction.CancelledAt,
            PickupAddressLine1 = transaction.Item.Location.AddressLine1,
            PickupAddressLine2 = transaction.Item.Location.AddressLine2,
            PickupCity = transaction.Item.Location.City,
            PickupState = transaction.Item.Location.State,
            PickupZipCode = transaction.Item.Location.ZipCode,
            IsBuyer = transaction.BuyerId == userId,
            IsSeller = transaction.SellerId == userId,
            CanCancel = transaction.Status == TransactionStatus.Pending && transaction.BuyerId == userId,
            CanComplete = transaction.Status == TransactionStatus.Pending && transaction.SellerId == userId
        };

        return View(viewModel);
    }

    // POST: Transactions/RequestItem
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestItem(int itemId)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var item = await _context.Items
            .Include(i => i.User)
            .FirstOrDefaultAsync(i => i.Id == itemId);

        if (item == null)
        {
            TempData["Error"] = "Item not found.";
            return RedirectToAction("Index", "Home");
        }

        // Check if item is available
        if (item.Status != ItemStatus.Available)
        {
            TempData["Error"] = "This item is no longer available.";
            return RedirectToAction("Details", "Items", new { id = itemId });
        }

        // Check if user owns the item
        if (item.UserId == userId)
        {
            TempData["Error"] = "You cannot request your own item.";
            return RedirectToAction("Details", "Items", new { id = itemId });
        }

        // Check if user already has a pending transaction for this item
        var existingTransaction = await _context.Transactions
            .FirstOrDefaultAsync(t => t.ItemId == itemId && t.BuyerId == userId && t.Status == TransactionStatus.Pending);

        if (existingTransaction != null)
        {
            TempData["Error"] = "You already have a pending request for this item.";
            return RedirectToAction("Details", "Items", new { id = itemId });
        }

        // Create transaction based on item type
        var transaction = new Transaction
        {
            ItemId = itemId,
            BuyerId = userId,
            SellerId = item.UserId,
            TransactionType = item.ItemType == ItemType.ForSale
                ? TransactionType.Purchase
                : item.ItemType == ItemType.Free
                    ? TransactionType.FreePickup
                    : TransactionType.Barter,
            Amount = item.Price,
            Status = TransactionStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _context.Transactions.Add(transaction);

        // Update item status to Reserved
        item.Status = ItemStatus.Reserved;
        item.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Create notification for seller
        var notification = new Notification
        {
            UserId = item.UserId,
            Type = NotificationType.NewTransaction,
            Title = "New Item Request",
            Message = $"Someone is interested in your item: {item.Title}",
            RelatedEntityId = transaction.Id,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync();

        // Send email notification to seller
        var buyer = await _userManager.GetUserAsync(User);
        if (buyer != null && !string.IsNullOrEmpty(item.User.Email))
        {
            var buyerName = $"{buyer.FirstName} {buyer.LastName}";
            var sellerName = $"{item.User.FirstName} {item.User.LastName}";
            await _emailService.SendTransactionCreatedEmailAsync(
                item.User.Email,
                sellerName,
                item.Title,
                buyerName,
                transaction.Amount);
        }

        TempData["Success"] = item.ItemType == ItemType.Free
            ? "Your request has been sent to the seller. They will contact you soon!"
            : "Your request has been sent. Please proceed to payment.";

        return RedirectToAction("Details", new { id = transaction.Id });
    }

    // POST: Transactions/Cancel/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var transaction = await _context.Transactions
            .Include(t => t.Item)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (transaction == null)
        {
            return NotFound();
        }

        // Only buyer can cancel pending transactions
        if (transaction.BuyerId != userId || transaction.Status != TransactionStatus.Pending)
        {
            return Forbid();
        }

        transaction.Status = TransactionStatus.Cancelled;
        transaction.CancelledAt = DateTime.UtcNow;

        // Make item available again
        transaction.Item.Status = ItemStatus.Available;
        transaction.Item.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        TempData["Success"] = "Transaction cancelled successfully.";
        return RedirectToAction("Index");
    }

    // POST: Transactions/Complete/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(int id)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var transaction = await _context.Transactions
            .Include(t => t.Item)
            .Include(t => t.Buyer)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (transaction == null)
        {
            return NotFound();
        }

        // Only seller can mark as completed
        if (transaction.SellerId != userId || transaction.Status != TransactionStatus.Pending)
        {
            return Forbid();
        }

        transaction.Status = TransactionStatus.Completed;
        transaction.CompletedAt = DateTime.UtcNow;

        // Mark item as sold/given
        transaction.Item.Status = ItemStatus.Sold;
        transaction.Item.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Create notification for buyer
        var notification = new Notification
        {
            UserId = transaction.BuyerId,
            Type = NotificationType.TransactionCompleted,
            Title = "Transaction Completed",
            Message = $"The transaction for {transaction.Item.Title} has been completed.",
            RelatedEntityId = transaction.Id,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync();

        // Send email notification to buyer
        if (!string.IsNullOrEmpty(transaction.Buyer.Email))
        {
            var buyerName = $"{transaction.Buyer.FirstName} {transaction.Buyer.LastName}";
            await _emailService.SendTransactionCompletedEmailAsync(
                transaction.Buyer.Email,
                buyerName,
                transaction.Item.Title,
                transaction.Amount);
        }

        TempData["Success"] = "Transaction marked as completed.";
        return RedirectToAction("Details", new { id = transaction.Id });
    }

    // GET: Transactions/PaymentSuccess
    public async Task<IActionResult> PaymentSuccess(int transactionId, string? payment_intent = null)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var transaction = await _context.Transactions
            .Include(t => t.Item)
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        if (transaction == null)
        {
            return NotFound();
        }

        // Verify user is the buyer
        if (transaction.BuyerId != userId)
        {
            return Forbid();
        }

        // Check if payment was successful
        if (!string.IsNullOrEmpty(payment_intent))
        {
            TempData["Success"] = "Payment successful! The seller will be notified to arrange pickup.";
        }
        else
        {
            TempData["Info"] = "Payment is being processed. Please check back shortly.";
        }

        return RedirectToAction("Details", new { id = transactionId });
    }
}
