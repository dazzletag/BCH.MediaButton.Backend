using MediaButtonBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistItem> PlaylistItems => Set<PlaylistItem>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<ResidentPlaylistSnapshot> ResidentPlaylists => Set<ResidentPlaylistSnapshot>();
    public DbSet<CarePlanVersion> CarePlanVersions => Set<CarePlanVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>(b =>
        {
            b.HasKey(d => d.DeviceId);
            b.Property(d => d.DeviceId).HasMaxLength(100);
            b.Property(d => d.DisplayName).HasMaxLength(200);
            b.Property(d => d.ConfigJson).HasMaxLength(4000);
        });

        modelBuilder.Entity<Playlist>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<MediaAsset>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Name).HasMaxLength(200);
            b.Property(m => m.BlobPath).IsRequired().HasMaxLength(500);
            b.Property(m => m.ContentType).HasMaxLength(200);
            b.Property(m => m.Type).HasConversion<string>().IsRequired();
        });

        modelBuilder.Entity<PlaylistItem>(b =>
        {
            b.HasKey(pi => pi.Id);
            b.HasOne(pi => pi.Playlist)
                .WithMany(p => p.Items)
                .HasForeignKey(pi => pi.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(pi => pi.Media)
                .WithMany()
                .HasForeignKey(pi => pi.MediaId)
                .OnDelete(DeleteBehavior.Restrict);

            b.Property(pi => pi.Order).IsRequired();
        });

        modelBuilder.Entity<ResidentPlaylistSnapshot>(b =>
        {
            b.HasKey(r => r.Resident);
            b.Property(r => r.Resident).HasMaxLength(200);
            b.Property(r => r.ManualUpdatedBy).HasMaxLength(200);
        });

        modelBuilder.Entity<CarePlanVersion>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.ResidentId).IsRequired().HasMaxLength(200);
            b.Property(c => c.Section).IsRequired().HasMaxLength(100);
            b.Property(c => c.Status).IsRequired().HasMaxLength(20);
            b.Property(c => c.CreatedById).HasMaxLength(200);
            b.Property(c => c.CreatedByName).HasMaxLength(200);
            b.HasIndex(c => new { c.CarePlanId, c.VersionNumber }).IsUnique();
            b.HasIndex(c => new { c.ResidentId, c.Section, c.IsCurrent });
        });
    }
}
