using Models;


namespace Abstractions;

/// <summary>
/// Defines access to room lifecycle and state management.
/// </summary>
public interface IRoomRegistry
{
    /// <summary>
    /// Creates a new room with the specified name and language.
    /// </summary>
    /// <param name="name">The room name.</param>
    /// <param name="language">The room language.</param>
    /// <param name="createdBy">The admin that created the room.</param>
    /// <param name="initialText">The optional initial room text.</param>
    /// <returns>The created room state.</returns>
    RoomState CreateRoom(RoomName name, RoomLanguage language, AdminUser createdBy, RoomText? initialText = null);

    /// <summary>
    /// Tries to get a room by its identifier.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="room">The room state when found.</param>
    /// <returns>True when found; otherwise false.</returns>
    bool TryGetRoom(RoomId roomId, out RoomState room);

    /// <summary>
    /// Gets a snapshot of all rooms.
    /// </summary>
    /// <returns>The room snapshots.</returns>
    IEnumerable<RoomState> GetRoomsSnapshot();

    /// <summary>
    /// Deletes a room by identifier with a reason.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="reason">The deletion reason.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when removed; otherwise false.</returns>
    Task<bool> DeleteRoomAsync(RoomId roomId, RoomDeletionReason reason, CancellationToken cancellationToken);
}
