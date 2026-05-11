using Models;

namespace Transport.Models;

/// <summary>
/// Represents the result of joining a room.
/// </summary>
/// <param name="Name">The room name.</param>
/// <param name="Language">The room language.</param>
/// <param name="Text">The room text.</param>
/// <param name="Version">The current room version.</param>
/// <param name="Users">The display names of users currently in the room.</param>
/// <param name="CreatedBy">The display name of the room creator.</param>
/// <param name="YjsState">Optional Yjs document state for the room.</param>
/// <param name="JoinedDisplayName">The display name assigned to the current connection.</param>
public sealed record JoinRoomResult(
    string Name,
    string Language,
    string Text,
    int Version,
    string[] Users,
    string CreatedBy,
    string? YjsState,
    string? JoinedDisplayName = null)
{
    /// <summary>
    /// Creates a join-room result from room state.
    /// </summary>
    /// <param name="room">The source room state.</param>
    /// <returns>The mapped join-room result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="room"/> is null.</exception>
    public static JoinRoomResult From(RoomState room)
        => From(room, null);

    /// <summary>
    /// Creates a join-room result from room state and current user display name.
    /// </summary>
    /// <param name="room">The source room state.</param>
    /// <param name="joinedDisplayName">The display name assigned to the current connection.</param>
    /// <returns>The mapped join-room result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="room"/> is null.</exception>
    public static JoinRoomResult From(RoomState room, string? joinedDisplayName)
    {
        ArgumentNullException.ThrowIfNull(room);

        return new JoinRoomResult(
            room.Name.Value,
            room.Language.Value,
            room.Text.Value,
            room.Version.Value,
            [.. room.CreateUsersSnapshot().Select(x => x.Value)],
            room.CreatedBy.Name,
            room.YjsState.Length > 0 ? Convert.ToBase64String(room.YjsState) : null,
            joinedDisplayName);
    }
}
