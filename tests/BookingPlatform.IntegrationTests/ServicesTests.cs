using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T9: list, create, get and update services. Story "Owner defines services" (name, optional
/// description, duration, buffers, price). Any signed-in user may view services; only the
/// business's owner may create or change them (403 for anyone else, 404 if the business or
/// service does not exist; specs/07-nfr.md). Duration and buffer changes never rewrite existing
/// bookings (ADR 0004). Lists use the cursor pattern decided 2026-11-19 (name, then id).
/// </summary>
public class ServicesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static string ListPathFor(Guid businessId) => $"/v1/businesses/{businessId}/services";
    private static string PathFor(Guid serviceId) => $"/v1/services/{serviceId}";

    private const string ValidBody =
        """{"name":"Haircut","durationMinutes":60,"bufferBeforeMinutes":10,"bufferAfterMinutes":5,"price":"45"}""";

    private Task<HttpResponseMessage> List(Guid callerId, Guid businessId, string query = "") =>
        fixture.Api.ClientFor(callerId).GetAsync(ListPathFor(businessId) + query);

    private Task<HttpResponseMessage> Create(Guid callerId, Guid businessId, string json) =>
        fixture.Api.ClientFor(callerId).PostAsync(ListPathFor(businessId), Json.Body(json));

    private Task<HttpResponseMessage> Get(Guid callerId, Guid serviceId) =>
        fixture.Api.ClientFor(callerId).GetAsync(PathFor(serviceId));

    private Task<HttpResponseMessage> Patch(Guid callerId, Guid serviceId, string json) =>
        fixture.Api.ClientFor(callerId).PatchAsync(PathFor(serviceId), Json.Body(json));

    private async Task<(Guid OwnerId, Guid BusinessId)> BusinessAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync();
        return (owner, await fixture.Sql.InsertBusinessAsync(owner));
    }

    private Task<long> ServiceCountAsync(Guid businessId) => CountAsync(
        "SELECT COUNT(*) FROM services WHERE business_id = @businessId", new { businessId });

    private async Task<long> CountAsync(string sql, object parameters) =>
        Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    /// <summary>The service row, read straight from the database.</summary>
    private async Task<string> StoredAsync(Guid serviceId) => Convert.ToString(await fixture.Sql.ScalarAsync(
        "SELECT CONCAT_WS('|', business_id, name, IFNULL(description, '<null>'), duration_minutes, buffer_before_minutes, " +
        "buffer_after_minutes, price) FROM services WHERE id = @serviceId", new { serviceId }))!;

    private static void AssertService(JsonElement body, Guid businessId, string name, string? description,
        int duration, int bufferBefore, int bufferAfter, string price)
    {
        Assert.Equal(businessId, body.GetProperty("businessId").GetGuid());
        Assert.Equal(name, body.GetProperty("name").GetString());
        if (description is null) Assert.Equal(JsonValueKind.Null, body.GetProperty("description").ValueKind);
        else Assert.Equal(description, body.GetProperty("description").GetString());
        Assert.Equal(duration, body.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(bufferBefore, body.GetProperty("bufferBeforeMinutes").GetInt32());
        Assert.Equal(bufferAfter, body.GetProperty("bufferAfterMinutes").GetInt32());
        Assert.Equal(price, body.GetProperty("price").GetString());
    }

    private static List<string> Names(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList();

    // ------------------------------------------------------------ List

    [Fact]
    public async Task Any_signed_in_user_lists_a_business_s_services_by_name_ignoring_case()
    {
        var (_, businessId) = await BusinessAsync();
        foreach (var name in new[] { "colour", "Beard trim", "Cut", "afro" }) await fixture.Sql.InsertServiceAsync(businessId, name);
        var (_, otherBusiness) = await BusinessAsync();
        await fixture.Sql.InsertServiceAsync(otherBusiness, "Another business's service");

        var page = await Json.ReadAsync(await List(await fixture.Sql.InsertUserAsync(), businessId), HttpStatusCode.OK);

        Assert.Equal(["afro", "Beard trim", "colour", "Cut"], Names(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        var first = page.GetProperty("items")[0];
        AssertService(first, businessId, "afro", description: null, duration: 60, bufferBefore: 15, bufferAfter: 15, price: "45.00");
        Assert.Equal(8, first.EnumerateObject().Count()); // the Service schema: no staff in lists
    }

    [Fact]
    public async Task Business_without_services_lists_an_empty_last_page()
    {
        var (owner, businessId) = await BusinessAsync();

        var page = await Json.ReadAsync(await List(owner, businessId), HttpStatusCode.OK);

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Pages_follow_the_cursor_until_it_is_null()
    {
        var (owner, businessId) = await BusinessAsync();
        foreach (var name in new[] { "E", "C", "A", "D", "B" }) await fixture.Sql.InsertServiceAsync(businessId, name);

        var names = new List<string>();
        var pages = 0;
        string? cursor = null;
        do
        {
            Assert.True(pages < 10, "the cursor never reached the last page"); // a cursor bug fails, not hangs
            var query = "?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await Json.ReadAsync(await List(owner, businessId, query), HttpStatusCode.OK);
            names.AddRange(Names(page));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null);

        Assert.Equal(["A", "B", "C", "D", "E"], names);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task Last_full_page_has_no_cursor_when_nothing_follows()
    {
        var (owner, businessId) = await BusinessAsync();
        await fixture.Sql.InsertServiceAsync(businessId, "A");
        await fixture.Sql.InsertServiceAsync(businessId, "B");

        var page = await Json.ReadAsync(await List(owner, businessId, "?limit=2"), HttpStatusCode.OK);

        Assert.Equal(["A", "B"], Names(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Same_names_are_ordered_by_id_across_pages()
    {
        var (owner, businessId) = await BusinessAsync();
        var high = Guid.Parse("ffffffff-0000-0000-0000-000000000009");
        var low = Guid.Parse("00000000-0000-0000-0000-000000000009");
        await fixture.Sql.InsertServiceAsync(businessId, "Haircut", high); // inserted first: insertion order can't decide
        await fixture.Sql.InsertServiceAsync(businessId, "HAIRCUT", low);  // equal ignoring case

        var first = await Json.ReadAsync(await List(owner, businessId, "?limit=1"), HttpStatusCode.OK);
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await Json.ReadAsync(await List(owner, businessId, "?limit=1&cursor=" + Uri.EscapeDataString(cursor)),
            HttpStatusCode.OK);

        Assert.Equal(low, first.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(high, second.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Names_differing_only_in_accents_are_equal_and_ordered_by_id()
    {
        // The column collation ignores accents as well as case (decided 2026-11-19).
        var (owner, businessId) = await BusinessAsync();
        var high = Guid.Parse("ffffffff-0000-0000-0000-00000000000a");
        var low = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        await fixture.Sql.InsertServiceAsync(businessId, "Cafe", high);
        await fixture.Sql.InsertServiceAsync(businessId, "Café", low);
        await fixture.Sql.InsertServiceAsync(businessId, "Cafeteria");

        var first = await Json.ReadAsync(await List(owner, businessId, "?limit=1"), HttpStatusCode.OK);
        var rest = await Json.ReadAsync(await List(owner, businessId,
            "?cursor=" + Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!)), HttpStatusCode.OK);

        Assert.Equal(["Café"], Names(first));
        Assert.Equal(["Cafe", "Cafeteria"], Names(rest));
    }

    [Fact]
    public async Task Default_limit_is_50()
    {
        var (owner, businessId) = await BusinessAsync();
        for (var i = 0; i < 51; i++) await fixture.Sql.InsertServiceAsync(businessId, $"Service {i:D2}");

        var page = await Json.ReadAsync(await List(owner, businessId), HttpStatusCode.OK);

        Assert.Equal(50, page.GetProperty("items").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("?limit=1", 1)]
    [InlineData("?limit=200", 3)]
    public async Task Limit_accepts_1_to_200(string query, int expected)
    {
        var (owner, businessId) = await BusinessAsync();
        for (var i = 0; i < 3; i++) await fixture.Sql.InsertServiceAsync(businessId, $"S{i}");

        var page = await Json.ReadAsync(await List(owner, businessId, query), HttpStatusCode.OK);

        Assert.Equal(expected, page.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("limit", "?limit=0")]
    [InlineData("limit", "?limit=201")]
    [InlineData("limit", "?limit=-1")]
    [InlineData("limit", "?limit=abc")]
    [InlineData("limit", "?limit=")]
    [InlineData("cursor", "?cursor=not-a-cursor")]
    [InlineData("cursor", "?cursor=")]
    [InlineData("cursor", "?cursor=eyJ4IjoxfQ")] // valid base64url of {"x":1}, but not a cursor
    public async Task Bad_limit_or_cursor_returns_400_naming_it(string field, string query)
    {
        var (owner, businessId) = await BusinessAsync();

        var error = await Json.ErrorAsync(await List(owner, businessId, query), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
    }

    [Fact]
    public async Task Listing_an_unknown_business_is_404()
    {
        await Json.ErrorAsync(await List(await fixture.Sql.InsertUserAsync(), Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Listing_without_a_token_is_401()
    {
        var (_, businessId) = await BusinessAsync();

        await Json.ErrorAsync(await fixture.Api.CreateClient().GetAsync(ListPathFor(businessId)),
            HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    // ------------------------------------------------------------ Create

    [Fact]
    public async Task Owner_creates_a_service_in_their_business()
    {
        var (owner, businessId) = await BusinessAsync();

        var response = await Create(owner, businessId,
            """{"name":"  Haircut  ","description":"  Wash, cut and dry  ","durationMinutes":60,"bufferBeforeMinutes":10,"bufferAfterMinutes":5,"price":"45"}""");

        var body = await Json.ReadAsync(response, HttpStatusCode.Created);
        var id = body.GetProperty("id").GetGuid();
        AssertService(body, businessId, "Haircut", "Wash, cut and dry", duration: 60, bufferBefore: 10, bufferAfter: 5, price: "45.00");
        Assert.Empty(body.GetProperty("staff").EnumerateArray());
        Assert.Equal($"/v1/services/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal($"{businessId}|Haircut|Wash, cut and dry|60|10|5|45.00", await StoredAsync(id));
    }

    [Theory]
    [InlineData("""  "description":null,""", "<null>")]
    [InlineData("""  "description":"   ",""", "<null>")]
    [InlineData("""  "description":"",""", "<null>")]
    [InlineData("", "<null>")]
    public async Task Missing_or_blank_description_is_stored_as_null(string descriptionJson, string stored)
    {
        var (owner, businessId) = await BusinessAsync();

        var body = await Json.ReadAsync(await Create(owner, businessId,
            "{" + descriptionJson + "\"name\":\"Cut\",\"durationMinutes\":30,\"bufferBeforeMinutes\":0,\"bufferAfterMinutes\":0,\"price\":\"20\"}"),
            HttpStatusCode.Created);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("description").ValueKind);
        Assert.EndsWith($"|Cut|{stored}|30|0|0|20.00", await StoredAsync(body.GetProperty("id").GetGuid()));
    }

    [Theory]
    [InlineData("45", "45.00")]
    [InlineData("45.5", "45.50")]
    [InlineData("045.50", "45.50")]
    [InlineData("0", "0.00")]
    [InlineData("99999999.99", "99999999.99")]
    public async Task Price_is_returned_with_two_decimals(string sent, string returned)
    {
        var (owner, businessId) = await BusinessAsync();

        var body = await Json.ReadAsync(await Create(owner, businessId,
            $$"""{"name":"Cut","durationMinutes":30,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"{{sent}}"}"""),
            HttpStatusCode.Created);

        Assert.Equal(returned, body.GetProperty("price").GetString());
        var listed = await Json.ReadAsync(await List(owner, businessId), HttpStatusCode.OK);
        Assert.Equal(returned, listed.GetProperty("items")[0].GetProperty("price").GetString());
    }

    [Fact]
    public async Task Duration_and_buffers_accept_their_maxima()
    {
        var (owner, businessId) = await BusinessAsync();

        var body = await Json.ReadAsync(await Create(owner, businessId,
            """{"name":"All day","durationMinutes":1440,"bufferBeforeMinutes":1440,"bufferAfterMinutes":1440,"price":"1"}"""),
            HttpStatusCode.Created);

        AssertService(body, businessId, "All day", null, duration: 1440, bufferBefore: 1440, bufferAfter: 1440, price: "1.00");
    }

    [Fact]
    public async Task Description_of_2000_characters_after_trimming_is_accepted()
    {
        var (owner, businessId) = await BusinessAsync();
        var description = new string('d', 2000);

        var body = await Json.ReadAsync(await Create(owner, businessId,
            $$"""{"name":"Cut","description":"  {{description}}  ","durationMinutes":30,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1"}"""),
            HttpStatusCode.Created);

        Assert.Equal(description, body.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("name", """{"durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1"}""")]
    [InlineData("name", """{"name":null,"durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1"}""")]
    [InlineData("name", """{"name":"   ","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1"}""")]
    [InlineData("durationMinutes", """{"name":"Cut","bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1"}""")]
    [InlineData("durationMinutes", """{"name":"Cut","durationMinutes":0,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1"}""")]
    [InlineData("durationMinutes", """{"name":"Cut","durationMinutes":1441,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1"}""")]
    [InlineData("durationMinutes", """{"name":"Cut","durationMinutes":null,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1"}""")]
    [InlineData("bufferBeforeMinutes", """{"name":"Cut","durationMinutes":60,"bufferAfterMinutes":0,"price":"1"}""")]
    [InlineData("bufferBeforeMinutes", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":-1,"bufferAfterMinutes":0,"price":"1"}""")]
    [InlineData("bufferAfterMinutes", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":1441,"price":"1"}""")]
    [InlineData("price", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0}""")]
    [InlineData("price", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"abc"}""")]
    [InlineData("price", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1.234"}""")]
    [InlineData("price", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"-1"}""")]
    [InlineData("price", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"100000000"}""")]
    [InlineData("price", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":""}""")]
    [InlineData("price", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":" 1"}""")]
    [InlineData("price", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1."}""")]
    [InlineData("price", """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":45}""")]
    public async Task Invalid_create_returns_400_naming_the_field_and_creates_nothing(string field, string json)
    {
        var (owner, businessId) = await BusinessAsync();

        var error = await Json.ErrorAsync(await Create(owner, businessId, json), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
        Assert.Equal(0, await ServiceCountAsync(businessId));
    }

    [Fact]
    public async Task Name_or_description_too_long_after_trimming_is_refused()
    {
        var (owner, businessId) = await BusinessAsync();

        var longName = await Json.ErrorAsync(await Create(owner, businessId,
            $$"""{"name":"{{new string('n', 201)}}","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1"}"""),
            HttpStatusCode.BadRequest, "ValidationFailed");
        var longDescription = await Json.ErrorAsync(await Create(owner, businessId,
            $$"""{"name":"Cut","description":"{{new string('d', 2001)}}","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1"}"""),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("name", Json.FieldNames(longName));
        Assert.Contains("description", Json.FieldNames(longDescription));
        Assert.Equal(0, await ServiceCountAsync(businessId));
    }

    [Fact]
    public async Task Unknown_field_on_create_returns_400()
    {
        var (owner, businessId) = await BusinessAsync();

        await Json.ErrorAsync(await Create(owner, businessId,
            """{"name":"Cut","durationMinutes":60,"bufferBeforeMinutes":0,"bufferAfterMinutes":0,"price":"1","staff":[]}"""),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Equal(0, await ServiceCountAsync(businessId));
    }

    private async Task AssertCreateForbidden(Guid callerId, Guid businessId)
    {
        await Json.ErrorAsync(await Create(callerId, businessId, ValidBody), HttpStatusCode.Forbidden, "Forbidden");
        Assert.Equal(0, await ServiceCountAsync(businessId));
    }

    [Fact]
    public async Task Only_the_owner_may_create_a_service()
    {
        var (_, businessId) = await BusinessAsync();
        var staffUser = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertStaffAsync(businessId, staffUser);
        var (otherOwner, _) = await BusinessAsync();

        await AssertCreateForbidden(staffUser, businessId);
        await AssertCreateForbidden(await fixture.Sql.InsertUserAsync(), businessId);
        await AssertCreateForbidden(otherOwner, businessId);
        await AssertCreateForbidden(await fixture.Sql.InsertAdminUserAsync(), businessId);
    }

    [Fact]
    public async Task Creating_in_an_unknown_business_is_404()
    {
        var (owner, _) = await BusinessAsync();

        await Json.ErrorAsync(await Create(owner, Guid.NewGuid(), ValidBody), HttpStatusCode.NotFound, "NotFound");
    }

    // ------------------------------------------------------------ Get

    [Fact]
    public async Task Any_signed_in_user_gets_a_service_with_its_assigned_staff_by_name()
    {
        var (_, businessId) = await BusinessAsync();
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);
        var zoe = await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "zoe"));
        var adam = await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "Adam"));
        var removed = await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "Bea"));
        await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "Not assigned"));
        foreach (var staff in new[] { zoe, adam, removed }) await fixture.Sql.AssignStaffToServiceAsync(staff, serviceId);
        await fixture.Sql.RemoveStaffAsync(removed);

        var body = await Json.ReadAsync(await Get(await fixture.Sql.InsertUserAsync(), serviceId), HttpStatusCode.OK);

        Assert.Equal(serviceId, body.GetProperty("id").GetGuid());
        AssertService(body, businessId, "Haircut", null, duration: 60, bufferBefore: 15, bufferAfter: 15, price: "45.00");
        var staffList = body.GetProperty("staff").EnumerateArray().ToList();
        Assert.Equal([adam, zoe], staffList.Select(s => s.GetProperty("staffId").GetGuid()));
        Assert.Equal(["Adam", "zoe"], staffList.Select(s => s.GetProperty("name").GetString()));
        Assert.All(staffList, s => Assert.Equal(2, s.EnumerateObject().Count()));
    }

    [Fact]
    public async Task Staff_with_the_same_name_are_ordered_by_staff_id()
    {
        var (_, businessId) = await BusinessAsync();
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);
        var staffIds = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var staffId = await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "Sam"));
            await fixture.Sql.AssignStaffToServiceAsync(staffId, serviceId);
            staffIds.Add(staffId);
        }
        var expected = (await Task.WhenAll(staffIds.Select(async id => (Id: id, Text: Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT id FROM staff WHERE id = @id", new { id }))!))))
            .OrderBy(x => x.Text, StringComparer.Ordinal).Select(x => x.Id);

        var body = await Json.ReadAsync(await Get(await fixture.Sql.InsertUserAsync(), serviceId), HttpStatusCode.OK);

        Assert.Equal(expected, body.GetProperty("staff").EnumerateArray().Select(s => s.GetProperty("staffId").GetGuid()));
    }

    [Fact]
    public async Task Getting_an_unknown_service_is_404()
    {
        await Json.ErrorAsync(await Get(await fixture.Sql.InsertUserAsync(), Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Getting_without_a_token_is_401()
    {
        var (_, businessId) = await BusinessAsync();

        await Json.ErrorAsync(await fixture.Api.CreateClient().GetAsync(PathFor(await fixture.Sql.InsertServiceAsync(businessId))),
            HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    // ------------------------------------------------------------ Update

    [Fact]
    public async Task Owner_changes_one_field_and_the_others_keep_their_values()
    {
        var (owner, businessId) = await BusinessAsync();
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);

        var body = await Json.ReadAsync(await Patch(owner, serviceId, """{"price":"50.5"}"""), HttpStatusCode.OK);

        AssertService(body, businessId, "Haircut", null, duration: 60, bufferBefore: 15, bufferAfter: 15, price: "50.50");
        Assert.Empty(body.GetProperty("staff").EnumerateArray());
        Assert.Equal($"{businessId}|Haircut|<null>|60|15|15|50.50", await StoredAsync(serviceId));
    }

    [Fact]
    public async Task Owner_changes_every_field_at_once()
    {
        var (owner, businessId) = await BusinessAsync();
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);

        var body = await Json.ReadAsync(await Patch(owner, serviceId,
            """{"name":" Colour ","description":" Full head ","durationMinutes":90,"bufferBeforeMinutes":0,"bufferAfterMinutes":20,"price":"80"}"""),
            HttpStatusCode.OK);

        AssertService(body, businessId, "Colour", "Full head", duration: 90, bufferBefore: 0, bufferAfter: 20, price: "80.00");
        Assert.Equal($"{businessId}|Colour|Full head|90|0|20|80.00", await StoredAsync(serviceId));
    }

    [Theory]
    [InlineData("""{"description":null}""")]
    [InlineData("""{"description":"  "}""")]
    public async Task Description_is_cleared_with_null_or_blank(string json)
    {
        var (owner, businessId) = await BusinessAsync();
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);
        await Json.ReadAsync(await Patch(owner, serviceId, """{"description":"Something"}"""), HttpStatusCode.OK);

        var body = await Json.ReadAsync(await Patch(owner, serviceId, json), HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("description").ValueKind);
        Assert.Equal($"{businessId}|Haircut|<null>|60|15|15|45.00", await StoredAsync(serviceId));
    }

    [Fact]
    public async Task Update_response_lists_the_assigned_staff()
    {
        var (owner, businessId) = await BusinessAsync();
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);
        var staffId = await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "Ann"));
        await fixture.Sql.AssignStaffToServiceAsync(staffId, serviceId);

        var body = await Json.ReadAsync(await Patch(owner, serviceId, """{"name":"Cut"}"""), HttpStatusCode.OK);

        Assert.Equal(staffId, Assert.Single(body.GetProperty("staff").EnumerateArray()).GetProperty("staffId").GetGuid());
    }

    [Fact]
    public async Task Duration_and_buffer_changes_never_rewrite_existing_bookings() // R1, ADR 0004
    {
        var booking = await fixture.Sql.InsertBookingFixtureAsync();
        var owner = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT owner_user_id FROM businesses WHERE id = @id", new { id = booking.BusinessId }))!);
        var bookingId = await fixture.Sql.InsertBookingAsync(booking);
        const string bookingRow =
            "SELECT CONCAT_WS('|', start_utc, end_utc, blocked_start_utc, blocked_end_utc, status, version) FROM bookings WHERE id = @bookingId";
        var before = await fixture.Sql.ScalarAsync(bookingRow, new { bookingId });

        await Json.ReadAsync(await Patch(owner, booking.ServiceId,
            """{"durationMinutes":120,"bufferBeforeMinutes":30,"bufferAfterMinutes":30}"""), HttpStatusCode.OK);

        Assert.Equal(before, await fixture.Sql.ScalarAsync(bookingRow, new { bookingId }));
    }

    [Fact]
    public async Task Update_response_shows_a_field_another_change_committed_meanwhile()
    {
        var (owner, businessId) = await BusinessAsync();
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);
        Task<HttpResponseMessage> patch;
        await using (var held = await fixture.Sql.LockRowAsync("services", serviceId))
        {
            patch = Patch(owner, serviceId, """{"price":"50"}""");
            await fixture.Sql.WaitForLockWaitAsync("services");
            await held.ExecuteAsync("UPDATE services SET name = 'Renamed' WHERE id = @serviceId", new { serviceId });
            await held.CommitAsync();
        }

        var body = await Json.ReadAsync(await patch, HttpStatusCode.OK);

        AssertService(body, businessId, "Renamed", null, duration: 60, bufferBefore: 15, bufferAfter: 15, price: "50.00");
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("""{"name":null}""", "name")]
    [InlineData("""{"durationMinutes":null}""", "durationMinutes")]
    [InlineData("""{"bufferBeforeMinutes":null}""", "bufferBeforeMinutes")]
    [InlineData("""{"bufferAfterMinutes":null}""", "bufferAfterMinutes")]
    [InlineData("""{"price":null}""", "price")]
    [InlineData("""{"durationMinutes":1441}""", "durationMinutes")]
    [InlineData("""{"price":"1.234"}""", "price")]
    [InlineData("""{"name":"  "}""", "name")]
    [InlineData("""{"businessId":"00000000-0000-0000-0000-000000000001"}""", null)]
    public async Task Invalid_update_returns_400_and_changes_nothing(string json, string? field)
    {
        var (owner, businessId) = await BusinessAsync();
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);
        var before = await StoredAsync(serviceId);

        var error = await Json.ErrorAsync(await Patch(owner, serviceId, json), HttpStatusCode.BadRequest, "ValidationFailed");

        if (field is not null) Assert.Contains(field, Json.FieldNames(error));
        Assert.Equal(before, await StoredAsync(serviceId));
    }

    [Fact]
    public async Task Only_the_owner_may_change_a_service()
    {
        var (_, businessId) = await BusinessAsync();
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);
        var staffUser = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertStaffAsync(businessId, staffUser);
        var (otherOwner, _) = await BusinessAsync();
        var before = await StoredAsync(serviceId);

        foreach (var caller in new[] { staffUser, await fixture.Sql.InsertUserAsync(), otherOwner, await fixture.Sql.InsertAdminUserAsync() })
        {
            await Json.ErrorAsync(await Patch(caller, serviceId, """{"price":"1"}"""), HttpStatusCode.Forbidden, "Forbidden");
        }

        Assert.Equal(before, await StoredAsync(serviceId));
    }

    [Fact]
    public async Task Changing_an_unknown_service_is_404()
    {
        var (owner, _) = await BusinessAsync();

        await Json.ErrorAsync(await Patch(owner, Guid.NewGuid(), """{"price":"1"}"""), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Service_changes_emit_no_event()
    {
        var (owner, businessId) = await BusinessAsync();
        var before = Convert.ToInt64(await fixture.Sql.ScalarAsync("SELECT COUNT(*) FROM outbox"));

        var created = await Json.ReadAsync(await Create(owner, businessId, ValidBody), HttpStatusCode.Created);
        await Json.ReadAsync(await Patch(owner, created.GetProperty("id").GetGuid(), """{"price":"1"}"""), HttpStatusCode.OK);

        Assert.Equal(before, Convert.ToInt64(await fixture.Sql.ScalarAsync("SELECT COUNT(*) FROM outbox")));
    }
}
