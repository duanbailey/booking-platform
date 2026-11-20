using System.Globalization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Slots;
using NodaTime;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T13: slot generation rules, without the database (the endpoint is tested in SlotsTests).
/// Decisions 2026-11-20 (plan/tasks.md): notice inclusive (start ≥ now + notice), advance window
/// start ≤ now + maxAdvanceDays × 24 h, steps on the local wall clock from each block start, a
/// block edge in a spring-forward gap resolves to the gap's end, an ambiguous block start to its
/// earlier and an ambiguous end to its later occurrence, ordered by start then staffId.
/// Early March 2027 Europe/London is UTC+0, so local and UTC times coincide there.
/// </summary>
public class SlotGeneratorTests
{
    private static readonly DateTimeZone London = DateTimeZoneProviders.Tzdb["Europe/London"];
    private static readonly DateTimeZone NewYork = DateTimeZoneProviders.Tzdb["America/New_York"];
    private static readonly DateTimeZone Auckland = DateTimeZoneProviders.Tzdb["Pacific/Auckland"];

    private static readonly Guid StaffA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid StaffB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static readonly DateOnly Monday = new(2027, 3, 8);
    private static readonly DateOnly SpringForward = new(2027, 3, 14); // New York, 02:00 EST -> 03:00 EDT
    private static readonly DateOnly FallBack = new(2027, 11, 7);      // New York, 02:00 EDT -> 01:00 EST

    private static readonly DateTime DefaultNow = Utc("2027-03-01T12:00Z");
    private static readonly SlotSettings AnyTime = new(MinNoticeMinutes: 0, MaxAdvanceDays: 3650, SlotStepMinutes: 30);
    private static readonly SlotService Hour = new(60, 0, 0);

    private static DateTime Utc(string text) =>
        DateTime.SpecifyKind(DateTime.ParseExact(text, "yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static string Text(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);

    private static AvailabilityRule Weekly(DayOfWeek day, string start, string end) => new()
    {
        Id = Guid.NewGuid(), StaffId = StaffA, DayOfWeek = day, StartLocal = TimeOnly.Parse(start), EndLocal = TimeOnly.Parse(end),
    };

    private static AvailabilityException Exception(DateOnly date, string? start, string? end) => new()
    {
        Id = Guid.NewGuid(), StaffId = StaffA, Date = date,
        StartLocal = start is null ? null : TimeOnly.Parse(start), EndLocal = end is null ? null : TimeOnly.Parse(end),
    };

    private static StaffSchedule Schedule(
        IEnumerable<AvailabilityRule>? weekly = null, IEnumerable<AvailabilityException>? exceptions = null,
        IEnumerable<UtcInterval>? blocked = null, Guid? staffId = null) =>
        new(staffId ?? StaffA, (weekly ?? []).ToList(), (exceptions ?? []).ToList(), (blocked ?? []).ToList());

    private static List<OpenSlot> Generate(
        StaffSchedule schedule, DateOnly from, DateOnly? to = null, DateTimeZone? zone = null, SlotSettings? settings = null,
        SlotService? service = null, DateTime? now = null, bool ignoreMinimumNotice = false) =>
        SlotGenerator.Generate(zone ?? London, settings ?? AnyTime, service ?? Hour, from, to ?? from, now ?? DefaultNow,
            ignoreMinimumNotice, [schedule]);

    private static List<string> Starts(IEnumerable<OpenSlot> slots) => slots.Select(s => Text(s.StartUtc)).ToList();

    // ------------------------------------------------------------ R8 availability

    [Fact]
    public void R8_steps_are_counted_from_each_block_start_and_the_appointment_ends_by_the_block_end()
    {
        var slots = Generate(Schedule([Weekly(DayOfWeek.Monday, "09:15", "11:00"), Weekly(DayOfWeek.Monday, "13:00", "14:30")]), Monday);

        Assert.Equal(["2027-03-08T09:15Z", "2027-03-08T09:45Z", "2027-03-08T13:00Z", "2027-03-08T13:30Z"], Starts(slots));
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(60), s.EndUtc - s.StartUtc));
        Assert.All(slots, s => Assert.Equal(StaffA, s.StaffId));
        Assert.All(slots, s => Assert.Equal(DateTimeKind.Utc, s.StartUtc.Kind));
    }

