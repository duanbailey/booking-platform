using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Controllers;

/// <summary>
/// PATCH /v1/businesses/{businessId}/settings body. Only fields that are sent change; a field
/// sent as null, or no field at all, is a validation error (as PATCH /v1/me). Each setting is
/// validated on its own, so a combination that offers no slots is allowed (decided 2026-11-19).
/// The setters record which fields the JSON contained.
/// </summary>
public sealed class UpdateBusinessSettingsRequest : IValidatableObject
{
    /// <summary>{1} and {2} are the range; the field is named by its key, not in the message.</summary>
    private const string RangeMessage = "Must be between {1} and {2}.";

    private string? _name;
    private int? _minNoticeMinutes;
    private int? _cancellationWindowHours;
    private int? _maxAdvanceDays;
    private int? _slotStepMinutes;

    [BusinessName]
    public string? Name { get => _name; set { _name = value; HasName = true; } }

    [Range(0, BusinessLimits.MaxMinNoticeMinutes, ErrorMessage = RangeMessage)] // R3
    public int? MinNoticeMinutes { get => _minNoticeMinutes; set { _minNoticeMinutes = value; HasMinNoticeMinutes = true; } }

    [Range(0, BusinessLimits.MaxCancellationWindowHours, ErrorMessage = RangeMessage)] // R4
    public int? CancellationWindowHours
    {
        get => _cancellationWindowHours;
        set { _cancellationWindowHours = value; HasCancellationWindowHours = true; }
    }

    [Range(1, BusinessLimits.MaxMaxAdvanceDays, ErrorMessage = RangeMessage)] // R8
    public int? MaxAdvanceDays { get => _maxAdvanceDays; set { _maxAdvanceDays = value; HasMaxAdvanceDays = true; } }

    [Range(1, BusinessLimits.MaxSlotStepMinutes, ErrorMessage = RangeMessage)] // R8
    public int? SlotStepMinutes { get => _slotStepMinutes; set { _slotStepMinutes = value; HasSlotStepMinutes = true; } }

    [JsonIgnore] public bool HasName { get; private set; }
    [JsonIgnore] public bool HasMinNoticeMinutes { get; private set; }
    [JsonIgnore] public bool HasCancellationWindowHours { get; private set; }
    [JsonIgnore] public bool HasMaxAdvanceDays { get; private set; }
    [JsonIgnore] public bool HasSlotStepMinutes { get; private set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!HasName && !HasMinNoticeMinutes && !HasCancellationWindowHours && !HasMaxAdvanceDays && !HasSlotStepMinutes)
        {
            yield return new ValidationResult(
                "Send at least one of name, minNoticeMinutes, cancellationWindowHours, maxAdvanceDays and slotStepMinutes.");
        }
        if (HasName && Name is null) yield return Null(nameof(Name));
        if (HasMinNoticeMinutes && MinNoticeMinutes is null) yield return Null(nameof(MinNoticeMinutes));
        if (HasCancellationWindowHours && CancellationWindowHours is null) yield return Null(nameof(CancellationWindowHours));
        if (HasMaxAdvanceDays && MaxAdvanceDays is null) yield return Null(nameof(MaxAdvanceDays));
        if (HasSlotStepMinutes && SlotStepMinutes is null) yield return Null(nameof(SlotStepMinutes));
    }

    private static ValidationResult Null(string member) => new("Must not be null.", [member]);
}

/// <summary>A business and its settings (story "Owner sets up their business").</summary>
[ApiController]
[Route("v1/businesses")]
public sealed class BusinessesController(BookingDbContext db) : ControllerBase
{
    /// <summary>Any signed-in user may view a business and its public settings.</summary>
    [HttpGet("{businessId:guid}")]
    public async Task<BusinessResponse> Get(Guid businessId, CancellationToken ct) =>
        BusinessResponse.From(Access.Found(await LoadAsync(businessId, ct)));

    /// <summary>
    /// Owner only: 404 if the business does not exist, otherwise 403 for anyone else, since any
    /// signed-in user can view it (specs/07-nfr.md). Applies from now on; existing bookings are
    /// never rewritten (R3, R4, R8). Only the sent columns are written, so concurrent changes to
    /// different fields don't overwrite each other. Takes no staff lock (decided 2026-11-19).
    /// The response is read again after saving, so it shows fields another change committed
    /// meanwhile, not this request's first read.
    /// </summary>
    [HttpPatch("{businessId:guid}/settings")]
    public async Task<BusinessResponse> UpdateSettings(Guid businessId, UpdateBusinessSettingsRequest request, CancellationToken ct)
    {
        var business = Access.Found(await db.Businesses.SingleOrDefaultAsync(b => b.Id == businessId, ct));
        Access.Require(canView: true, canAct: business.OwnerUserId == User.GetUserId());

        if (request.HasName) business.Name = request.Name!.Trim();
        if (request.HasMinNoticeMinutes) business.MinNoticeMinutes = request.MinNoticeMinutes!.Value;
        if (request.HasCancellationWindowHours) business.CancellationWindowHours = request.CancellationWindowHours!.Value;
        if (request.HasMaxAdvanceDays) business.MaxAdvanceDays = request.MaxAdvanceDays!.Value;
        if (request.HasSlotStepMinutes) business.SlotStepMinutes = request.SlotStepMinutes!.Value;
        await db.SaveChangesAsync(ct);

        // The business is not deleted, so it is still there.
        return BusinessResponse.From((await LoadAsync(businessId, ct))!);
    }

    /// <summary>A fresh, untracked read: never the instance this context already tracks.</summary>
    private Task<Business?> LoadAsync(Guid businessId, CancellationToken ct) =>
        db.Businesses.AsNoTracking().SingleOrDefaultAsync(b => b.Id == businessId, ct);
}
