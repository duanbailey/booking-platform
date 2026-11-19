using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Paging;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Controllers;

/// <summary>The Service schema (lists): no staff.</summary>
public sealed record ServiceResponse(
    Guid Id, Guid BusinessId, string Name, string? Description, int DurationMinutes,
    int BufferBeforeMinutes, int BufferAfterMinutes, string Price)
{
    public static ServiceResponse From(Service s) => new(
        s.Id, s.BusinessId, s.Name, s.Description, s.DurationMinutes, s.BufferBeforeMinutes, s.BufferAfterMinutes,
        PriceAttribute.Format(s.Price));
}

/// <summary>The StaffSummary schema.</summary>
public sealed record StaffSummaryResponse(Guid StaffId, string Name);

/// <summary>The ServiceDetail schema: the service and its assigned staff who are not removed.</summary>
public sealed record ServiceDetailResponse(
    Guid Id, Guid BusinessId, string Name, string? Description, int DurationMinutes,
    int BufferBeforeMinutes, int BufferAfterMinutes, string Price, IReadOnlyList<StaffSummaryResponse> Staff)
{
    public static ServiceDetailResponse From(Service s, IReadOnlyList<StaffSummaryResponse> staff) => new(
        s.Id, s.BusinessId, s.Name, s.Description, s.DurationMinutes, s.BufferBeforeMinutes, s.BufferAfterMinutes,
        PriceAttribute.Format(s.Price), staff);
}

/// <summary>POST /v1/businesses/{businessId}/services body. Description is optional; blank means none.</summary>
public sealed record CreateServiceRequest(
    [Required, ServiceName] string Name,
    [ServiceDescription] string? Description,
    [Required, Range(1, ServiceLimits.MaxDurationMinutes, ErrorMessage = CreateServiceRequest.RangeMessage)] int? DurationMinutes,
    [Required, Range(0, ServiceLimits.MaxBufferMinutes, ErrorMessage = CreateServiceRequest.RangeMessage)] int? BufferBeforeMinutes,
    [Required, Range(0, ServiceLimits.MaxBufferMinutes, ErrorMessage = CreateServiceRequest.RangeMessage)] int? BufferAfterMinutes,
    [Required, Price] string Price)
{
    internal const string RangeMessage = "Must be between {1} and {2}.";
}

/// <summary>
/// PATCH /v1/services/{serviceId} body. Only fields that are sent change; null is allowed only
/// for description (which clears it), and no field at all is a validation error. The setters
/// record which fields the JSON contained.
/// </summary>
public sealed class UpdateServiceRequest : IValidatableObject
{
    private string? _name;
    private string? _description;
    private int? _durationMinutes;
    private int? _bufferBeforeMinutes;
    private int? _bufferAfterMinutes;
    private string? _price;

    [ServiceName]
    public string? Name { get => _name; set { _name = value; HasName = true; } }

    [ServiceDescription]
    public string? Description { get => _description; set { _description = value; HasDescription = true; } }

    [Range(1, ServiceLimits.MaxDurationMinutes, ErrorMessage = CreateServiceRequest.RangeMessage)]
    public int? DurationMinutes { get => _durationMinutes; set { _durationMinutes = value; HasDurationMinutes = true; } }

    [Range(0, ServiceLimits.MaxBufferMinutes, ErrorMessage = CreateServiceRequest.RangeMessage)]
    public int? BufferBeforeMinutes { get => _bufferBeforeMinutes; set { _bufferBeforeMinutes = value; HasBufferBeforeMinutes = true; } }

    [Range(0, ServiceLimits.MaxBufferMinutes, ErrorMessage = CreateServiceRequest.RangeMessage)]
    public int? BufferAfterMinutes { get => _bufferAfterMinutes; set { _bufferAfterMinutes = value; HasBufferAfterMinutes = true; } }

    [Price]
    public string? Price { get => _price; set { _price = value; HasPrice = true; } }

    [JsonIgnore] public bool HasName { get; private set; }
    [JsonIgnore] public bool HasDescription { get; private set; }
    [JsonIgnore] public bool HasDurationMinutes { get; private set; }
    [JsonIgnore] public bool HasBufferBeforeMinutes { get; private set; }
    [JsonIgnore] public bool HasBufferAfterMinutes { get; private set; }
    [JsonIgnore] public bool HasPrice { get; private set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!HasName && !HasDescription && !HasDurationMinutes && !HasBufferBeforeMinutes && !HasBufferAfterMinutes && !HasPrice)
        {
            yield return new ValidationResult(
                "Send at least one of name, description, durationMinutes, bufferBeforeMinutes, bufferAfterMinutes and price.");
        }
        if (HasName && Name is null) yield return Null(nameof(Name));
        if (HasDurationMinutes && DurationMinutes is null) yield return Null(nameof(DurationMinutes));
        if (HasBufferBeforeMinutes && BufferBeforeMinutes is null) yield return Null(nameof(BufferBeforeMinutes));
        if (HasBufferAfterMinutes && BufferAfterMinutes is null) yield return Null(nameof(BufferAfterMinutes));
        if (HasPrice && Price is null) yield return Null(nameof(Price));
    }

    private static ValidationResult Null(string member) => new("Must not be null.", [member]);
}