    [Fact]
    public void R8_touching_blocks_stay_separate_so_no_slot_crosses_from_one_into_the_next()
    {
        // Decided 2026-11-20 (code review): each block bounds slots on its own; 11:30-12:30 is not offered,
        // and steps restart at 12:00, the second block's start.
        var weekly = Schedule([Weekly(DayOfWeek.Monday, "10:00", "12:00"), Weekly(DayOfWeek.Monday, "12:00", "13:00")]);
        var exceptions = Schedule(exceptions: [Exception(Monday, "10:00", "12:00"), Exception(Monday, "12:00", "13:00")]);

        string[] expected = ["2027-03-08T10:00Z", "2027-03-08T10:30Z", "2027-03-08T11:00Z", "2027-03-08T12:00Z"];
        Assert.Equal(expected, Starts(Generate(weekly, Monday)));
        Assert.Equal(expected, Starts(Generate(exceptions, Monday)));
    }

    [Fact]
    public void R8_a_step_longer_than_the_appointment_leaves_gaps()
    {
        var slots = Generate(Schedule([Weekly(DayOfWeek.Monday, "09:00", "12:00")]), Monday,
            settings: AnyTime with { SlotStepMinutes = 90 }, service: new SlotService(30, 0, 0));

        Assert.Equal(["2027-03-08T09:00Z", "2027-03-08T10:30Z"], Starts(slots));
    }

    [Fact]
    public void R8_a_date_exception_replaces_the_weekly_availability_for_its_date_only()
    {
        var schedule = Schedule([Weekly(DayOfWeek.Monday, "09:00", "11:00")], [Exception(Monday, "14:00", "15:00")]);

        var slots = Generate(schedule, Monday, Monday.AddDays(7));

        Assert.Equal(["2027-03-08T14:00Z", "2027-03-15T09:00Z", "2027-03-15T09:30Z", "2027-03-15T10:00Z"], Starts(slots));
    }

    [Fact]
    public void R8_an_unavailable_exception_gives_no_slots_that_date()
    {
        var schedule = Schedule([Weekly(DayOfWeek.Monday, "09:00", "10:00")], [Exception(Monday, null, null)]);

        Assert.Equal(["2027-03-15T09:00Z"], Starts(Generate(schedule, Monday, Monday.AddDays(7))));
    }

    [Fact]
    public void R8_an_exception_may_have_several_ranges()
    {
        var schedule = Schedule([Weekly(DayOfWeek.Monday, "12:00", "13:00")],
            [Exception(Monday, "09:00", "10:00"), Exception(Monday, "16:00", "17:00")]);

        Assert.Equal(["2027-03-08T09:00Z", "2027-03-08T16:00Z"], Starts(Generate(schedule, Monday)));
    }

    [Fact]
    public void R8_an_exception_gives_slots_on_a_day_without_weekly_availability()
    {
        var schedule = Schedule(exceptions: [Exception(Monday, "09:00", "10:00")]);

        Assert.Equal(["2027-03-08T09:00Z"], Starts(Generate(schedule, Monday)));
    }

    [Fact]
    public void R8_only_dates_in_the_range_both_inclusive_are_covered()
    {
        var schedule = Schedule([
            Weekly(DayOfWeek.Sunday, "09:00", "10:00"), Weekly(DayOfWeek.Monday, "09:00", "10:00"),
            Weekly(DayOfWeek.Tuesday, "09:00", "10:00"), Weekly(DayOfWeek.Wednesday, "09:00", "10:00")]);

        Assert.Equal(["2027-03-08T09:00Z", "2027-03-09T09:00Z"], Starts(Generate(schedule, Monday, Monday.AddDays(1))));
    }

    [Fact]
    public void R8_the_weekday_is_the_business_local_date()
    {
        // 09:00 on Monday in Auckland (UTC+13 in March) is 20:00 on Sunday in UTC.
        var slots = Generate(Schedule([Weekly(DayOfWeek.Monday, "09:00", "10:00")]), Monday, zone: Auckland);

        Assert.Equal(["2027-03-07T20:00Z"], Starts(slots));
    }

    [Fact]
    public void R8_no_slots_start_in_the_past()
    {
        var slots = Generate(Schedule([Weekly(DayOfWeek.Monday, "09:00", "12:00")]), Monday, now: Utc("2027-03-08T10:10Z"));

        Assert.Equal(["2027-03-08T10:30Z", "2027-03-08T11:00Z"], Starts(slots));
    }

    [Fact]
    public void R8_the_advance_window_is_checked_on_the_start_and_is_inclusive()
    {
        // Window end: now + 7 × 24 h = 2027-03-08T09:00Z.
        var slots = Generate(Schedule([Weekly(DayOfWeek.Monday, "08:00", "12:00")]), Monday,
            settings: AnyTime with { MaxAdvanceDays = 7 }, now: Utc("2027-03-01T09:00Z"));

        Assert.Equal(["2027-03-08T08:00Z", "2027-03-08T08:30Z", "2027-03-08T09:00Z"], Starts(slots));
    }

