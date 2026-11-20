using BookingPlatform.Api.Controllers;
using BookingPlatform.Api.Data;

namespace BookingPlatform.Api.Bookings;

/// <summary>Queries shared by the booking endpoints.</summary>
public static class BookingQueries
{
    /// <summary>The Booking schema for each booking, keeping the query's order.</summary>
    public static IQueryable<BookingResponse> Responses(BookingDbContext db, IQueryable<Booking> bookings) =>
        from b in bookings
        join s in db.Staff on b.StaffId equals s.Id
        join biz in db.Businesses on s.BusinessId equals biz.Id
        join u in db.Users on b.CustomerId equals u.Id
        select new BookingResponse(b.Id, biz.Id, biz.TimeZone, b.ServiceId, b.StaffId, new BookingCustomer(u.Id, u.Name, u.Email),
            b.StartUtc, b.EndUtc, b.Status, b.CancelledAt, b.CancellationReason);
}
