using WillVault.Domain.Common;
using WillVault.Domain.Enums;

namespace WillVault.Domain.Entities;

public class VaultItem : BaseEntity
{
    public Guid OwnerId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public VaultItemType ItemType { get; set; }

    /// <summary>
    /// For text notes, stores the encrypted content directly.
    /// For files (voice, video, document), stores the relative path to the encrypted file.
    /// </summary>
    public string? ContentText { get; set; }
    public string? ContentPath { get; set; }

    /// <summary>
    /// Original filename for uploaded files.
    /// </summary>
    public string? OriginalFileName { get; set; }
    public string? ContentType { get; set; }
    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// Reference to the encryption key used for this item's content.
    /// </summary>
    public string? EncryptionKeyId { get; set; }

    public bool IsArchived { get; set; }

    // Navigation properties
    public VaultOwner Owner { get; set; } = null!;
    public ICollection<VaultItemRecipient> VaultItemRecipients { get; set; } = new List<VaultItemRecipient>();

    // Will-specific properties (only used when ItemType == Will)
    public Guid? ExecutorId { get; set; }
    public string? LegalNotes { get; set; }
    public string? SpecialInstructions { get; set; }
}
