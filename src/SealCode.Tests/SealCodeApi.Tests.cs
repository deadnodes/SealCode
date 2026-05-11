using FluentAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Models;

using Transport.Models;

namespace SealCode.Tests;

public sealed class SealCodeApiTests
{
    [Fact(DisplayName = "GET /health returns ok")]
    [Trait("Category", "Integration")]
    public async Task GetHealthReturnsOk()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var payload = await response.Content.ReadFromJsonAsync<string>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        payload.Should().Be("ok");
    }

    [Fact(DisplayName = "GET /languages returns configured languages")]
    [Trait("Category", "Integration")]
    public async Task GetLanguagesReturnsConfiguredLanguages()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/languages");
        var payload = await response.Content.ReadFromJsonAsync<string[]>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        payload.Should().NotBeNull();
        payload!.Should().Contain(["csharp", "sql"]);
    }

    [Fact(DisplayName = "GET /admin/rooms returns unauthorized without cookie")]
    [Trait("Category", "Integration")]
    public async Task GetAdminRoomsWithoutCookieReturnsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/rooms");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "POST /admin/rooms returns unauthorized without cookie")]
    [Trait("Category", "Integration")]
    public async Task CreateRoomWithoutCookieReturnsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/admin/rooms", new { Name = "Room", Language = "csharp" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "POST /admin/login sets cookie and allows admin room creation")]
    [Trait("Category", "Integration")]
    public async Task LoginThenCreateRoomReturnsRoomDetails()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var loginContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "Admin",
            ["password"] = "pass1"
        });

        var loginResponse = await client.PostAsync("/admin/login", loginContent);

        loginResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        loginResponse.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue();
        var setCookie = setCookies!.First();
        var cookie = setCookie.Split(';', 2)[0];

        client.DefaultRequestHeaders.Add("Cookie", cookie);

        var createResponse = await client.PostAsJsonAsync("/admin/rooms", new { Name = "Room", Language = "sql" });
        var payload = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        payload.GetProperty("RoomId").GetString().Should().NotBeNullOrWhiteSpace();
        payload.GetProperty("Name").GetString().Should().Be("Room");
        payload.GetProperty("Language").GetString().Should().Be("sql");
        payload.GetProperty("CreatedBy").GetString().Should().Be("Admin");
    }

    [Fact(DisplayName = "GET /room/{id} returns not found for missing room")]
    [Trait("Category", "Integration")]
    public async Task GetRoomReturnsNotFoundForMissingRoom()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var missingRoomId = RoomId.New().Value;
        var response = await client.GetAsync($"/room/{missingRoomId}");
        var payload = await response.Content.ReadFromJsonAsync<string>();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        payload.Should().Be("Room not found");
    }

    [Fact(DisplayName = "GET /room/{id} allows standalone admin rooms without platform token")]
    [Trait("Category", "Integration")]
    public async Task GetRoomAllowsStandaloneAdminRoomsWithoutPlatformToken()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var cookie = await LoginAsAdminAsync(client);
        var roomId = await CreateRoomAsync(client, cookie);

        var response = await client.GetAsync($"/room/{roomId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
    }

    [Fact(DisplayName = "GET /room/{id} requires token for platform rooms")]
    [Trait("Category", "Integration")]
    public async Task GetRoomRequiresTokenForPlatformRooms()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var roomId = await CreatePlatformRoomAsync(client);

        var response = await client.GetAsync($"/room/{roomId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "GET /room/{id} allows platform rooms with valid token")]
    [Trait("Category", "Integration")]
    public async Task GetRoomAllowsPlatformRoomsWithValidToken()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var roomId = await CreatePlatformRoomAsync(client);
        var token = CreatePlatformRoomToken(roomId);

        var response = await client.GetAsync($"/room/{roomId}?access_token={Uri.EscapeDataString(token)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
    }

    [Fact(DisplayName = "SignalR /roomHub allows joining a room")]
    [Trait("Category", "Integration")]
    public async Task JoinRoomOverSignalRReturnsRoomState()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var cookie = await LoginAsAdminAsync(client);
        var roomId = await CreateRoomAsync(client, cookie);

        var cookieContainer = new CookieContainer();
        cookieContainer.Add(client.BaseAddress!, cookie);

        var hubUrl = new Uri(client.BaseAddress!, "/roomHub");
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Cookies = cookieContainer;
            })
            .Build();

        await connection.StartAsync();

        var result = await connection.InvokeAsync<JoinRoomResult>("JoinRoom", roomId, "Alice");

        result.Name.Should().Be("Room");
        result.Language.Should().Be("sql");
        result.Text.Should().Be(string.Empty);
        result.Version.Should().BeGreaterThanOrEqualTo(1);
        result.Users.Should().ContainSingle().Which.Should().Be("Alice");
        result.CreatedBy.Should().Be("Admin");
        result.YjsState.Should().BeNull();

        await connection.DisposeAsync();
    }

    [Fact(DisplayName = "SignalR /roomHub broadcasts room events")]
    [Trait("Category", "Integration")]
    public async Task SignalRRoomHubBroadcastsRoomEvents()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var cookie = await LoginAsAdminAsync(client);
        var roomId = await CreateRoomAsync(client, cookie);

        await using var alice = await CreateConnectionAsync(factory, client.BaseAddress!, cookie);
        await using var bob = await CreateConnectionAsync(factory, client.BaseAddress!, cookie);

        var bobJoined = new TaskCompletionSource<(string name, string[] users)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bobJoinedSubscription = bob.On<string, string[]>("UserJoined", (name, users) =>
            bobJoined.TrySetResult((name, users)));

        var bobJoin = await bob.InvokeAsync<JoinRoomResult>("JoinRoom", roomId, "Bob");
        bobJoin.Users.Should().ContainSingle().Which.Should().Be("Bob");

        var aliceJoin = await alice.InvokeAsync<JoinRoomResult>("JoinRoom", roomId, "Alice");
        aliceJoin.Users.Should().Contain(["Alice", "Bob"]);

        var joinedPayload = await AwaitWithTimeout(bobJoined.Task);
        joinedPayload.name.Should().Be("Alice");
        joinedPayload.users.Should().Contain(["Alice", "Bob"]);

        var textUpdated = new TaskCompletionSource<(string text, int version, string author)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (bob.On<string, int, string>("TextUpdated", (text, version, author) =>
            textUpdated.TrySetResult((text, version, author))))
        {
            await alice.InvokeAsync("UpdateText", roomId, "new text", 1);
            var payload = await AwaitWithTimeout(textUpdated.Task);
            payload.author.Should().Be("Alice");
            payload.text.Should().Be("new text");
        }

        var yjsUpdated = new TaskCompletionSource<(string update, int version, string author, string state)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (bob.On<string, int, string, string>("YjsUpdated", (update, version, author, state) =>
            yjsUpdated.TrySetResult((update, version, author, state))))
        {
            var update = Convert.ToBase64String(new byte[] { 1, 2, 3 });
            var state = Convert.ToBase64String(new byte[] { 5, 6, 7 });
            await alice.InvokeAsync("UpdateYjs", roomId, update, state, "snapshot");

            var payload = await AwaitWithTimeout(yjsUpdated.Task);
            payload.update.Should().Be(update);
            payload.state.Should().Be(state);
            payload.author.Should().Be("Alice");
            payload.version.Should().BeGreaterThanOrEqualTo(2);
        }

        var languageUpdated = new TaskCompletionSource<(string language, int version)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (bob.On<string, int>("LanguageUpdated", (language, version) =>
            languageUpdated.TrySetResult((language, version))))
        {
            await alice.InvokeAsync("SetLanguage", roomId, "sql");
            var payload = await AwaitWithTimeout(languageUpdated.Task);
            payload.language.Should().Be("sql");
            payload.version.Should().BeGreaterThanOrEqualTo(2);
        }

        var cursorUpdated = new TaskCompletionSource<(string author, int position)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (bob.On<string, int>("CursorUpdated", (author, position) =>
            cursorUpdated.TrySetResult((author, position))))
        {
            await alice.InvokeAsync("UpdateCursor", roomId, 42);
            var payload = await AwaitWithTimeout(cursorUpdated.Task);
            payload.author.Should().Be("Alice");
            payload.position.Should().Be(42);
        }

        var selectionUpdated = new TaskCompletionSource<(string author, bool isMultiLine)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (bob.On<string, bool>("UserSelection", (author, isMultiLine) =>
            selectionUpdated.TrySetResult((author, isMultiLine))))
        {
            await alice.InvokeAsync("UpdateSelection", roomId, true);
            var payload = await AwaitWithTimeout(selectionUpdated.Task);
            payload.author.Should().Be("Alice");
            payload.isMultiLine.Should().BeTrue();
        }

        var copyUpdated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (bob.On<string>("UserCopy", author => copyUpdated.TrySetResult(author)))
        {
            await alice.InvokeAsync("UpdateCopy", roomId);
            var author = await AwaitWithTimeout(copyUpdated.Task);
            author.Should().Be("Alice");
        }

        var leftUpdated = new TaskCompletionSource<(string author, string[] users)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (bob.On<string, string[]>("UserLeft", (author, users) =>
            leftUpdated.TrySetResult((author, users))))
        {
            await alice.InvokeAsync("LeaveRoom", roomId);
            var payload = await AwaitWithTimeout(leftUpdated.Task);
            payload.author.Should().Be("Alice");
        }
    }

    [Fact(DisplayName = "DELETE /admin/rooms forbids non-super admins deleting others rooms")]
    [Trait("Category", "Integration")]
    public async Task DeleteRoomForOtherAdminReturnsForbidden()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var rootCookie = await LoginAsync(client, "Root", "pass2");
        var roomId = await CreateRoomAsync(client, rootCookie);

        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{rootCookie.Name}={rootCookie.Value}");

        var roomsResponse = await client.GetAsync("/admin/rooms");
        roomsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var roomsPayload = await roomsResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        roomsPayload.Should().NotBeNull();
        var createdRoom = roomsPayload!.First(room => room.GetProperty("RoomId").GetString() == roomId);
        createdRoom.GetProperty("CreatedBy").GetString().Should().Be("Root");

        var adminCookie = await LoginAsync(client, "Admin", "pass1");
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{adminCookie.Name}={adminCookie.Value}");

        var forbiddenResponse = await client.DeleteAsync($"/admin/rooms/{roomId}");

        forbiddenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{rootCookie.Name}={rootCookie.Value}");

        var deleteResponse = await client.DeleteAsync($"/admin/rooms/{roomId}");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static SealCodeAppFactory CreateFactory() => new();

    private static Task<Cookie> LoginAsAdminAsync(HttpClient client)
        => LoginAsync(client, "Admin", "pass1");

    private static async Task<Cookie> LoginAsync(HttpClient client, string name, string password)
    {
        using var loginContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = name,
            ["password"] = password
        });

        var loginResponse = await client.PostAsync("/admin/login", loginContent);

        loginResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        loginResponse.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue();

        var rawCookie = setCookies!.First().Split(';', 2)[0];
        var parts = rawCookie.Split('=', 2);
        return new Cookie(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }

    private static async Task<string> CreateRoomAsync(HttpClient client, Cookie cookie)
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{cookie.Name}={cookie.Value}");

        var response = await client.PostAsJsonAsync("/admin/rooms", new { Name = "Room", Language = "sql" });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return payload.GetProperty("RoomId").GetString()!;
    }

    private static async Task<string> CreatePlatformRoomAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/platform/rooms")
        {
            Content = JsonContent.Create(new
            {
                ExternalId = "assignment-1",
                Name = "Platform Room",
                Language = "python",
                InitialText = "print('hello')",
                CreatedBy = "platform"
            })
        };
        request.Headers.Add("X-Service-Token", PlatformServiceToken);

        var response = await client.SendAsync(request);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return payload.GetProperty("roomId").GetString()!;
    }

    private static string CreatePlatformRoomToken(string roomId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            room_id = roomId,
            assignment_id = "assignment-1",
            name = "Candidate",
            sub = "anon:candidate",
            role = "candidate",
            exp = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()
        });
        var body = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signature = Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(PlatformSigningSecret), Encoding.ASCII.GetBytes(body)));
        return $"{body}.{signature}";
    }

    private static string Base64UrlEncode(byte[] input)
        => Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<HubConnection> CreateConnectionAsync(WebApplicationFactory<Program> factory, Uri baseAddress, Cookie cookie)
    {
        var cookieContainer = new CookieContainer();
        cookieContainer.Add(baseAddress, cookie);

        var hubUrl = new Uri(baseAddress, "/roomHub");
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Cookies = cookieContainer;
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }

    private static async Task<T> AwaitWithTimeout<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(task);
        return await task;
    }

    private sealed class SealCodeAppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["AdminUsers:0:Name"] = "Admin",
                    ["AdminUsers:0:Password"] = "pass1",
                    ["AdminUsers:0:IsSuperAdmin"] = "false",
                    ["AdminUsers:1:Name"] = "Root",
                    ["AdminUsers:1:Password"] = "pass2",
                    ["AdminUsers:1:IsSuperAdmin"] = "true",
                    ["Languages:0"] = "csharp",
                    ["Languages:1"] = "sql",
                    ["Languages:2"] = "python",
                    ["MaxUsersPerRoom"] = "3",
                    ["PlatformServiceToken"] = PlatformServiceToken,
                    ["PlatformSigningSecret"] = PlatformSigningSecret
                };

                config.AddInMemoryCollection(settings);
            });
        }
    }

    private const string PlatformServiceToken = "test-service-token";
    private const string PlatformSigningSecret = "test-signing-secret";
}
