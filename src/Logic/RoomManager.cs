using Abstractions;

using Microsoft.Extensions.Options;

using Models;
using Models.Configuration;
using Models.Exceptions;

namespace Logic;

/// <summary>
/// Default implementation of <see cref="IRoomManager"/>.
/// </summary>
public sealed class RoomManager : IRoomManager
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoomManager"/> class.
    /// </summary>
    /// <param name="registry">The room registry.</param>
    /// <param name="settings">The application settings.</param>
    public RoomManager(IRoomRegistry registry, IOptions<ApplicationConfiguration> settings)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(settings);
        _registry = registry;
        _maxUsersPerRoom = Math.Clamp(settings.Value.MaxUsersPerRoom, 1, 5);
    }

    /// <inheritdoc />
    public bool TryGetRoom(RoomId roomId, out RoomState room)
        => _registry.TryGetRoom(roomId, out room);

    /// <inheritdoc />
    public RoomState RegisterUserInRoom(
        RoomId roomId,
        ConnectionId connectionId,
        RoomUser roomUser)
    {
        if (!_registry.TryGetRoom(roomId, out var room))
        {
            throw new RoomNotFoundException();
        }

        room.AddUser(connectionId, roomUser, _maxUsersPerRoom);
        return room;
    }

    /// <inheritdoc />
    public RoomState RegisterPlatformUserInRoom(
        RoomId roomId,
        ConnectionId connectionId,
        RoomUser roomUser,
        string platformSubject)
    {
        if (!_registry.TryGetRoom(roomId, out var room))
        {
            throw new RoomNotFoundException();
        }

        room.AddPlatformUser(connectionId, roomUser, platformSubject, _maxUsersPerRoom);
        return room;
    }

    /// <inheritdoc />
    public RoomView[] GetRoomsSnapshot(AdminUser adminUser)
        => [.. _registry.GetRoomsSnapshot()
            .Select(room => RoomView.From(room, adminUser))
            .OrderBy(room => room.Name.Value, StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc />
    public RoomState CreateRoom(
        RoomName name,
        RoomLanguage language,
        AdminUser adminUser,
        RoomText? initialText = null,
        RoomAccessMode accessMode = RoomAccessMode.Standalone)
        => _registry.CreateRoom(name, language, adminUser, initialText, accessMode);

    /// <inheritdoc />
    public async Task<RoomDeletionResult> DeleteRoomAsync(RoomId roomId, AdminUser adminUser, CancellationToken cancellationToken)
    {
        if (!_registry.TryGetRoom(roomId, out var room))
        {
            return RoomDeletionResult.NotFound;
        }

        if (!room.CanDelete(adminUser))
        {
            return RoomDeletionResult.Forbidden;
        }

        var deleted = await _registry.DeleteRoomAsync(
            roomId,
            new RoomDeletionReason("Room deleted by admin"),
            cancellationToken).ConfigureAwait(false);
        return deleted ? RoomDeletionResult.Deleted : RoomDeletionResult.NotFound;
    }

    private readonly IRoomRegistry _registry;
    private readonly int _maxUsersPerRoom;
}
