using Backend.Models;
using Backend.Models.Enums;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace Backend.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<UserWhitelist> UserWhitelists => Set<UserWhitelist>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Ride> Rides => Set<Ride>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc)
        );

        var utcNullableConverter = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue
                ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime())
                : v,
            v => v.HasValue
                ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)
                : v
        );

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                    property.SetValueConverter(utcConverter);
                else if (property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(utcNullableConverter);
            }
        }

        modelBuilder.Entity<Ride>()
            .HasOne(r => r.Driver)
            .WithMany(u => u.Rides)
            .HasForeignKey(r => r.DriverId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<UserWhitelist>()
            .HasOne(w => w.Settings)
            .WithOne(s => s.UserWhitelist)
            .HasForeignKey<UserSettings>(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Ride>()
            .Property(r => r.Rating)
            .HasPrecision(5, 2);
        modelBuilder.Entity<Ride>()
            .Property(r => r.DistanceKm)
            .HasPrecision(12, 2);
        modelBuilder.Entity<Ride>()
            .Property(r => r.Price)
            .HasPrecision(12, 2);
        modelBuilder.Entity<Ride>()
            .Property(r => r.DriverProfit)
            .HasPrecision(12, 2);

        modelBuilder.Entity<SystemSettings>(e =>
        {
            e.Property(s => s.BaseFare).HasPrecision(12, 2);
            e.Property(s => s.CostPerKm).HasPrecision(12, 2);
            e.Property(s => s.PlatformFixedFee).HasPrecision(12, 2);
            e.Property(s => s.PlatformFeePercentage).HasPrecision(6, 4);
            e.HasData(new SystemSettings
            {
                Id = 1,
                BaseFare = 50m,
                CostPerKm = 15m,
                PlatformFixedFee = 10m,
                PlatformFeePercentage = 0.10m
            });
        });

        var routeProperty = modelBuilder.Entity<Ride>()
            .Property(r => r.Route);

        routeProperty.Metadata.SetValueComparer(new ValueComparer<List<RideRoutePoint>>(
            (left, right) =>
                JsonSerializer.Serialize(left ?? new List<RideRoutePoint>(), (JsonSerializerOptions?)null) ==
                JsonSerializer.Serialize(right ?? new List<RideRoutePoint>(), (JsonSerializerOptions?)null),
            value => JsonSerializer.Serialize(value ?? new List<RideRoutePoint>(), (JsonSerializerOptions?)null).GetHashCode(),
            value => value == null
                ? new List<RideRoutePoint>()
                : value.Select(point => new RideRoutePoint { Lat = point.Lat, Lng = point.Lng }).ToList()));

        routeProperty
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<RideRoutePoint>()
                    : JsonSerializer.Deserialize<List<RideRoutePoint>>(v, (JsonSerializerOptions?)null) ?? new List<RideRoutePoint>())
            .HasColumnType("longtext");
        modelBuilder.Entity<UserWhitelist>().HasData(new UserWhitelist
        {
            Id = 1,
            PhoneNumber = "+380967515075",
            Role = UserRole.SuperAdmin,
            IsActive = true,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
    }
}