    [Fact]
    public void R8_availability_bounds_the_appointment_but_not_its_buffers()
    {
        var slots = Generate(Schedule([Weekly(DayOfWeek.Monday, "09:00", "11:00")]), Monday, service: new SlotService(60, 15, 15));

        Assert.Equal(["2027-03-08T09:00Z", "2027-03-08T09:30Z", "2027-03-08T10:00Z"], Starts(slots));
    }

    [Fact]
    public void R8_a_slot_whose_blocked_interval_overlaps_time_off_is_not_offered_and_touching_is_fine()
    {
        // Each slot blocks [start - 15, end + 15). Time off [10:45, 11:45) excludes starts after 09:30 and before 12:00.
        var schedule = Schedule([Weekly(DayOfWeek.Monday, "09:00", "13:00")],
            blocked: [new UtcInterval(Utc("2027-03-08T10:45Z"), Utc("2027-03-08T11:45Z"))]);

        var slots = Generate(schedule, Monday, service: new SlotService(60, 15, 15));

        Assert.Equal(["2027-03-08T09:00Z", "2027-03-08T09:30Z", "2027-03-08T12:00Z"], Starts(slots));
    }

    [Fact]
    public void R1_R8_every_blocked_interval_counts_even_when_they_overlap_each_other()
    {
        var schedule = Schedule([Weekly(DayOfWeek.Monday, "09:00", "12:00")], blocked: [
            new UtcInterval(Utc("2027-03-08T09:45Z"), Utc("2027-03-08T10:15Z")),
            new UtcInterval(Utc("2027-03-08T10:10Z"), Utc("2027-03-08T10:50Z"))]);

        var slots = Generate(schedule, Monday, service: new SlotService(30, 0, 0));

        Assert.Equal(["2027-03-08T09:00Z", "2027-03-08T11:00Z", "2027-03-08T11:30Z"], Starts(slots));
    }

    [Fact]
    public void ADR0005_ranges_in_the_distant_past_or_future_are_empty_not_errors()
    {
        var schedule = Schedule([.. Enum.GetValues<DayOfWeek>().Select(d => Weekly(d, "00:00", "23:59"))]);

        Assert.Empty(Generate(schedule, new DateOnly(1, 1, 1), new DateOnly(1, 1, 30)));
        Assert.Empty(Generate(schedule, new DateOnly(9999, 12, 2), new DateOnly(9999, 12, 31)));
        Assert.Empty(Generate(schedule, new DateOnly(1, 1, 1), new DateOnly(1, 1, 30), zone: Auckland));
        Assert.Empty(Generate(schedule, new DateOnly(9999, 12, 2), new DateOnly(9999, 12, 31), zone: Auckland,
            settings: AnyTime with { SlotStepMinutes = 1 }, service: new SlotService(1440, 1440, 1440)));
    }

    // ------------------------------------------------------------ R3 minimum notice

    [Fact]
    public void R3_a_slot_exactly_at_the_end_of_the_notice_period_is_offered()
    {
        var schedule = Schedule([Weekly(DayOfWeek.Monday, "08:00", "11:00")]);
        var settings = AnyTime with { MinNoticeMinutes = 120 };

        Assert.Equal(["2027-03-08T09:00Z", "2027-03-08T09:30Z", "2027-03-08T10:00Z"],
            Starts(Generate(schedule, Monday, settings: settings, now: Utc("2027-03-08T07:00Z"))));
        Assert.Equal(["2027-03-08T09:30Z", "2027-03-08T10:00Z"],
            Starts(Generate(schedule, Monday, settings: settings, now: Utc("2027-03-08T07:00Z").AddTicks(10))));
    }

    [Fact]
    public void R3_ignoring_the_notice_offers_slots_inside_it_but_never_in_the_past()
    {
        var slots = Generate(Schedule([Weekly(DayOfWeek.Monday, "08:00", "10:00")]), Monday,
            settings: AnyTime with { MinNoticeMinutes = 120 }, now: Utc("2027-03-08T08:30Z"), ignoreMinimumNotice: true);

        Assert.Equal(["2027-03-08T08:30Z", "2027-03-08T09:00Z"], Starts(slots));
    }

    // ------------------------------------------------------------ R2 daylight saving

