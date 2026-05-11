namespace Models;

/// <summary>
/// Defines how a room can be accessed by participants.
/// </summary>
public enum RoomAccessMode
{
    /// <summary>
    /// The room is opened directly and participants join by entering a display name.
    /// </summary>
    Standalone = 0,

    /// <summary>
    /// The room requires a signed platform access token.
    /// </summary>
    Platform = 1
}
