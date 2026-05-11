using Microsoft.AspNetCore.Http.Json;
using Models.Configuration;

using Abstractions;
using Logic;
using Models;

using SealCode;

using SealCode.Routing;
using SealCode.Security;

using Transport.Models;
using Transport.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ApplicationConfiguration>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<ILanguageValidator, ConfigurationLanguageValidator>();

builder.Services.AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 1024 * 1024;
        options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(15);
    })
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = null;
        options.PayloadSerializerOptions.Converters.Add(new ShortGuidJsonConverter());
    });

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.Converters.Add(new ShortGuidJsonConverter());
});
builder.Services.Configure<RouteOptions>(options => options.ConstraintMap["ShortGuid"] = typeof(ShortGuidRouteConstraint));

builder.Services.AddSingleton<IRoomRegistry, RoomRegistry>();
builder.Services.AddSingleton<IRoomNotifier, SignalRRoomNotifier>();
builder.Services.AddSingleton<IRoomManager, RoomManager>();
builder.Services.AddSingleton<IPlatformAccessValidator, PlatformAccessValidator>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(sp =>
{
    var accessor = sp.GetRequiredService<IHttpContextAccessor>();
    return accessor.HttpContext ?? throw new InvalidOperationException("HttpContext is not available.");
});
builder.Services.AddScoped<IAdminUserManager, AdminUserManager>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", (IWebHostEnvironment env) =>
{
    var path = Path.Combine(env.WebRootPath, "index.html");
    return Results.File(path, "text/html");
});

app.MapGet("/admin/login", (IWebHostEnvironment env) =>
{
    var path = Path.Combine(env.WebRootPath, "admin-login.html");
    return Results.File(path, "text/html");
});

app.MapPost("/admin/login",
            async (IAdminUserManager adminUserManager,
                   CancellationToken cancellationToken) =>
{
    return await adminUserManager.TrySetCurrentUserAsync(cancellationToken).ConfigureAwait(false)
        ? Results.Redirect("/admin")
        : Results.Redirect("/admin/login?error=1");
});

app.MapPost("/admin/logout", (IAdminUserManager adminUserManager) =>
{
    adminUserManager.ClearCurrentAdminUser();
    return Results.Redirect("/admin/login");
});

app.MapGet("/admin", (IWebHostEnvironment env, IAdminUserManager adminUserManager) =>
{
    if (!adminUserManager.IsAdmin())
    {
        return Results.Redirect("/admin/login");
    }

    var path = Path.Combine(env.WebRootPath, "admin.html");
    return Results.File(path, "text/html");
});

app.MapGet("/admin/rooms", (IRoomManager roomManager, IAdminUserManager adminUserManager) =>
{
    if (!adminUserManager.TryGetAdminUser(out var adminUser))
    {
        return Results.Unauthorized();
    }

    var rooms = roomManager.GetRoomsSnapshot(adminUser)
        .Select(room => new RoomSummary(
            room.RoomId.Value,
            room.Name.Value,
            room.Language.Value,
            room.UsersCount,
            room.LastUpdatedUtc,
            room.CreatedBy.Name,
            room.CanDelete))
        .ToArray();

    return Results.Json(rooms);
});

app.MapGet("/languages", (ILanguageValidator validator)
    => Results.Json(validator.Languages));

app.MapPost("/admin/rooms",
            async (HttpContext context,
                   IRoomManager roomManager,
                   IAdminUserManager adminUserManager,
                   CancellationToken cancellationToken) =>
{
    if (!adminUserManager.TryGetAdminUser(out var adminUser))
    {
        return Results.Unauthorized();
    }

    var payload = await context.Request.ReadFromJsonAsync<CreateRoomRequest>(cancellationToken).ConfigureAwait(false);
    if (payload is null || string.IsNullOrWhiteSpace(payload.Name))
    {
        return Results.BadRequest(new { error = "Name is required" });
    }

    var language = new RoomLanguage(payload.Language ?? "csharp");
    var name = new RoomName(payload.Name);
    var room = roomManager.CreateRoom(name, language, adminUser);
    return Results.Json(new
    {
        RoomId = room.RoomId.Value,
        Name = room.Name.Value,
        Language = room.Language.Value,
        CreatedBy = room.CreatedBy.Name
    });
});

app.MapDelete("/admin/rooms/{roomId:ShortGuid}",
            async (RoomId roomId,
                   IRoomManager roomManager,
                   IAdminUserManager adminUserManager,
                   CancellationToken cancellationToken) =>
{
    if (!adminUserManager.TryGetAdminUser(out var adminUser))
    {
        return Results.Unauthorized();
    }

    var result = await roomManager.DeleteRoomAsync(roomId, adminUser, cancellationToken).ConfigureAwait(false);
    return result switch
    {
        RoomDeletionResult.Deleted => Results.Ok(),
        RoomDeletionResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        RoomDeletionResult.NotFound => Results.NotFound(),
        _ => throw new NotImplementedException()
    };
});

app.MapPost("/platform/rooms",
            async (HttpContext context,
                   IRoomManager roomManager,
                   IRoomRegistry registry,
                   IPlatformAccessValidator accessValidator,
                   CancellationToken cancellationToken) =>
{
    if (!accessValidator.IsServiceRequest(context))
    {
        return Results.Unauthorized();
    }

    var payload = await context.Request.ReadFromJsonAsync<PlatformRoomRequest>(cancellationToken).ConfigureAwait(false);
    if (payload is null || string.IsNullOrWhiteSpace(payload.ExternalId) || string.IsNullOrWhiteSpace(payload.Name))
    {
        return Results.BadRequest(new { error = "externalId and name are required" });
    }

    if (RoomId.TryParse(payload.CurrentRoomId, out var currentRoomId) && registry.TryGetRoom(currentRoomId, out var currentRoom))
    {
        return Results.Json(new
        {
            roomId = currentRoom.RoomId.Value,
            name = currentRoom.Name.Value,
            language = currentRoom.Language.Value,
            reused = true
        });
    }

    var language = new RoomLanguage(string.IsNullOrWhiteSpace(payload.Language) ? "javascript" : payload.Language);
    var createdBy = new AdminUser(string.IsNullOrWhiteSpace(payload.CreatedBy) ? "platform" : payload.CreatedBy);
    var room = roomManager.CreateRoom(
        new RoomName(payload.Name),
        language,
        createdBy,
        new RoomText(payload.InitialText ?? string.Empty));

    return Results.Json(new
    {
        roomId = room.RoomId.Value,
        name = room.Name.Value,
        language = room.Language.Value,
        reused = false
    });
});

app.MapGet("/room/{roomId:ShortGuid}", (HttpContext context,
                                         RoomId roomId,
                                         IRoomRegistry registry,
                                         IWebHostEnvironment env,
                                         IPlatformAccessValidator accessValidator) =>
{
    if (!registry.TryGetRoom(roomId, out _))
    {
        return Results.NotFound("Room not found");
    }

    var token = context.Request.Query["access_token"].ToString();
    if (!accessValidator.TryValidateRoomToken(token, roomId.Value, out _))
    {
        return Results.Unauthorized();
    }

    var path = Path.Combine(env.WebRootPath, "room.html");
    return Results.File(path, "text/html");
});

app.MapGet("/health", () => Results.Ok("ok"));

app.MapHub<RoomHub>("/roomHub");

app.Run();

internal sealed record PlatformRoomRequest(
    string? ExternalId,
    string? CurrentRoomId,
    string? Name,
    string? Language,
    string? InitialText,
    string? CreatedBy);
