using BookingPlatform.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Authorization;

/// <summary>
/// The caller's roles in one business (specs/07-nfr.md "Authorization").
/// A removed staff row does not count, so <see cref="StaffId"/> is null for a removed staff member.
/// </summary>
public sealed record BusinessRoles(Guid BusinessId, bool IsOwner, Guid? StaffId)
{
    public bool IsStaff => StaffId is not null;
}

/// <summary>The caller's access to one staff row (availability, time off).</summary>
public sealed record StaffAccess(Guid StaffId, bool IsRemoved, bool IsSelf, BusinessRoles Business)
{
    /// <summary>Staff manage their own row; owners manage any row in their business.</summary>
    public bool CanManage => IsSelf || Business.IsOwner;
}

/// <summary>The caller's access to one booking.</summary>
public sealed record BookingAccess(Guid BookingId, bool IsCustomer, bool IsBookingStaff, BusinessRoles Business)
{
    /// <summary>Customers see their own bookings, staff the bookings they work, owners any booking in the business.</summary>
    public bool CanView => IsCustomer || IsBookingStaff || Business.IsOwner;
}

/// <summary>
/// Works out roles from the data on every request, so a change (e.g. a staff member
/// being removed) takes effect at once (specs/07-nfr.md). Every check is scoped to one
/// business. Each method returns null when the resource does not exist; combine with
/// <see cref="Access"/> for the 404-versus-403 rule.
/// </summary>
public sealed class AccessResolver(BookingDbContext db)
{
    /// <summary>Admin is platform-wide and grants nothing inside any business.</summary>
    public Task<bool> IsAdminAsync(Guid userId, CancellationToken ct = default) =>
        db.PlatformAdmins.AnyAsync(a => a.UserId == userId, ct);

    // Each method is one query: CallerStaffId (the caller's active staff row in the business)
    // is a subquery in the same SELECT, not a second round trip. It is written inline because
    // EF Core cannot translate a call to a helper method inside a projection.

    public async Task<BusinessRoles?> ForBusinessAsync(Guid userId, Guid businessId, CancellationToken ct = default)
    {
        var row = await db.Businesses
            .Where(b => b.Id == businessId)
            .Select(b => new { b.OwnerUserId, CallerStaffId = db.Staff.Where(cs => cs.BusinessId == b.Id && cs.UserId == userId && cs.RemovedAt == null).Select(cs => (Guid?)cs.Id).FirstOrDefault() })
            .SingleOrDefaultAsync(ct);
        return row is null ? null : new BusinessRoles(businessId, row.OwnerUserId == userId, row.CallerStaffId);
    }

    public async Task<StaffAccess?> ForStaffAsync(Guid userId, Guid staffId, CancellationToken ct = default)
    {
        var row = await (
            from s in db.Staff
            join b in db.Businesses on s.BusinessId equals b.Id
            where s.Id == staffId
            select new { s.UserId, s.RemovedAt, s.BusinessId, b.OwnerUserId, CallerStaffId = db.Staff.Where(cs => cs.BusinessId == b.Id && cs.UserId == userId && cs.RemovedAt == null).Select(cs => (Guid?)cs.Id).FirstOrDefault() })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;

        var removed = row.RemovedAt is not null;
        return new StaffAccess(staffId, removed, IsSelf: row.UserId == userId && !removed,
            new BusinessRoles(row.BusinessId, row.OwnerUserId == userId, row.CallerStaffId));
    }

    public async Task<BookingAccess?> ForBookingAsync(Guid userId, Guid bookingId, CancellationToken ct = default)
    {
        var row = await (
            from bk in db.Bookings
            join s in db.Staff on bk.StaffId equals s.Id
            join b in db.Businesses on s.BusinessId equals b.Id
            where bk.Id == bookingId
            select new
            {
                bk.CustomerId, StaffUserId = s.UserId, s.RemovedAt, s.BusinessId, b.OwnerUserId,
                CallerStaffId = db.Staff.Where(cs => cs.BusinessId == b.Id && cs.UserId == userId && cs.RemovedAt == null).Select(cs => (Guid?)cs.Id).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;

        return new BookingAccess(bookingId,
            IsCustomer: row.CustomerId == userId,
            IsBookingStaff: row.StaffUserId == userId && row.RemovedAt is null,
            new BusinessRoles(row.BusinessId, row.OwnerUserId == userId, row.CallerStaffId));
    }
}