/// <summary>
/// Services (story "Owner defines services"). Any signed-in user may view them; only the
/// business's owner may create or change them: 404 if the business or service does not exist,
/// otherwise 403 for anyone else (specs/07-nfr.md).
/// </summary>
[ApiController]
[Route("v1")]
public sealed class ServicesController(BookingDbContext db, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// A page of the business's services, ordered by name, then id. Names compare by the column's
    /// collation (utf8mb4_0900_ai_ci: ignoring case and accents), in the ORDER BY and the keyset alike,
    /// so the cursor agrees with the order; the (business_id, name, id) index serves both.
    /// </summary>
    [HttpGet("businesses/{businessId:guid}/services")]
    public async Task<Page<ServiceResponse>> List(Guid businessId, [FromQuery] PageQuery page, CancellationToken ct)
    {
        if (!await db.Businesses.AnyAsync(b => b.Id == businessId, ct)) throw ApiException.NotFound();

        var after = page.AfterNameId();
        var query = after is null
            ? db.Services.Where(s => s.BusinessId == businessId)
            // Keyset: rows after the cursor in (name, id) order, compared by the columns' collation.
            : db.Services.FromSql(
                $"SELECT * FROM services WHERE business_id = {businessId} AND (name > {after.Name} OR (name = {after.Name} AND id > {after.Id}))");
        var fetched = await query.AsNoTracking()
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .Take(page.Limit + 1)
            .ToListAsync(ct);

        return PageBuilder.From(fetched, page.Limit, s => new NameIdCursor(s.Name, s.Id).Encode(), ServiceResponse.From);
    }

    /// <summary>Owner only. The new service has no staff yet; Location is its URL.</summary>
    [HttpPost("businesses/{businessId:guid}/services")]
    public async Task<IActionResult> Create(Guid businessId, CreateServiceRequest request, CancellationToken ct)
    {
        var ownerUserId = await db.Businesses.Where(b => b.Id == businessId).Select(b => (Guid?)b.OwnerUserId).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound();
        Access.Require(canView: true, canAct: ownerUserId == User.GetUserId());

        PriceAttribute.TryParse(request.Price, out var price); // valid: checked by [Price]
        var service = new Service
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Name = request.Name.Trim(),
            Description = ServiceDescriptionAttribute.Normalize(request.Description),
            DurationMinutes = request.DurationMinutes!.Value,
            BufferBeforeMinutes = request.BufferBeforeMinutes!.Value,
            BufferAfterMinutes = request.BufferAfterMinutes!.Value,
            Price = price,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Services.Add(service);
        await db.SaveChangesAsync(ct);

        return Created($"/v1/services/{service.Id}", ServiceDetailResponse.From(service, []));
    }

    /// <summary>Any signed-in user: the service and its assigned staff who are not removed.</summary>
    [HttpGet("services/{serviceId:guid}")]
    public async Task<ServiceDetailResponse> Get(Guid serviceId, CancellationToken ct) => await DetailAsync(serviceId, ct);

    /// <summary>
    /// Owner only. Duration and buffer changes affect new bookings and reschedules only;
    /// existing bookings keep their snapshot (R1, ADR 0004). Only the sent columns are written,
    /// and the response is read again after saving, as for business settings.
    /// </summary>
    [HttpPatch("services/{serviceId:guid}")]
    public async Task<ServiceDetailResponse> Update(Guid serviceId, UpdateServiceRequest request, CancellationToken ct)
    {
        var service = Access.Found(await db.Services.SingleOrDefaultAsync(s => s.Id == serviceId, ct));
        var ownerUserId = await db.Businesses.Where(b => b.Id == service.BusinessId).Select(b => b.OwnerUserId).SingleAsync(ct);
        Access.Require(canView: true, canAct: ownerUserId == User.GetUserId());

        if (request.HasName) service.Name = request.Name!.Trim();
        if (request.HasDescription) service.Description = ServiceDescriptionAttribute.Normalize(request.Description);
        if (request.HasDurationMinutes) service.DurationMinutes = request.DurationMinutes!.Value;
        if (request.HasBufferBeforeMinutes) service.BufferBeforeMinutes = request.BufferBeforeMinutes!.Value;
        if (request.HasBufferAfterMinutes) service.BufferAfterMinutes = request.BufferAfterMinutes!.Value;
        if (request.HasPrice && PriceAttribute.TryParse(request.Price!, out var price)) service.Price = price;
        await db.SaveChangesAsync(ct);

        return await DetailAsync(serviceId, ct); // an untracked read: shows changes committed meanwhile
    }

    private async Task<ServiceDetailResponse> DetailAsync(Guid serviceId, CancellationToken ct)
    {
        var service = Access.Found(await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serviceId, ct));
        var staff = await (
                from link in db.StaffServices
                join s in db.Staff on link.StaffId equals s.Id
                join u in db.Users on s.UserId equals u.Id
                where link.ServiceId == serviceId && s.RemovedAt == null
                orderby u.Name, s.Id
                select new StaffSummaryResponse(s.Id, u.Name))
            .ToListAsync(ct);
        return ServiceDetailResponse.From(service, staff);
    }
}
