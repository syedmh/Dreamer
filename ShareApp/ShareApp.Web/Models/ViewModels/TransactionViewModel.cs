using ShareApp.Web.Entities;

namespace ShareApp.Web.Models.ViewModels;

public class TransactionViewModel
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public string ItemTitle { get; set; } = string.Empty;
    public string? ItemImageUrl { get; set; }
    public string BuyerName { get; set; } = string.Empty;
    public string SellerName { get; set; } = string.Empty;
    public TransactionType TransactionType { get; set; }
    public decimal? Amount { get; set; }
    public TransactionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Buyer information
    public string? BuyerEmail { get; set; }
    public string? BuyerPhone { get; set; }

    // Seller information
    public string? SellerEmail { get; set; }
    public string? SellerPhone { get; set; }

    // Item details
    public ItemType ItemType { get; set; }
    public string? PickupAddress { get; set; }

    // For current user context
    public bool IsBuyer { get; set; }
    public bool IsSeller { get; set; }
}

public class TransactionListViewModel
{
    public IEnumerable<TransactionViewModel> Purchases { get; set; } = new List<TransactionViewModel>();
    public IEnumerable<TransactionViewModel> Sales { get; set; } = new List<TransactionViewModel>();
}

public class TransactionDetailsViewModel
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public string ItemTitle { get; set; } = string.Empty;
    public string ItemDescription { get; set; } = string.Empty;
    public string? ItemImageUrl { get; set; }

    public string BuyerId { get; set; } = string.Empty;
    public string BuyerName { get; set; } = string.Empty;
    public string BuyerEmail { get; set; } = string.Empty;

    public string SellerId { get; set; } = string.Empty;
    public string SellerName { get; set; } = string.Empty;
    public string SellerEmail { get; set; } = string.Empty;

    public TransactionType TransactionType { get; set; }
    public decimal? Amount { get; set; }
    public TransactionStatus Status { get; set; }

    public string? StripePaymentIntentId { get; set; }
    public string? StripeChargeId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    public string PickupAddressLine1 { get; set; } = string.Empty;
    public string? PickupAddressLine2 { get; set; }
    public string PickupCity { get; set; } = string.Empty;
    public string PickupState { get; set; } = string.Empty;
    public string PickupZipCode { get; set; } = string.Empty;

    public bool IsBuyer { get; set; }
    public bool IsSeller { get; set; }
    public bool CanCancel { get; set; }
    public bool CanComplete { get; set; }
}

public class CreateTransactionViewModel
{
    public int ItemId { get; set; }
    public string? Message { get; set; }
}
