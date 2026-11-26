using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BookingPlatform.Api.Data;

/// <summary>
/// EF Core context for the schema in specs/05-data-model.md (ADR 0006).
/// The schema changes only through migrations. Constraints that guard business
/// rules are declared here so the migration creates them in MySQL.
/// </summary>
public class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    // Case-insensitive but accent-sensitive, so "Ann@x" and "ann@x" collide but "josé@x" and "jose@x" do not.
    private const string EmailCollation = "utf8mb4_0900_as_ci";

    public DbSet<User> Users => Set<User>();
    public DbSet<UserInvite> UserInvites => Set<UserInvite>();
    public DbSet<PasswordReset> PasswordResets => Set<PasswordReset>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<StaffService> StaffServices => Set<StaffService>();
    public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>();
    public DbSet<AvailabilityException> AvailabilityExceptions => Set<AvailabilityException>();
    public DbSet<TimeOff> TimeOff => Set<TimeOff>();
    public DbSet<TimeZoneChange> TimeZoneChanges => Set<TimeZoneChange>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingHistory> BookingHistory => Set<BookingHistory>();
    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>
    /// Loads the user with <c>SELECT ... FOR UPDATE</c>, inside the caller's transaction, so
    /// operations on one user's account run one after another: login, accepting or resending an
    /// invite, creating an owner (T7), password recovery and change (T7a). Call it before the
    /// user is tracked by this context: a tracked instance would be returned with its old
    /// values, not the ones read under the lock. Lock order: the user row first, then that
    /// user's invites, recovery links or refresh tokens.
    /// </summary>
    public Task<User?> LockUserAsync(Guid userId, CancellationToken ct = default) =>
        Users.FromSql($"SELECT * FROM users WHERE id = {userId} FOR UPDATE").SingleOrDefaultAsync(ct);

    /// <summary>
    /// Takes the user row in share mode (<c>FOR SHARE</c>): a refresh holds it, so it never
    /// interleaves with a password change or reset (which take <see cref="LockUserAsync"/>),
    /// while one user's devices can still refresh at the same time (T7a). Returns false if the
    /// user does not exist.
    /// </summary>
    public Task<bool> LockUserSharedAsync(Guid userId, CancellationToken ct = default) =>
        Users.FromSql($"SELECT * FROM users WHERE id = {userId} FOR SHARE").Select(u => u.Id).AnyAsync(ct);

    /// <summary>
    /// Locks user rows before any staff lock (ADR 0002 "Implicit locks on user rows count"): each user once,
    /// FOR UPDATE if any entry asks for it, else FOR SHARE, in one fixed order (by id) so two such transactions
    /// can't deadlock. Every operation that locks more than one user row goes through here. Returns the first
    /// user that does not exist, or null.
    /// </summary>
    public async Task<Guid?> LockUsersAsync(IEnumerable<(Guid UserId, bool Exclusive)> users, CancellationToken ct = default)
    {
        foreach (var (userId, exclusive) in users.GroupBy(u => u.UserId).Select(g => (g.Key, g.Any(u => u.Exclusive))).OrderBy(u => u.Key))
        {
            var exists = exclusive ? await LockUserAsync(userId, ct) is not null : await LockUserSharedAsync(userId, ct);
            if (!exists) return userId;
        }
        return null;
    }

    /// <summary>
    /// The ADR 0002 lock: <c>SELECT ... FROM staff ... FOR UPDATE</c> inside the caller's transaction, so
    /// changes to one staff member's schedule run one after another. Take any user row locks first
    /// (ADR 0002 amendment). Returns the row as read under the lock (not tracked), or null if it does not exist.
    /// </summary>
    public Task<Staff?> LockStaffAsync(Guid staffId, CancellationToken ct = default) =>
        Staff.FromSql($"SELECT * FROM staff WHERE id = {staffId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // R2: every timestamp is stored and read as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        // Local dates and times of day (R8) are DATE and TIME columns. Oracle's MySQL provider writes
        // DateOnly and TimeOnly but reads them back as DateTime and TimeSpan, so map through those
        // types both ways (T11). The column types stay as InitialSchema created them.
        configurationBuilder.Properties<TimeOnly>().HaveConversion<TimeOnlyToTimeSpanConverter>().HaveColumnType("time");
        configurationBuilder.Properties<DateOnly>().HaveConversion<DateOnlyToDateTimeConverter>().HaveColumnType("date");
    }

    private sealed class TimeOnlyToTimeSpanConverter() : ValueConverter<TimeOnly, TimeSpan>(
        t => t.ToTimeSpan(), s => TimeOnly.FromTimeSpan(s));

    private sealed class DateOnlyToDateTimeConverter() : ValueConverter<DateOnly, DateTime>(
        d => d.ToDateTime(TimeOnly.MinValue), dt => DateOnly.FromDateTime(dt));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.Email).HasMaxLength(254).UseCollation(EmailCollation);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(255);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.TimeZone).HasMaxLength(64);
        });

        modelBuilder.Entity<UserInvite>(e =>
        {
            e.ToTable("user_invites");
            e.Property(x => x.TokenHash).HasMaxLength(128);
            e.HasIndex(x => x.TokenHash).IsUnique(); // several NULLs are allowed in a MySQL unique index
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PasswordReset>(e =>
        {
            e.ToTable("password_resets"); // T7a
            e.Property(x => x.TokenHash).HasMaxLength(128); // as user_invites
            e.HasIndex(x => x.TokenHash).IsUnique(); // several NULLs are allowed in a MySQL unique index
            e.HasIndex(x => new { x.UserId, x.CreatedAt }); // newest row per user, the cooldown check
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens"); // ADR 0010
            e.Property(x => x.TokenHash).HasMaxLength(64); // SHA-256, hex
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlatformAdmin>(e =>
        {
            e.ToTable("platform_admins");
            e.HasKey(x => x.UserId);
            e.HasOne<User>().WithOne().HasForeignKey<PlatformAdmin>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            // References users, not platform_admins, so revoking the granter's role is not blocked.
            e.HasOne<User>().WithMany().HasForeignKey(x => x.GrantedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Business>(e =>
        {
            e.ToTable("businesses", t =>
            {
                t.HasCheckConstraint("ck_businesses_min_notice",
                    $"min_notice_minutes BETWEEN 0 AND {BusinessLimits.MaxMinNoticeMinutes}");               // R3
                t.HasCheckConstraint("ck_businesses_cancellation_window",
                    $"cancellation_window_hours BETWEEN 0 AND {BusinessLimits.MaxCancellationWindowHours}"); // R4
                t.HasCheckConstraint("ck_businesses_max_advance",
                    $"max_advance_days BETWEEN 1 AND {BusinessLimits.MaxMaxAdvanceDays}");                   // R8
                t.HasCheckConstraint("ck_businesses_slot_step",
                    $"slot_step_minutes BETWEEN 1 AND {BusinessLimits.MaxSlotStepMinutes}");                 // R8
            });
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.MinNoticeMinutes).HasDefaultValue(BusinessDefaults.MinNoticeMinutes);
            e.Property(x => x.CancellationWindowHours).HasDefaultValue(BusinessDefaults.CancellationWindowHours);
            e.Property(x => x.MaxAdvanceDays).HasDefaultValue(BusinessDefaults.MaxAdvanceDays);
            e.Property(x => x.SlotStepMinutes).HasDefaultValue(BusinessDefaults.SlotStepMinutes);
            // Exactly one owner per business, at most one business per owner.
            e.HasOne<User>().WithOne().HasForeignKey<Business>(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.OwnerUserId).IsUnique();
        });

        modelBuilder.Entity<Service>(e =>
        {
            e.ToTable("services", t =>
            {
                t.HasCheckConstraint("ck_services_duration",
                    $"duration_minutes BETWEEN 1 AND {ServiceLimits.MaxDurationMinutes}");
                t.HasCheckConstraint("ck_services_buffers",
                    $"buffer_before_minutes BETWEEN 0 AND {ServiceLimits.MaxBufferMinutes} " +
                    $"AND buffer_after_minutes BETWEEN 0 AND {ServiceLimits.MaxBufferMinutes}");
                t.HasCheckConstraint("ck_services_price", "price >= 0");
            });
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Price).HasPrecision(10, 2);
            e.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BusinessId, x.Name, x.Id }); // the service list's order and keyset (T9)
        });

        modelBuilder.Entity<Staff>(e =>
        {
            e.ToTable("staff");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
            // One row per user and business; a user may be staff at several businesses.
            e.HasIndex(x => new { x.UserId, x.BusinessId }).IsUnique();
        });

        modelBuilder.Entity<StaffService>(e =>
        {
            e.ToTable("staff_services");
            e.HasKey(x => new { x.StaffId, x.ServiceId });
            e.HasOne<Staff>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AvailabilityRule>(e =>
        {
            e.ToTable("availability_rules", t =>
                t.HasCheckConstraint("ck_availability_rules_range", "end_local > start_local")); // R8
            e.EnumColumn(x => x.DayOfWeek, [
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
            ]);
            e.HasOne<Staff>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.StaffId, x.DayOfWeek });
        });

        modelBuilder.Entity<AvailabilityException>(e =>
        {
            // R8: a row is a range (both times, end after start) or a whole-day-unavailable marker
            // (both null). "No null row alongside ranges" and "no overlapping ranges" span rows and
            // are checked by the API (T11).
            e.ToTable("availability_exceptions", t => t.HasCheckConstraint("ck_availability_exceptions_range",
                "(start_local IS NULL AND end_local IS NULL) OR " +
                "(start_local IS NOT NULL AND end_local IS NOT NULL AND end_local > start_local)"));
            e.HasOne<Staff>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.StaffId, x.Date });
        });

        modelBuilder.Entity<TimeOff>(e =>
        {
            e.ToTable("time_off", t => t.HasCheckConstraint("ck_time_off_range", "end_utc > start_utc"));
            e.HasOne<Staff>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.StaffId, x.StartUtc });
        });

        modelBuilder.Entity<TimeZoneChange>(e =>
        {
            e.ToTable("time_zone_changes");
            e.Property(x => x.OldTimeZone).HasMaxLength(64);
            e.Property(x => x.NewTimeZone).HasMaxLength(64);
            e.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ChangedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BusinessId, x.ChangedAt });
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.Property(x => x.EventType).HasMaxLength(100);
            e.Property(x => x.MessageGroupId).HasMaxLength(128);
            e.Property(x => x.Payload).HasColumnType("json");
            // AUTO_INCREMENT is added by hand in the OutboxRelay migration (the provider only generates it for
            // keys). EF never writes seq, so the database always assigns it.
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.Property(x => x.Seq).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            e.Property(x => x.Seq).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
            e.HasIndex(x => x.Seq).IsUnique();
            e.Property(x => x.LastError).HasMaxLength(OutboxMessageLimits.LastErrorLength);
            // The relay's scans (T21): unpublished rows in seq order, and "an earlier unpublished row in this group".
            e.HasIndex(x => new { x.PublishedAt, x.Seq });
            e.HasIndex(x => new { x.MessageGroupId, x.PublishedAt, x.Seq });
        });

        modelBuilder.Entity<Booking>(e =>
        {
            e.ToTable("bookings", t =>
            {
                // R1: slots are half-open and non-empty.
                t.HasCheckConstraint("ck_bookings_range", "end_utc > start_utc");
                // R1, ADR 0004: buffers are never negative, so the blocked interval contains the booking.
                t.HasCheckConstraint("ck_bookings_blocked_range",
                    "blocked_start_utc <= start_utc AND blocked_end_utc >= end_utc");
                // Cancellation columns are null while Confirmed and all required once Cancelled.
                t.HasCheckConstraint("ck_bookings_cancellation",
                    "(status = 'Confirmed' AND cancelled_at IS NULL AND cancelled_by IS NULL AND cancellation_reason IS NULL) OR " +
                    "(status = 'Cancelled' AND cancelled_at IS NOT NULL AND cancelled_by IS NOT NULL AND cancellation_reason IS NOT NULL)");
            });
            e.EnumColumn(x => x.Status, Enum.GetValues<BookingStatus>());
            e.EnumColumn(x => x.CancellationReason, Enum.GetValues<CancellationReason>().Cast<CancellationReason?>());
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Staff>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CancelledBy).OnDelete(DeleteBehavior.Restrict);
            // R1, R7: overlap check for a staff member's active bookings, under the ADR 0002 lock.
            e.HasIndex(x => new { x.StaffId, x.Status, x.BlockedStartUtc });
            e.HasIndex(x => new { x.CustomerId, x.StartUtc });
            e.HasIndex(x => new { x.StaffId, x.StartUtc }); // staff and business booking lists by (start, id) (T16)
        });

        modelBuilder.Entity<BookingHistory>(e =>
        {
            // ADR 0003: a reschedule records the slot it moved from; a creation has none.
            e.ToTable("booking_history", t => t.HasCheckConstraint("ck_booking_history_previous_slot",
                "(type = 'Created' AND previous_staff_id IS NULL AND previous_start_utc IS NULL AND previous_end_utc IS NULL) OR " +
                "(type = 'Rescheduled' AND previous_staff_id IS NOT NULL AND previous_start_utc IS NOT NULL AND previous_end_utc IS NOT NULL)"));
            e.EnumColumn(x => x.Type, Enum.GetValues<BookingHistoryType>());
            e.EnumColumn(x => x.ActorRole, Enum.GetValues<ActorRole>());
            e.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Staff>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Staff>().WithMany().HasForeignKey(x => x.PreviousStaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BookingId, x.At });
        });

        modelBuilder.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_keys"); // T29
            e.HasKey(x => new { x.UserId, x.IdempotencyKey }); // keys belong to the caller
            // Case-sensitive ASCII. The provider drops the collation from the DDL; the migration sets it by hand.
            e.Property(x => x.IdempotencyKey).HasMaxLength(64).UseCollation("ascii_bin");
            e.Property(x => x.RequestHash).HasMaxLength(64); // SHA-256, hex
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.CreatedAt); // T31: the cleanup deletes by created_at
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.EnumColumn(x => x.Kind, Enum.GetValues<NotificationKind>());
            e.EnumColumn(x => x.Status, Enum.GetValues<NotificationStatus>());
            e.Property(x => x.SesMessageId).HasMaxLength(255);
            e.Property(x => x.Error).HasColumnType("text");
            // event_id has no foreign key: events also come from EventBridge (reminders), not only the outbox.
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
            // Each event emails each recipient at most once.
            e.HasIndex(x => new { x.EventId, x.RecipientUserId }).IsUnique();
        });

        UseSnakeCaseColumns(modelBuilder);
    }

    private static void UseSnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && !char.IsUpper(name[i - 1])) builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => EnsureUtc(v),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
        private static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc
            ? value
            : throw new InvalidOperationException($"R2: timestamps must be UTC, got DateTimeKind.{value.Kind}.");
    }
}

internal static class EnumColumnExtensions
{
    /// <summary>Stores an enum as its name in a MySQL ENUM column listing exactly these values.</summary>
    public static void EnumColumn<TEntity, TEnum>(
        this EntityTypeBuilder<TEntity> entity,
        System.Linq.Expressions.Expression<Func<TEntity, TEnum>> property,
        IEnumerable<TEnum> values)
        where TEntity : class
    {
        var enumType = Nullable.GetUnderlyingType(typeof(TEnum)) ?? typeof(TEnum);
        var names = values.Select(v => $"'{Enum.GetName(enumType, v!)}'");
        entity.Property(property)
            .HasConversion<string>()
            .HasColumnType($"enum({string.Join(",", names)})");
    }
}
