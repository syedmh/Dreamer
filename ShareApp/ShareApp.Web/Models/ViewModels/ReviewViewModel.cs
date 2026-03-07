using System.ComponentModel.DataAnnotations;

namespace ShareApp.Web.Models.ViewModels;

public class ReviewViewModel
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public string ItemTitle { get; set; } = string.Empty;
    public string ReviewerName { get; set; } = string.Empty;
    public string ReviewedUserName { get; set; } = string.Empty;
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? TransactionId { get; set; }
}

public class CreateReviewViewModel
{
    [Required]
    public int TransactionId { get; set; }

    [Required]
    public int ItemId { get; set; }

    [Required]
    public string ReviewedUserId { get; set; } = string.Empty;

    [Required(ErrorMessage = "Rating is required")]
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5")]
    public int Rating { get; set; }

    [MaxLength(1000, ErrorMessage = "Comment cannot exceed 1000 characters")]
    public string? Comment { get; set; }

    // For display purposes
    public string? ItemTitle { get; set; }
    public string? ReviewedUserName { get; set; }
}

public class UserReviewsViewModel
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public decimal? AverageRating { get; set; }
    public int TotalReviews { get; set; }
    public IEnumerable<ReviewViewModel> Reviews { get; set; } = new List<ReviewViewModel>();
}