    [Fact]
    public void R2_the_spring_forward_gap_is_skipped_and_slots_last_their_elapsed_duration()
    {
        // 00:00-04:00 local; 02:00 and 02:30 do not exist. 01:30 EST + 60 min = 03:30 EDT.
        var slots = Generate(Schedule([Weekly(DayOfWeek.Sunday, "00:00", "04:00")]), SpringForward, zone: NewYork);

        Assert.Equal(["2027-03-14T05:00Z", "2027-03-14T05:30Z", "2027-03-14T06:00Z", "2027-03-14T06:30Z", "2027-03-14T07:00Z"],
            Starts(slots));
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(60), s.EndUtc - s.StartUtc));
    }

    [Fact]
    public void R2_a_two_hour_slot_at_01_00_on_a_spring_forward_night_ends_at_04_00_local()
    {
        var slots = Generate(Schedule([Weekly(DayOfWeek.Sunday, "00:00", "04:00")]), SpringForward, zone: NewYork,
            service: new SlotService(120, 0, 0));

        var slot = Assert.Single(slots, s => s.StartUtc == Utc("2027-03-14T06:00Z")); // 01:00 EST
        Assert.Equal(Utc("2027-03-14T08:00Z"), slot.EndUtc);                         // 04:00 EDT
        Assert.Equal(["2027-03-14T05:00Z", "2027-03-14T05:30Z", "2027-03-14T06:00Z"], Starts(slots));
    }

    [Fact]
    public void R2_only_the_first_occurrence_of_a_fall_back_overlap_is_offered()
    {
        // 00:00-04:00 local; 01:00 and 01:30 happen twice (EDT 05:00Z/05:30Z, then EST 06:00Z/06:30Z).
        var slots = Generate(Schedule([Weekly(DayOfWeek.Sunday, "00:00", "04:00")]), FallBack, zone: NewYork);

        Assert.Equal([
            "2027-11-07T04:00Z", "2027-11-07T04:30Z", "2027-11-07T05:00Z", "2027-11-07T05:30Z",
            "2027-11-07T07:00Z", "2027-11-07T07:30Z", "2027-11-07T08:00Z"], Starts(slots));
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(60), s.EndUtc - s.StartUtc));
    }

    [Fact]
    public void R2_a_block_end_inside_the_gap_is_the_instant_the_gap_ends()
    {
        // 02:30 does not exist; the block ends at 03:00 EDT (07:00Z), so 01:30 EST (ending 07:30Z) is not offered.
        var slots = Generate(Schedule([Weekly(DayOfWeek.Sunday, "00:00", "02:30")]), SpringForward, zone: NewYork);

        Assert.Equal(["2027-03-14T05:00Z", "2027-03-14T05:30Z", "2027-03-14T06:00Z"], Starts(slots));
    }

    [Fact]
    public void R2_steps_from_a_block_start_inside_the_gap_stay_on_its_local_grid()
    {
        var slots = Generate(Schedule([Weekly(DayOfWeek.Sunday, "02:15", "04:00")]), SpringForward, zone: NewYork,
            service: new SlotService(30, 0, 0));

        Assert.Equal(["2027-03-14T07:15Z"], Starts(slots)); // 03:15 EDT; 02:15 and 02:45 do not exist
    }

    [Fact]
    public void R2_an_ambiguous_block_end_is_its_later_occurrence()
    {
        // The block ends at 01:30 EST (06:30Z), so 01:00 EDT (05:00Z-06:00Z) fits.
        var slots = Generate(Schedule([Weekly(DayOfWeek.Sunday, "00:00", "01:30")]), FallBack, zone: NewYork);

        Assert.Equal(["2027-11-07T04:00Z", "2027-11-07T04:30Z", "2027-11-07T05:00Z"], Starts(slots));
    }

    [Fact]
    public void R2_an_ambiguous_block_start_is_its_earlier_occurrence()
    {
        var slots = Generate(Schedule([Weekly(DayOfWeek.Sunday, "01:30", "03:00")]), FallBack, zone: NewYork,
            service: new SlotService(30, 0, 0));

        Assert.Equal(["2027-11-07T05:30Z", "2027-11-07T07:00Z", "2027-11-07T07:30Z"], Starts(slots));
    }

    // ------------------------------------------------------------ Ordering

    [Fact]
    public void Slots_are_ordered_by_start_then_staffId()
    {
        var b = Schedule([Weekly(DayOfWeek.Monday, "09:00", "10:30")], staffId: StaffB);
        var a = Schedule([new AvailabilityRule { StaffId = StaffA, DayOfWeek = DayOfWeek.Monday, StartLocal = new(9, 30), EndLocal = new(10, 30) }]);

        var slots = SlotGenerator.Generate(London, AnyTime, Hour, Monday, Monday, DefaultNow, false, [b, a]);

        Assert.Equal([
            (StaffB, "2027-03-08T09:00Z"), (StaffA, "2027-03-08T09:30Z"), (StaffB, "2027-03-08T09:30Z")],
            slots.Select(s => (s.StaffId, Text(s.StartUtc))).ToList());
    }
}
