using Abstractions;

using Microsoft.AspNetCore.SignalR;

using Models;
using Models.Exceptions;

using Transport.Models;

namespace SealCode;

/// <summary>
/// SignalR hub for room interactions.
/// </summary>
#pragma warning disable CA1515 // Need for testing in mocks
public sealed class RoomHub : Hub
#pragma warning restore CA1515 
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoomHub"/> class.
    /// </summary>
    /// <param name="roomManager">The room manager.</param>
    /// <param name="languageValidator">The language validator.</param>
    /// <param name="accessValidator">The platform access validator.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when a dependency is null.</exception>
    public RoomHub(
        IRoomManager roomManager,
        ILanguageValidator languageValidator,
        IPlatformAccessValidator accessValidator,
        ILogger<RoomHub> logger)
    {
        ArgumentNullException.ThrowIfNull(roomManager);
        ArgumentNullException.ThrowIfNull(languageValidator);
        ArgumentNullException.ThrowIfNull(accessValidator);
        ArgumentNullException.ThrowIfNull(logger);

        _roomManager = roomManager;
        _languageValidator = languageValidator;
        _accessValidator = accessValidator;
        _logger = logger;
    }

    /// <summary>
    /// Joins the specified room with a display name.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="displayName">The user display name.</param>
    /// <returns>The join result including the initial room state.</returns>
    /// <exception cref="HubException">Thrown when the room or inputs are invalid.</exception>
    [HubMethodName("JoinRoom")]
    public async Task<JoinRoomResult> JoinRoomAsync(string roomId, string displayName)
    {
        var parsedRoomId = ParseRoomIdOrThrow(roomId);
        string? token = null;
        try
        {
            token = Context.GetHttpContext()?.Request.Query["access_token"].ToString();
        }
        catch (InvalidOperationException)
        {
            token = null;
        }
        if (!_accessValidator.TryValidateRoomToken(token, parsedRoomId.Value, out var access))
        {
            throw new HubException("Unauthorized");
        }

        displayName = (displayName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = access.Name;
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new HubException("Display name required");
        }

        var connectionId = new ConnectionId(Context.ConnectionId);
        RoomState room;

        try
        {
            var platformDisplayName = !string.IsNullOrWhiteSpace(access.Name)
                && string.Equals(displayName, access.Name.Trim(), StringComparison.OrdinalIgnoreCase);
            room = RegisterUserInRoom(parsedRoomId, connectionId, displayName, platformDisplayName, out displayName);
        }
        catch (RoomNotFoundException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (AddRoomUserException ex)
        {
            throw new HubException(ex.Message);
        }

        var joinResult = JoinRoomResult.From(room);

        Context.Items["roomId"] = parsedRoomId.Value;
        Context.Items["displayName"] = displayName;

        var cancellationToken = Context.ConnectionAborted;

        await Groups.AddToGroupAsync(connectionId.Value, parsedRoomId.Value, cancellationToken).ConfigureAwait(false);
        await Clients.GroupExcept(parsedRoomId.Value, connectionId.Value)
            .SendAsync("UserJoined", displayName, joinResult.Users, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("User joined {RoomId} ({Name}) as {RoomUser}", parsedRoomId.Value, room.Name.Value, displayName);

        return joinResult;
    }

    private RoomState RegisterUserInRoom(
        RoomId parsedRoomId,
        ConnectionId connectionId,
        string displayName,
        bool allowGeneratedSuffix,
        out string joinedDisplayName)
    {
        joinedDisplayName = displayName;

        try
        {
            return _roomManager.RegisterUserInRoom(
                parsedRoomId,
                connectionId,
                new RoomUser(displayName));
        }
        catch (AddRoomUserException ex) when (allowGeneratedSuffix && IsDisplayNameConflict(ex))
        {
            for (var suffix = 2; suffix <= MAXGENERATEDDISPLAYNAMESUFFIX; suffix++)
            {
                var generatedDisplayName = $"{displayName} ({suffix})";
                try
                {
                    var room = _roomManager.RegisterUserInRoom(
                        parsedRoomId,
                        connectionId,
                        new RoomUser(generatedDisplayName));
                    joinedDisplayName = generatedDisplayName;
                    return room;
                }
                catch (AddRoomUserException retryEx) when (IsDisplayNameConflict(retryEx))
                {
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Updates the room text.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="newText">The new text.</param>
    /// <param name="clientVersion">The client version.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="HubException">Thrown when inputs are invalid or the room is not found.</exception>
    [HubMethodName("UpdateText")]
    public async Task UpdateTextAsync(string roomId, string newText, int clientVersion)
    {
        var parsedRoomId = ParseRoomIdOrThrow(roomId);

        if (clientVersion < 0)
        {
            throw new HubException("Invalid client version");
        }

        if (!_roomManager.TryGetRoom(parsedRoomId, out var room))
        {
            throw new HubException("Room not found");
        }

        var text = newText ?? string.Empty;
        var newVersion = room.UpdateText(new(text), DateTimeOffset.UtcNow).Value;
        var author = room.TryGetRoomUser(new ConnectionId(Context.ConnectionId), out var user)
            ? user.Value
            : "unknown";

        var cancellationToken = Context.ConnectionAborted;
        await Clients.GroupExcept(parsedRoomId.Value, Context.ConnectionId)
            .SendAsync("TextUpdated", newText ?? string.Empty, newVersion, author, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a Yjs update and stores the latest Yjs state for new joiners.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="updateBase64">The incremental Yjs update (base64).</param>
    /// <param name="stateBase64">The full Yjs document state (base64).</param>
    /// <param name="textSnapshot">The plain text snapshot of the document.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="HubException">Thrown when inputs are invalid or the room is not found.</exception>
    [HubMethodName("UpdateYjs")]
    public async Task UpdateYjsAsync(string roomId, string updateBase64, string stateBase64, string textSnapshot)
    {
        var parsedRoomId = ParseRoomIdOrThrow(roomId);

        if (string.IsNullOrWhiteSpace(updateBase64))
        {
            throw new HubException("Update payload required");
        }

        if (string.IsNullOrWhiteSpace(stateBase64))
        {
            throw new HubException("State payload required");
        }

        if (!_roomManager.TryGetRoom(parsedRoomId, out var room))
        {
            throw new HubException("Room not found");
        }

        byte[] update;
        byte[] state;
        try
        {
            update = Convert.FromBase64String(updateBase64);
            state = Convert.FromBase64String(stateBase64);
        }
        catch (FormatException)
        {
            throw new HubException("Invalid Yjs payload");
        }

        var author = room.TryGetRoomUser(new ConnectionId(Context.ConnectionId), out var user)
            ? user.Value
            : "unknown";

        var text = textSnapshot ?? string.Empty;
        if (!room.TryUpdateYjsState(state, new RoomText(text), DateTimeOffset.UtcNow, out var newVersion))
        {
            return;
        }

        var cancellationToken = Context.ConnectionAborted;
        await Clients.Group(parsedRoomId.Value)
            .SendAsync("YjsUpdated", updateBase64, newVersion.Value, author, stateBase64, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the room language.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="language">The language to set.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="HubException">Thrown when inputs are invalid or the room is not found.</exception>
    [HubMethodName("SetLanguage")]
    public async Task SetLanguageAsync(string roomId, string language)
    {
        var parsedRoomId = ParseRoomIdOrThrow(roomId);

        if (string.IsNullOrWhiteSpace(language))
        {
            throw new HubException("Language required");
        }

        if (!_roomManager.TryGetRoom(parsedRoomId, out var room))
        {
            throw new HubException("Room not found");
        }

        int newVersion;
        RoomLanguage normalized;
        try
        {
            normalized = new RoomLanguage(language);
            newVersion = room.UpdateLanguage(new RoomLanguage(language), DateTimeOffset.UtcNow, _languageValidator).Value;
        }
        catch (ArgumentException)
        {
            throw new HubException("Invalid language");
        }

        var cancellationToken = Context.ConnectionAborted;
        await Clients.Group(parsedRoomId.Value)
            .SendAsync("LanguageUpdated", normalized.Value, newVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the cursor position for the current user.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="position">The cursor position.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="HubException">Thrown when inputs are invalid or the room is not found.</exception>
    [HubMethodName("UpdateCursor")]
    public async Task UpdateCursorAsync(string roomId, int position)
    {
        var parsedRoomId = ParseRoomIdOrThrow(roomId);

        if (position < 0)
        {
            throw new HubException("Invalid cursor position");
        }

        if (!_roomManager.TryGetRoom(parsedRoomId, out var room))
        {
            throw new HubException("Room not found");
        }

        if (!room.TryGetRoomUser(new ConnectionId(Context.ConnectionId), out var user))
        {
            return;
        }
        var author = user.Value;

        var cancellationToken = Context.ConnectionAborted;
        await Clients.GroupExcept(parsedRoomId.Value, Context.ConnectionId)
            .SendAsync("CursorUpdated", author, position, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the selection state for the current user.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="isMultiLine">True if selection spans multiple lines; otherwise false.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="HubException">Thrown when inputs are invalid or the room is not found.</exception>
    [HubMethodName("UpdateSelection")]
    public async Task UpdateSelectionAsync(string roomId, bool isMultiLine)
    {
        var parsedRoomId = ParseRoomIdOrThrow(roomId);

        if (!_roomManager.TryGetRoom(parsedRoomId, out var room))
        {
            throw new HubException("Room not found");
        }

        if (!room.TryGetRoomUser(new ConnectionId(Context.ConnectionId), out var user))
        {
            return;
        }
        var author = user.Value;

        var cancellationToken = Context.ConnectionAborted;
        await Clients.Group(parsedRoomId.Value).SendAsync("UserSelection", author, isMultiLine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a copy-to-clipboard action for the current user.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="HubException">Thrown when inputs are invalid or the room is not found.</exception>
    [HubMethodName("UpdateCopy")]
    public async Task UpdateCopyAsync(string roomId)
    {
        var parsedRoomId = ParseRoomIdOrThrow(roomId);

        if (!_roomManager.TryGetRoom(parsedRoomId, out var room))
        {
            throw new HubException("Room not found");
        }

        if (!room.TryGetRoomUser(new ConnectionId(Context.ConnectionId), out var user))
        {
            return;
        }

        var cancellationToken = Context.ConnectionAborted;
        await Clients.Group(parsedRoomId.Value).SendAsync("UserCopy", user.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Leaves the specified room.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="HubException">Thrown when inputs are invalid.</exception>
    [HubMethodName("LeaveRoom")]
    public async Task LeaveRoomAsync(string roomId)
    {
        var parsedRoomId = ParseRoomIdOrThrow(roomId);

        await RemoveFromRoomAsync(parsedRoomId.Value, new ConnectionId(Context.ConnectionId), notify: true, Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles client disconnection.
    /// </summary>
    /// <param name="exception">The disconnect exception, if any.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("roomId", out var roomIdObj) && roomIdObj is string roomId)
        {
            // ConnectionAborted is already canceled during disconnect; use a non-canceled token for cleanup/notifications.
            await RemoveFromRoomAsync(roomId, new ConnectionId(Context.ConnectionId), notify: true, CancellationToken.None).ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    private async Task RemoveFromRoomAsync(string roomId, ConnectionId connectionId, bool notify, CancellationToken cancellationToken)
    {
        if (!RoomId.TryParse(roomId, out var parsedRoomId))
        {
            return;
        }

        if (!_roomManager.TryGetRoom(parsedRoomId, out var room))
        {
            return;
        }

        string? displayName = null;
        string[] usersSnapshot = [];

        if (room.RemoveUser(connectionId, out var user))
        {
            displayName = user.Value;
            usersSnapshot = [.. room.CreateUsersSnapshot().Select(x => x.Value)];
        }

        await Groups.RemoveFromGroupAsync(connectionId.Value, parsedRoomId.Value, cancellationToken).ConfigureAwait(false);

        if (notify && displayName is not null)
        {
            await Clients.Group(parsedRoomId.Value).SendAsync("UserLeft", displayName, usersSnapshot, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("User left {RoomId} as {RoomUser}", parsedRoomId.Value, displayName);
        }
    }

    private static RoomId ParseRoomIdOrThrow(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new HubException("Room id required");
        }

        if (!RoomId.TryParse(roomId, out var parsedRoomId))
        {
            throw new HubException("Invalid room id");
        }

        return parsedRoomId;
    }

    private readonly IRoomManager _roomManager;
    private readonly ILanguageValidator _languageValidator;
    private readonly IPlatformAccessValidator _accessValidator;
    private readonly ILogger<RoomHub> _logger;
    private const int MAXGENERATEDDISPLAYNAMESUFFIX = 5;

    private static bool IsDisplayNameConflict(AddRoomUserException exception)
        => exception.Message.Contains("Display name already in use", StringComparison.OrdinalIgnoreCase);
}
