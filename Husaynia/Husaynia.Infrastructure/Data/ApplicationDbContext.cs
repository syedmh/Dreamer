using Husaynia.Core.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Data
{
    public class ApplicationDbContext : IdentityDbContext<AppUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Event> Events { get; set; }
        public DbSet<Announcement> Announcements { get; set; }
        public DbSet<MediaItem> MediaItems { get; set; }
        public DbSet<PrayerTime> PrayerTimes { get; set; }
        public DbSet<Donation> Donations { get; set; }
        public DbSet<DonationCampaign> DonationCampaigns { get; set; }
        public DbSet<SiteConfiguration> SiteConfigurations { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Event configuration
            modelBuilder.Entity<Event>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Description).IsRequired();
                entity.Property(e => e.Location).HasMaxLength(200);
                entity.Property(e => e.Category).HasMaxLength(50);
                entity.HasIndex(e => e.StartDateTime);
                entity.HasIndex(e => e.IsPublished);
            });

            // Announcement configuration
            modelBuilder.Entity<Announcement>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Content).IsRequired();
                entity.Property(e => e.Excerpt).HasMaxLength(500);
                entity.Property(e => e.Author).HasMaxLength(100);
                entity.HasIndex(e => e.PublishedDate);
                entity.HasIndex(e => e.IsPinned);
                entity.HasIndex(e => e.IsPublished);
            });

            // MediaItem configuration
            modelBuilder.Entity<MediaItem>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Url).IsRequired();
                entity.Property(e => e.Album).HasMaxLength(100);
                entity.HasIndex(e => e.Type);
                entity.HasIndex(e => e.Album);
            });

            // PrayerTime configuration
            modelBuilder.Entity<PrayerTime>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Date).IsUnique();
            });

            // Donation configuration
            modelBuilder.Entity<Donation>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
                entity.Property(e => e.DonorName).HasMaxLength(100);
                entity.Property(e => e.DonorEmail).HasMaxLength(100);
                entity.Property(e => e.PaymentMethod).HasMaxLength(50);
                entity.Property(e => e.Campaign).HasMaxLength(100);
                entity.HasIndex(e => e.DonationDate);
                entity.HasIndex(e => e.Status);

                // Configure relationship with DonationCampaign
                entity.HasOne(d => d.DonationCampaign)
                      .WithMany(c => c.Donations)
                      .HasForeignKey(d => d.DonationCampaignId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // DonationCampaign configuration
            modelBuilder.Entity<DonationCampaign>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
                entity.Property(e => e.GoalAmount).HasColumnType("decimal(18,2)");
                entity.Property(e => e.CurrentAmount).HasColumnType("decimal(18,2)");
                entity.HasIndex(e => e.IsActive);
            });

            // SiteConfiguration configuration
            modelBuilder.Entity<SiteConfiguration>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Key).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Category).HasMaxLength(50);
                entity.HasIndex(e => e.Key).IsUnique();
                entity.HasIndex(e => e.Category);
            });
        }
    }
}
