using Models;
using Models.Exceptions;

namespace Abstractions;

/// <summary>
/// Defines higher-level room management operations.
/// </summary>
public interface IRoomManager
{
    /// <summary>
    /// Tries to get a room by identifier.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="room">When this method returns, contains the room if found.</param>
    /// <returns><see langword="true"/> when the room exists; otherwise <see langword="false"/>.</returns>
    bool TryGetRoom(RoomId roomId, out RoomState room);

    /// <summary>
    /// Registers a user in the target room.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="connectionId">The user connection identifier.</param>
    /// <param name="roomUser">The room user name.</param>
    /// <returns>The updated room state.</returns>
    /// <exception cref="RoomNotFoundException">Thrown when the room cannot be found.</exception>
    /// <exception cref="AddRoomUserException">Thrown when the user cannot be added to the room.</exception>
    RoomState RegisterUserInRoom(
        RoomId roomId,
        ConnectionId connectionId,
        RoomUser roomUser);

    /// <summary>
    /// Registers a platform-scoped user in the target room, replacing older connections for the same subject.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="connectionId">The user connection identifier.</param>
    /// <param name="roomUser">The room user name.</param>
    /// <param name="platformSubject">The stable platform subject identifier.</param>
    /// <returns>The updated room state.</returns>
    /// <exception cref="RoomNotFoundException">Thrown when the room cannot be found.</exception>
    /// <exception cref="AddRoomUserException">Thrown when the user cannot be added to the room.</exception>
    RoomState RegisterPlatformUserInRoom(
        RoomId roomId,
        ConnectionId connectionId,
        RoomUser roomUser,
        string platformSubject);

    /// <summary>
    /// Gets room snapshots for the specified admin user.
    /// </summary>
    /// <param name="adminUser">The current admin user.</param>
    /// <returns>The room views.</returns>
    RoomView[] GetRoomsSnapshot(AdminUser adminUser);

    /// <summary>
    /// Creates a new room with the specified name and language.
    /// </summary>
    /// <param name="name">The room name.</param>
    /// <param name="language">The room language.</param>
    /// <param name="adminUser">The admin creating the room.</param>
    /// <param name="initialText">The optional initial room text.</param>
    /// <returns>The created room state.</returns>
    RoomState CreateRoom(RoomName name, RoomLanguage language, AdminUser adminUser, RoomText? initialText = null);

    /// <summary>
    /// Deletes a room with admin authorization checks.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="adminUser">The current admin user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deletion result.</returns>
    Task<RoomDeletionResult> DeleteRoomAsync(RoomId roomId, AdminUser adminUser, CancellationToken cancellationToken);
}
