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
public class ReviewsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _emailService;
    private readonly ILogger<ReviewsController> _logger;

    public ReviewsController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IEmailService emailService,
        ILogger<ReviewsController> logger)
    {
        _context = context;
        _userManager = userManager;
        _emailService = emailService;
        _logger = logger;
    }

    // GET: Reviews/Create?transactionId=1
    public async Task<IActionResult> Create(int transactionId)
    {
        var userId = _userManager.GetUserId(HttpContext.User);
        if (userId == null)
        {
            return RedirectToAction("Login", "Account");
        }

        var transaction = await _context.Transactions
            .Include(t => t.Item)
            .Include(t => t.Buyer)
            .Include(t => t.Seller)
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        if (transaction == null)
        {
            return NotFound();
        }

        // Verify user is part of the transaction
        if (transaction.BuyerId != userId && transaction.SellerId != userId)
        {
            return Forbid();
        }

        // Verify transaction is completed
        if (transaction.Status != TransactionStatus.Completed)
        {
            TempData["Error"] = "You can only review completed transactions.";
            return RedirectToAction("Details", "Transactions", new { id = transactionId });
        }

        // Check if user already reviewed
        var existingReview = await _context.Reviews
            .FirstOrDefaultAsync(r => r.TransactionId == transactionId && r.ReviewerId == userId);

        if (existingReview != null)
        {
            TempData["Error"] = "You have already reviewed this transaction.";
            return RedirectToAction("Details", "Transactions", new { id = transactionId });
        }

        // Determine who to review (the other party)
        var reviewedUserId = transaction.BuyerId == userId ? transaction.SellerId : transaction.BuyerId;
        var reviewedUser = transaction.BuyerId == userId ? transaction.Seller : transaction.Buyer;

        var viewModel = new CreateReviewViewModel
        {
            TransactionId = transactionId,
            ItemId = transaction.ItemId,
            ReviewedUserId = reviewedUserId,
            ItemTitle = transaction.Item.Title,
            ReviewedUserName = $"{reviewedUser.FirstName} {reviewedUser.LastName}"
        };

        return View(viewModel);
    }

    // POST: Reviews/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateReviewViewModel model)
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

        var transaction = await _context.Transactions
            .Include(t => t.Item)
            .FirstOrDefaultAsync(t => t.Id == model.TransactionId);

        if (transaction == null)
        {
            return NotFound();
        }

        // Verify user is part of the transaction
        if (transaction.BuyerId != userId && transaction.SellerId != userId)
        {
            return Forbid();
        }

        // Check if already reviewed
        var existingReview = await _context.Reviews
            .FirstOrDefaultAsync(r => r.TransactionId == model.TransactionId && r.ReviewerId == userId);

        if (existingReview != null)
        {
            TempData["Error"] = "You have already reviewed this transaction.";
            return RedirectToAction("Details", "Transactions", new { id = model.TransactionId });
        }

        // Create review
        var review = new Review
        {
            ItemId = model.ItemId,
            ReviewerId = userId,
            ReviewedUserId = model.ReviewedUserId,
            Rating = model.Rating,
            Comment = model.Comment,
            TransactionId = model.TransactionId,
            CreatedAt = DateTime.UtcNow
        };

        _context.Reviews.Add(review);

        // Update user's average rating
        await UpdateUserRatingAsync(model.ReviewedUserId);

        await _context.SaveChangesAsync();

        // Create notification for reviewed user
        var notification = new Notification
        {
            UserId = model.ReviewedUserId,
            Type = NotificationType.Review,
            Title = "New Review Received",
            Message = $"You received a {model.Rating}-star review.",
            RelatedEntityId = review.Id,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync();

        // Send email notification
        var reviewedUser = await _userManager.FindByIdAsync(model.ReviewedUserId);
        var reviewer = await _userManager.FindByIdAsync(userId);
        if (reviewedUser != null && reviewer != null && !string.IsNullOrEmpty(reviewedUser.Email))
        {
            var reviewedUserName = $"{reviewedUser.FirstName} {reviewedUser.LastName}";
            var reviewerName = $"{reviewer.FirstName} {reviewer.LastName}";
            await _emailService.SendReviewReceivedEmailAsync(
                reviewedUser.Email,
                reviewedUserName,
                model.Rating,
                model.Comment,
                reviewerName);
        }

        TempData["Success"] = "Review submitted successfully!";
        return RedirectToAction("Details", "Transactions", new { id = model.TransactionId });
    }

    // GET: Reviews/UserReviews/5
    [AllowAnonymous]
    [ActionName("User")]
    public async Task<IActionResult> UserReviews(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        var reviews = await _context.Reviews
            .Include(r => r.Reviewer)
            .Include(r => r.Item)
            .Where(r => r.ReviewedUserId == id)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        var reviewViewModels = reviews.Select(r => new ReviewViewModel
        {
            Id = r.Id,
            ItemId = r.ItemId ?? 0,
            ItemTitle = r.Item?.Title ?? "Unknown Item",
            ReviewerName = r.Reviewer.FirstName + " " + r.Reviewer.LastName,
            ReviewedUserName = user.FirstName + " " + user.LastName,
            Rating = r.Rating,
            Comment = r.Comment,
            CreatedAt = r.CreatedAt,
            TransactionId = r.TransactionId
        }).ToList();

        var viewModel = new UserReviewsViewModel
        {
            UserId = id,
            UserName = $"{user.FirstName} {user.LastName}",
            AverageRating = user.Rating,
            TotalReviews = reviewViewModels.Count,
            Reviews = reviewViewModels
        };

        return View(viewModel);
    }

    private async Task UpdateUserRatingAsync(string userId)
    {
        var reviews = await _context.Reviews
            .Where(r => r.ReviewedUserId == userId)
            .ToListAsync();

        if (reviews.Any())
        {
            var averageRating = reviews.Average(r => (decimal)r.Rating);
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                user.Rating = Math.Round(averageRating, 2);
                await _userManager.UpdateAsync(user);
            }
        }
    }
}
