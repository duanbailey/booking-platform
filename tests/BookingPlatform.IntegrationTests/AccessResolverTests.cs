using System.Data.Common;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Data;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T3: roles are worked out from the data on every request, scoped to one business
/// (specs/07-nfr.md "Authorization", specs/05-data-model.md roles).
/// </summary>
public class AccessResolverTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private readonly Sql _sql = new(database.ConnectionString);

    private AccessResolver Resolver() => new(database.CreateContext());

    // ------------------------------------------------------------ Business roles

    [Fact]
    public async Task Owner_is_owner_of_their_business()
    {
        var owner = await _sql.InsertUserAsync();
        var business = await _sql.InsertBusinessAsync(owner);

        var roles = await Resolver().ForBusinessAsync(owner, business);

        Assert.NotNull(roles);
        Assert.True(roles.IsOwner);
        Assert.False(roles.IsStaff);
        Assert.Null(roles.StaffId);
    }

    [Fact]
    public async Task Owner_who_takes_bookings_is_also_staff()
    {
        var owner = await _sql.InsertUserAsync();
        var business = await _sql.InsertBusinessAsync(owner);
        var staffId = await _sql.InsertStaffAsync(business, owner);

        var roles = await Resolver().ForBusinessAsync(owner, business);

        Assert.True(roles!.IsOwner);
        Assert.True(roles.IsStaff);
        Assert.Equal(staffId, roles.StaffId);
    }

    [Fact]
    public async Task Staff_member_is_staff_of_their_business_only()
    {
        var business = await _sql.InsertBusinessAsync();
        var user = await _sql.InsertUserAsync();
        var staffId = await _sql.InsertStaffAsync(business, user);

        var roles = await Resolver().ForBusinessAsync(user, business);

        Assert.False(roles!.IsOwner);
        Assert.True(roles.IsStaff);
        Assert.Equal(staffId, roles.StaffId);
    }

    [Fact]
    public async Task Removed_staff_member_has_no_role_in_the_business()
    {
        var business = await _sql.InsertBusinessAsync();
        var user = await _sql.InsertUserAsync();
        var staffId = await _sql.InsertStaffAsync(business, user);
        await _sql.RemoveStaffAsync(staffId);

        var roles = await Resolver().ForBusinessAsync(user, business);

        Assert.False(roles!.IsOwner);
        Assert.False(roles.IsStaff);
        Assert.Null(roles.StaffId);
    }

    [Fact]
    public async Task Owner_and_staff_of_one_business_have_no_role_in_another()
    {
        var owner = await _sql.InsertUserAsync();
        var businessA = await _sql.InsertBusinessAsync(owner);
        var staffUser = await _sql.InsertUserAsync();
        await _sql.InsertStaffAsync(businessA, staffUser);
        var businessB = await _sql.InsertBusinessAsync();

        foreach (var user in new[] { owner, staffUser })
        {
            var roles = await Resolver().ForBusinessAsync(user, businessB);
            Assert.False(roles!.IsOwner);
            Assert.False(roles.IsStaff);
        }
    }

    [Fact]
    public async Task Staff_at_two_businesses_gets_the_staff_row_of_the_business_asked_about()
    {
        var user = await _sql.InsertUserAsync();
        var businessA = await _sql.InsertBusinessAsync();
        var businessB = await _sql.InsertBusinessAsync();
        var staffA = await _sql.InsertStaffAsync(businessA, user);
        var staffB = await _sql.InsertStaffAsync(businessB, user);

        Assert.Equal(staffA, (await Resolver().ForBusinessAsync(user, businessA))!.StaffId);
        Assert.Equal(staffB, (await Resolver().ForBusinessAsync(user, businessB))!.StaffId);
    }

    [Fact]
    public async Task Unknown_business_resolves_to_null()
    {
        Assert.Null(await Resolver().ForBusinessAsync(await _sql.InsertUserAsync(), Guid.NewGuid()));
    }

    [Fact]
    public async Task Admin_is_admin_but_gets_no_role_in_any_business()
    {
        var admin = await _sql.InsertUserAsync();
        await _sql.InsertAdminAsync(admin);
        var business = await _sql.InsertBusinessAsync();

        Assert.True(await Resolver().IsAdminAsync(admin));
        var roles = await Resolver().ForBusinessAsync(admin, business);
        Assert.False(roles!.IsOwner);
        Assert.False(roles.IsStaff);
    }

    [Fact]
    public async Task User_without_a_platform_admins_row_is_not_admin()
    {
        Assert.False(await Resolver().IsAdminAsync(await _sql.InsertUserAsync()));
    }

    // ------------------------------------------------------------ Staff resources

    [Fact]
    public async Task Staff_member_manages_their_own_staff_row_only()
    {
        var business = await _sql.InsertBusinessAsync();
        var user = await _sql.InsertUserAsync();
        var own = await _sql.InsertStaffAsync(business, user);
        var colleague = await _sql.InsertStaffAsync(business);

        var self = await Resolver().ForStaffAsync(user, own);
        var other = await Resolver().ForStaffAsync(user, colleague);

        Assert.True(self!.IsSelf);
        Assert.True(self.CanManage);
        Assert.False(other!.IsSelf);
        Assert.False(other.CanManage);
        Assert.True(other.Business.IsStaff); // still staff in the business
    }

    [Fact]
    public async Task Owner_manages_any_staff_row_in_their_business()
    {
        var owner = await _sql.InsertUserAsync();
        var business = await _sql.InsertBusinessAsync(owner);
        var staff = await _sql.InsertStaffAsync(business);

        var access = await Resolver().ForStaffAsync(owner, staff);

        Assert.False(access!.IsSelf);
        Assert.True(access.CanManage);
    }

    [Fact]
    public async Task Removed_staff_member_cannot_manage_their_old_staff_row()
    {
        var business = await _sql.InsertBusinessAsync();
        var user = await _sql.InsertUserAsync();
        var staff = await _sql.InsertStaffAsync(business, user);
        await _sql.RemoveStaffAsync(staff);

        var access = await Resolver().ForStaffAsync(user, staff);

        Assert.False(access!.IsSelf);
        Assert.False(access.CanManage);
        Assert.True(access.IsRemoved);
    }

    [Fact]
    public async Task Owner_of_another_business_cannot_manage_the_staff_row()
    {
        var otherOwner = await _sql.InsertUserAsync();
        await _sql.InsertBusinessAsync(otherOwner);
        var staff = await _sql.InsertStaffAsync(await _sql.InsertBusinessAsync());

        Assert.False((await Resolver().ForStaffAsync(otherOwner, staff))!.CanManage);
    }

    [Fact]
    public async Task Unknown_staff_row_resolves_to_null()
    {
        Assert.Null(await Resolver().ForStaffAsync(await _sql.InsertUserAsync(), Guid.NewGuid()));
    }

    // ------------------------------------------------------------ Bookings

    private async Task<(Sql.BookingFixture Fixture, Guid BookingId, Guid OwnerId, Guid StaffUserId)> BookingAsync()
    {
        var owner = await _sql.InsertUserAsync();
        var business = await _sql.InsertBusinessAsync(owner);
        var staffUser = await _sql.InsertUserAsync();
        var fixture = new Sql.BookingFixture(business, await _sql.InsertStaffAsync(business, staffUser),
            await _sql.InsertServiceAsync(business), await _sql.InsertUserAsync());
        var booking = await _sql.InsertBookingAsync(fixture);
        return (fixture, booking, owner, staffUser);
    }

    [Fact]
    public async Task Customer_views_their_own_booking()
    {
        var (f, booking, _, _) = await BookingAsync();

        var access = await Resolver().ForBookingAsync(f.CustomerId, booking);

        Assert.True(access!.IsCustomer);
        Assert.True(access.CanView);
    }

    [Fact]
    public async Task Staff_member_views_bookings_where_they_are_the_staff_member()
    {
        var (_, booking, _, staffUser) = await BookingAsync();

        var access = await Resolver().ForBookingAsync(staffUser, booking);

        Assert.True(access!.IsBookingStaff);
        Assert.True(access.CanView);
    }

    [Fact]
    public async Task Owner_views_any_booking_in_their_business()
    {
        var (_, booking, owner, _) = await BookingAsync();

        var access = await Resolver().ForBookingAsync(owner, booking);

        Assert.True(access!.Business.IsOwner);
        Assert.True(access.CanView);
    }

    [Fact]
    public async Task Other_staff_in_the_same_business_cannot_view_the_booking()
    {
        var (f, booking, _, _) = await BookingAsync();
        var colleague = await _sql.InsertUserAsync();
        await _sql.InsertStaffAsync(f.BusinessId, colleague);

        var access = await Resolver().ForBookingAsync(colleague, booking);

        Assert.True(access!.Business.IsStaff);
        Assert.False(access.CanView);
    }

    [Fact]
    public async Task Removed_staff_member_can_no_longer_view_their_bookings()
    {
        var (f, booking, _, staffUser) = await BookingAsync();
        await _sql.RemoveStaffAsync(f.StaffId);

        var access = await Resolver().ForBookingAsync(staffUser, booking);

        Assert.False(access!.IsBookingStaff);
        Assert.False(access.CanView);
    }

    [Fact]
    public async Task Strangers_owners_of_other_businesses_and_admins_cannot_view_the_booking()
    {
        var (_, booking, _, _) = await BookingAsync();
        var stranger = await _sql.InsertUserAsync();
        var otherOwner = await _sql.InsertUserAsync();
        await _sql.InsertBusinessAsync(otherOwner);
        var admin = await _sql.InsertUserAsync();
        await _sql.InsertAdminAsync(admin);

        foreach (var user in new[] { stranger, otherOwner, admin })
        {
            Assert.False((await Resolver().ForBookingAsync(user, booking))!.CanView);
        }
    }

    // ------------------------------------------------------------ Cost

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Each_lookup_is_a_single_query()
    {
        var (f, booking, owner, _) = await BookingAsync();
        var counter = new CommandCounter();
        await using var context = new BookingDbContext(new DbContextOptionsBuilder<BookingDbContext>()
            .UseMySQL(database.ConnectionString).AddInterceptors(counter).Options);
        var resolver = new AccessResolver(context);

        Assert.True((await resolver.ForBusinessAsync(owner, f.BusinessId))!.IsOwner);
        Assert.Equal(1, counter.Count);
        Assert.True((await resolver.ForStaffAsync(owner, f.StaffId))!.CanManage);
        Assert.Equal(2, counter.Count);
        Assert.True((await resolver.ForBookingAsync(owner, booking))!.CanView);
        Assert.Equal(3, counter.Count);
    }

    [Fact]
    public async Task Unknown_booking_resolves_to_null()
    {
        Assert.Null(await Resolver().ForBookingAsync(await _sql.InsertUserAsync(), Guid.NewGuid()));
    }
}
