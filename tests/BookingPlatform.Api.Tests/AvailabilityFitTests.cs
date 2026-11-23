using System.Globalization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Slots;
using NodaTime;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T19: whether a booking still fits its staff member's availability after a change (R6, R8), without the
/// database (the endpoints are tested in AvailabilityR6Tests). Decisions 2026-11-23 (plan/tasks.md, T19): the
/// appointment [start, end) must lie inside a single block of the business-local date of its start; buffers and
/// steps don't count; block edges resolve as in slot generation (R2), so a booking fits exactly when a slot with
/// its times could exist in one block, ignoring steps.
/// </summary>
public class AvailabilityFitTests
{
    private static readonly DateTimeZone Utc = DateTimeZoneProviders.Tzdb["Etc/UTC"];
    private static readonly DateTimeZone NewYork = DateTimeZoneProviders.Tzdb["America/New_York"];

    private static readonly DateOnly Monday = new(2027, 3, 8);
    private static readonly DateOnly SpringForward = new(2027, 3, 14); // New York, 02:00 EST -> 03:00 EDT (07:00Z)
    private static readonly DateOnly FallBack = new(2027, 11, 7);      // New York, 02:00 EDT -> 01:00 EST (06:00Z)

    private static DateTime At(string text) =>
        DateTime.SpecifyKind(DateTime.ParseExact(text, "yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static (TimeOnly Start, TimeOnly End) Block(string start, string end) => (TimeOnly.Parse(start), TimeOnly.Parse(end));

    private static bool Fits(DateOnly date, string start, string end, params (TimeOnly, TimeOnly)[] blocks) =>
        AvailabilityFit.Fits(Utc, date, blocks, At(start), At(end));

    private static bool FitsInNewYork(DateOnly date, string start, string end, params (TimeOnly, TimeOnly)[] blocks) =>
        AvailabilityFit.Fits(NewYork, date, blocks, At(start), At(end));

    [Fact]
    public void R8_an_appointment_inside_a_block_fits_up_to_both_edges()
    {
        var block = Block("09:00", "12:00");

        Assert.True(Fits(Monday, "2027-03-08T09:00Z", "2027-03-08T12:00Z", block));
        Assert.True(Fits(Monday, "2027-03-08T10:00Z", "2027-03-08T11:00Z", block));
    }

    [Fact]
    public void R8_an_appointment_crossing_either_edge_does_not_fit()
    {
        var block = Block("09:00", "12:00");

        Assert.False(Fits(Monday, "2027-03-08T08:59Z", "2027-03-08T09:59Z", block));
        Assert.False(Fits(Monday, "2027-03-08T11:30Z", "2027-03-08T12:30Z", block));
        Assert.False(Fits(Monday, "2027-03-08T13:00Z", "2027-03-08T14:00Z", block));
    }

    [Fact]
    public void R8_steps_do_not_count_only_containment_does()
    {
        // Decided 2026-11-23: 09:30 is not a step of a 09:15 block with 30-minute steps, but it still fits.
        Assert.True(Fits(Monday, "2027-03-08T09:30Z", "2027-03-08T10:30Z", Block("09:15", "17:00")));
    }

    [Fact]
    public void R8_touching_blocks_stay_separate_ADR0011()
    {
        Assert.False(Fits(Monday, "2027-03-08T11:30Z", "2027-03-08T12:30Z", Block("09:00", "12:00"), Block("12:00", "17:00")));
        Assert.True(Fits(Monday, "2027-03-08T12:00Z", "2027-03-08T13:00Z", Block("09:00", "12:00"), Block("12:00", "17:00")));
    }

    [Fact]
    public void R8_no_blocks_means_nothing_fits()
    {
        Assert.False(Fits(Monday, "2027-03-08T10:00Z", "2027-03-08T11:00Z"));
    }

    [Fact]
    public void R2_the_local_date_is_the_business_local_date_of_the_start()
    {
        Assert.Equal(new DateOnly(2027, 3, 8), AvailabilityFit.LocalDate(NewYork, At("2027-03-09T02:00Z"))); // 21:00 EST on the 8th
        Assert.Equal(new DateOnly(2027, 3, 9), AvailabilityFit.LocalDate(Utc, At("2027-03-09T02:00Z")));
    }

    [Fact]
    public void R2_a_two_hour_booking_from_01_00_on_a_spring_forward_night_fits_a_block_ending_at_04_00()
    {
        // R2's example: 01:00 EST (06:00Z) + 2 h ends 04:00 EDT (08:00Z).
        Assert.True(FitsInNewYork(SpringForward, "2027-03-14T06:00Z", "2027-03-14T08:00Z", Block("01:00", "04:00")));
        Assert.False(FitsInNewYork(SpringForward, "2027-03-14T06:00Z", "2027-03-14T08:30Z", Block("01:00", "04:00")));
    }

    [Fact]
    public void R2_a_block_edge_inside_the_spring_forward_gap_resolves_to_the_gap_end()
    {
        // Block 02:30-05:00: its start resolves to 03:00 EDT (07:00Z).
        Assert.True(FitsInNewYork(SpringForward, "2027-03-14T07:00Z", "2027-03-14T08:00Z", Block("02:30", "05:00")));
        Assert.False(FitsInNewYork(SpringForward, "2027-03-14T06:30Z", "2027-03-14T07:30Z", Block("02:30", "05:00")));
        // Block 00:00-02:30: its end resolves to 03:00 EDT (07:00Z).
        Assert.True(FitsInNewYork(SpringForward, "2027-03-14T06:00Z", "2027-03-14T07:00Z", Block("00:00", "02:30")));
        Assert.False(FitsInNewYork(SpringForward, "2027-03-14T06:30Z", "2027-03-14T07:30Z", Block("00:00", "02:30")));
    }

    [Fact]
    public void R2_an_ambiguous_block_start_resolves_to_its_earlier_and_an_ambiguous_end_to_its_later_occurrence()
    {
        // Block 01:30-03:00: start at 01:30 EDT (05:30Z).
        Assert.True(FitsInNewYork(FallBack, "2027-11-07T05:30Z", "2027-11-07T06:30Z", Block("01:30", "03:00")));
        Assert.False(FitsInNewYork(FallBack, "2027-11-07T05:00Z", "2027-11-07T06:00Z", Block("01:30", "03:00")));
        // Block 00:00-01:30: end at 01:30 EST (06:30Z), so the whole repeated hour is covered.
        Assert.True(FitsInNewYork(FallBack, "2027-11-07T04:30Z", "2027-11-07T06:30Z", Block("00:00", "01:30")));
        Assert.False(FitsInNewYork(FallBack, "2027-11-07T05:45Z", "2027-11-07T06:45Z", Block("00:00", "01:30")));
    }

    [Theory]
    [InlineData("2027-03-14", "00:00", "04:00")]
    [InlineData("2027-03-14", "02:15", "05:00")]
    [InlineData("2027-03-14", "00:00", "02:30")]
    [InlineData("2027-11-07", "00:00", "04:00")]
    [InlineData("2027-11-07", "01:30", "03:00")]
    [InlineData("2027-11-07", "00:00", "01:30")]
    public void R8_every_slot_slot_generation_offers_fits_its_block(string date, string start, string end)
    {
        var day = DateOnly.Parse(date, CultureInfo.InvariantCulture);
        var rule = new AvailabilityRule { Id = Guid.NewGuid(), StaffId = Guid.Empty, DayOfWeek = day.DayOfWeek, StartLocal = TimeOnly.Parse(start), EndLocal = TimeOnly.Parse(end) };
        var slots = SlotGenerator.Generate(NewYork, new SlotSettings(0, 3650, 15), new SlotService(45, 0, 0), day, day, At("2027-01-01T00:00Z"),
            ignoreMinimumNotice: true, [new StaffSchedule(Guid.Empty, [rule], [], [])]);

        Assert.NotEmpty(slots);
        Assert.All(slots, s => Assert.True(AvailabilityFit.Fits(NewYork, day, [Block(start, end)], s.StartUtc, s.EndUtc)));
        Assert.All(slots, s => Assert.Equal(day, AvailabilityFit.LocalDate(NewYork, s.StartUtc)));
    }
}
