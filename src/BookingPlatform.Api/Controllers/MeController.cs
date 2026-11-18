using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Controllers;

public sealed record MeStaffRow(Guid StaffId, Guid BusinessId);

/// <summary>The Me schema: profile plus roles worked out from the data (specs/07-nfr.md).</summary>
public sealed record MeResponse(
    Guid Id, string Email, string Name, string TimeZone, bool IsAdmin, Guid? OwnedBusinessId, List<MeStaffRow> Staff);

/// <summary>
/// PATCH /v1/me body. Only fields that are sent change; a field sent as null, or no field at
/// all, is a validation error. The setters record which fields the JSON contained.
/// </summary>
public sealed class UpdateMeRequest : IValidatableObject
{
    private string? _name;
    private string? _timeZone;

    [PersonName]
    public string? Name { get => _name; set { _name = value; HasName = true; } }

    [IanaTimeZone]
    public string? TimeZone { get => _timeZone; set { _timeZone = value; HasTimeZone = true; } }

    [JsonIgnore] public bool HasName { get; private set; }
    [JsonIgnore] public bool HasTimeZone { get; private set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!HasName && !HasTimeZone) yield return new ValidationResult("Send at least one of name and timeZone.");
        if (HasName && Name is null) yield return new ValidationResult("Must not be null.", [nameof(Name)]);
        if (HasTimeZone && TimeZone is null) yield return new ValidationResult("Must not be null.", [nameof(TimeZone)]);
    }
}

/// <summary>The caller's own profile and roles; the client uses it to choose which pages to show.</summary>
[ApiController]
[Route("v1/me")]
public sealed class MeController(BookingDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<MeResponse> Get(CancellationToken ct) => await LoadAsync(User.GetUserId(), ct);

    [HttpPatch]
    public async Task<MeResponse> Update(UpdateMeRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw ApiException.Unauthenticated();

        if (request.HasName) user.Name = request.Name!.Trim();
        if (request.HasTimeZone) user.TimeZone = request.TimeZone!;
        await db.SaveChangesAsync(ct);

        return await LoadAsync(userId, ct);
    }

    /// <summary>A valid token for a user that does not exist is treated as no valid token (401).</summary>
    private async Task<MeResponse> LoadAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Email, u.Name, u.TimeZone }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.Unauthenticated();

        var isAdmin = await db.PlatformAdmins.AnyAsync(a => a.UserId == userId, ct);
        var ownedBusinessId = await db.Businesses.Where(b => b.OwnerUserId == userId)
            .Select(b => (Guid?)b.Id).SingleOrDefaultAsync(ct);
        var staff = await db.Staff.Where(s => s.UserId == userId && s.RemovedAt == null)
            .OrderBy(s => s.CreatedAt).Select(s => new MeStaffRow(s.Id, s.BusinessId)).ToListAsync(ct);

        return new MeResponse(user.Id, user.Email, user.Name, user.TimeZone, isAdmin, ownedBusinessId, staff);
    }
}
