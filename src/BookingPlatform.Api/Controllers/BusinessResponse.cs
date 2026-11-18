using BookingPlatform.Api.Data;

namespace BookingPlatform.Api.Controllers;

/// <summary>The Business schema in specs/04-api.openapi.yaml.</summary>
public sealed record BusinessResponse(
    Guid Id, string Name, string TimeZone, int MinNoticeMinutes, int CancellationWindowHours,
    int MaxAdvanceDays, int SlotStepMinutes)
{
    public static BusinessResponse From(Business b) => new(
        b.Id, b.Name, b.TimeZone, b.MinNoticeMinutes, b.CancellationWindowHours, b.MaxAdvanceDays, b.SlotStepMinutes);
}
