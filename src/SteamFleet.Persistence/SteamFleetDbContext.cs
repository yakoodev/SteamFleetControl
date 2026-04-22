using System.Text.Json;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Identity;

namespace SteamFleet.Persistence;

public sealed class SteamFleetDbContext(DbContextOptions<SteamFleetDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<SteamAccount> SteamAccounts => Set<SteamAccount>();
    public DbSet<SteamAccountSecret> SteamAccountSecrets => Set<SteamAccountSecret>();
    public DbSet<SteamAccountTag> SteamAccountTags => Set<SteamAccountTag>();
    public DbSet<SteamAccountTagLink> SteamAccountTagLinks => Set<SteamAccountTagLink>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<FleetJob> Jobs => Set<FleetJob>();
    public DbSet<FleetJobItem> JobItems => Set<FleetJobItem>();
    public DbSet<JobSensitiveReport> JobSensitiveReports => Set<JobSensitiveReport>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SteamAccountGame> SteamAccountGames => Set<SteamAccountGame>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Folder>(entity =>
        {
            entity.ToTable("folders");
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(x => x.Parent)
                .WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SteamAccount>(entity =>
        {
            entity.ToTable("steam_accounts");
            entity.Property(x => x.LoginName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(255);
            entity.Property(x => x.SteamId64).HasMaxLength(20);
            entity.Property(x => x.ProfileUrl).HasMaxLength(512);
            entity.Property(x => x.Email).HasMaxLength(255);
            entity.Property(x => x.PhoneMasked).HasMaxLength(64);
            entity.Property(x => x.Note).HasMaxLength(4000);
            entity.Property(x => x.Proxy).HasMaxLength(512);
            entity.Property(x => x.ExternalSource).HasMaxLength(128);
            entity.Property(x => x.SteamFamilyId).HasMaxLength(64);
            entity.Property(x => x.SteamFamilyRole).HasMaxLength(64);
            entity.Property(x => x.LastRiskReasonCode).HasMaxLength(64);
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.Property(x => x.CreatedBy).HasMaxLength(255);
            entity.Property(x => x.UpdatedBy).HasMaxLength(255);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(64);
            entity.Property(x => x.RiskLevel).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(x => x.Folder)
                .WithMany(x => x.Accounts)
                .HasForeignKey(x => x.FolderId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => x.LoginName).IsUnique();
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.LastCheckAt);
            entity.HasIndex(x => x.SteamFamilyId);
            entity.HasIndex(x => x.IsExternal);
            entity.HasIndex(x => x.SteamId64);
            entity.HasIndex(x => x.RiskLevel);
            entity.HasIndex(x => x.AutoRetryAfter);
        });

        builder.Entity<SteamAccountSecret>(entity =>
        {
            entity.ToTable("steam_account_secrets");
            entity.Property(x => x.EncryptedPassword).HasColumnType("text");
            entity.Property(x => x.EncryptedSharedSecret).HasColumnType("text");
            entity.Property(x => x.EncryptedIdentitySecret).HasColumnType("text");
            entity.Property(x => x.EncryptedDeviceId).HasColumnType("text");
            entity.Property(x => x.EncryptedRevocationCode).HasColumnType("text");
            entity.Property(x => x.EncryptedSerialNumber).HasColumnType("text");
            entity.Property(x => x.EncryptedTokenGid).HasColumnType("text");
            entity.Property(x => x.EncryptedUri).HasColumnType("text");
            entity.Property(x => x.EncryptedLinkStatePayload).HasColumnType("text");
            entity.Property(x => x.EncryptedSessionPayload).HasColumnType("text");
            entity.Property(x => x.EncryptedRecoveryPayload).HasColumnType("text");
            entity.Property(x => x.GuardFullyEnrolled);
            entity.Property(x => x.EncryptionVersion).HasMaxLength(64).IsRequired();
            entity.HasOne(x => x.Account)
                .WithOne(x => x.Secret)
                .HasForeignKey<SteamAccountSecret>(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.AccountId).IsUnique();
        });

        builder.Entity<SteamAccountTag>(entity =>
        {
            entity.ToTable("steam_account_tags");
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Color).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
        });

        builder.Entity<SteamAccountTagLink>(entity =>
        {
            entity.ToTable("steam_account_tag_links");
            entity.HasKey(x => new { x.AccountId, x.TagId });
            entity.HasOne(x => x.Account)
                .WithMany(x => x.TagLinks)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Tag)
                .WithMany(x => x.AccountLinks)
                .HasForeignKey(x => x.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<FleetJob>(entity =>
        {
            entity.ToTable("jobs");
            entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(64);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.Property(x => x.CreatedBy).HasMaxLength(255);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CreatedAt);
        });

        builder.Entity<FleetJobItem>(entity =>
        {
            entity.ToTable("job_items");
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(64);
            entity.Property(x => x.RequestJson).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.Property(x => x.ResultJson).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.Property(x => x.ErrorText).HasColumnType("text");
            entity.HasOne(x => x.Job)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Account)
                .WithMany(x => x.JobItems)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.JobId, x.AccountId });
            entity.HasIndex(x => x.Status);
        });

        builder.Entity<JobSensitiveReport>(entity =>
        {
            entity.ToTable("job_sensitive_reports");
            entity.Property(x => x.EncryptedPayload).HasColumnType("text").IsRequired();
            entity.Property(x => x.EncryptionVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ConsumedBy).HasMaxLength(255);
            entity.HasOne(x => x.Job)
                .WithOne(x => x.SensitiveReport)
                .HasForeignKey<JobSensitiveReport>(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.JobId).IsUnique();
            entity.HasIndex(x => x.ConsumedAt);
        });

        builder.Entity<SteamAccountGame>(entity =>
        {
            entity.ToTable("steam_account_games");
            entity.Property(x => x.Name).HasMaxLength(512).IsRequired();
            entity.Property(x => x.ImgIconUrl).HasMaxLength(1024);
            entity.HasOne(x => x.Account)
                .WithMany(x => x.Games)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.AccountId, x.AppId }).IsUnique();
            entity.HasIndex(x => x.Name);
            entity.HasIndex(x => x.LastSyncedAt);
        });

        builder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.Property(x => x.EventType).HasConversion<string>().HasMaxLength(64);
            entity.Property(x => x.EntityType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.EntityId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(255);
            entity.Property(x => x.Ip).HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.HasIndex(x => x.EventType);
            entity.HasIndex(x => x.CreatedAt);
        });

        builder.Entity<SystemSetting>(entity =>
        {
            entity.ToTable("system_settings");
            entity.Property(x => x.Key).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ValueJson).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.HasIndex(x => x.Key).IsUnique();
        });

        builder.Entity<AppUser>().ToTable("users");
        builder.Entity<AppRole>().ToTable("roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("user_tokens");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        TouchEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        TouchEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void TouchEntities()
    {
        var entries = ChangeTracker.Entries<EntityBase>()
            .Where(e => e.State == EntityState.Modified || e.State == EntityState.Added);

        var utcNow = DateTimeOffset.UtcNow;
        foreach (var entry in entries)
        {
            entry.Entity.UpdatedAt = utcNow;
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = utcNow;
            }
        }
    }
}
