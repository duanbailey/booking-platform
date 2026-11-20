using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Authorization;

/// <summary>
/// Access to one staff member's schedule data: weekly availability, date exceptions (T11) and
/// time off (T12). Decided 2026-11-19: the business's owner and its active staff may view; a
/// staff member manages their own, the owner anyone's, including a removed member's. Anyone else
/// gets 404; a colleague who may view but not manage gets 403 (specs/07-nfr.md).
/// <para>
/// A write calls <see cref="RequireAsync"/> (manage), then begins a transaction and calls
/// <see cref="LockAndRequireAsync"/>. The first check means a caller who may not write never takes
/// the staff lock (so they can't hold up writes or bookings); the second runs after the lock, so it
/// reads data current as of the lock (the lock is the transaction's first statement, and REPEATABLE
/// READ takes its snapshot at the first plain read after it).
/// </para>
/// </summary>
public sealed class StaffAccessGuard(BookingDbContext db, AccessResolver access)
{
    /// <exception cref="ApiException">NotFound unless the caller may view; Forbidden if they may view but not manage.</exception>
    public async Task RequireAsync(Guid callerId, Guid staffId, bool manage, CancellationToken ct = default)
    {
        var staff = await access.ForStaffAsync(callerId, staffId, ct) ?? throw ApiException.NotFound();
        Access.Require(canView: staff.Business.IsOwner || staff.Business.IsStaff, canAct: !manage || staff.CanManage);
    }

    /// <summary>
    /// Takes the staff row lock (ADR 0002) inside the caller's transaction, then checks that the
    /// caller may still manage this staff member.
    /// </summary>
    public async Task LockAndRequireAsync(Guid callerId, Guid staffId, CancellationToken ct = default)
    {
        if (await db.LockStaffAsync(staffId, ct) is null) throw ApiException.NotFound();
        await RequireAsync(callerId, staffId, manage: true, ct);
    }
}
