using System.Collections.Immutable;

using Models.Exceptions;

namespace Models;

/// <summary>
/// Represents the in-memory state of a room.
/// </summary>
public sealed class RoomState
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoomState"/> class.
    /// </summary>
    /// <param name="roomId">The room identifier.</param>
    /// <param name="name">The room name.</param>
    /// <param name="language">The room language.</param>
    /// <param name="text">The room text.</param>
    /// <param name="version">The room version.</param>
    /// <param name="lastUpdatedUtc">The last updated timestamp in UTC.</param>
    /// <param name="createdBy">The admin that created the room.</param>
    /// <param name="yjsState">The serialized Yjs document state (full update).</param>
    /// <param name="accessMode">The room access mode.</param>
    public RoomState(
        RoomId roomId,
        RoomName name,
        RoomLanguage language,
        RoomText text,
        RoomVersion version,
        DateTimeOffset lastUpdatedUtc,
        AdminUser createdBy,
        byte[]? yjsState = null,
        RoomAccessMode accessMode = RoomAccessMode.Standalone)
    {
        RoomId = roomId;
        Name = name;
        Language = language;
        Text = text;
        Version = version;
        LastUpdatedUtc = lastUpdatedUtc;
        CreatedBy = createdBy;
        YjsState = yjsState ?? [];
        AccessMode = accessMode;
    }

    /// <summary>
    /// Gets the room identifier.
    /// </summary>
    public RoomId RoomId { get; }

    /// <summary>
    /// Gets the room name.
    /// </summary>
    public RoomName Name { get; }

    /// <summary>
    /// Gets the room language.
    /// </summary>
    public RoomLanguage Language { get; private set; }

    /// <summary>
    /// Gets the room text.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when the value is null.</exception>
    public RoomText Text { get; private set; }

    /// <summary>
    /// Gets the room version.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the version is less than 1.</exception>
    public RoomVersion Version { get; private set; }

    /// <summary>
    /// Gets the serialized Yjs document state (full update).
    /// </summary>
    public byte[] YjsState { get; private set; }

    /// <summary>
    /// Gets the last updated timestamp in UTC.
    /// </summary>
    public DateTimeOffset LastUpdatedUtc { get; private set; }

    /// <summary>
    /// Gets the admin that created the room.
    /// </summary>
    public AdminUser CreatedBy { get; }

    /// <summary>
    /// Gets the room access mode.
    /// </summary>
    public RoomAccessMode AccessMode { get; }

    /// <summary>
    /// Gets a value indicating whether the room requires a signed platform access token.
    /// </summary>
    public bool RequiresPlatformAccess => AccessMode == RoomAccessMode.Platform;

    /// <summary>
    /// Determines whether the provided admin user created this room.
    /// </summary>
    /// <param name="adminUser">The admin user to check.</param>
    /// <returns>True when the admin user created this room; otherwise false.</returns>
    public bool IsCreatedBy(AdminUser adminUser)
        => CreatedBy.Matches(adminUser);

    /// <summary>
    /// Determines whether the provided admin user can delete this room.
    /// </summary>
    /// <param name="adminUser">The current admin user.</param>
    /// <returns>True when the admin user can delete this room; otherwise false.</returns>
    public bool CanDelete(AdminUser adminUser)
        => adminUser.IsSuperAdmin || IsCreatedBy(adminUser);

    /// <summary>
    /// Gets the connected users keyed by connection id.
    /// </summary>
    public IReadOnlyDictionary<ConnectionId, RoomUser> ConnectedUsers => _connectedUsers;

    /// <summary>
    /// Gets the number of connected users.
    /// </summary>
    public int ConnectedUserCount => _connectedUsers.Count;

    /// <summary>
    /// Creates a sorted snapshot of connected room users.
    /// </summary>
    /// <returns>Connected room users sorted case-insensitively.</returns>
    public RoomUser[] CreateUsersSnapshot()
        => [.. _connectedUsers.Values.OrderBy(name => name.Value, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Checks if a room user name is already in use by another connection.
    /// </summary>
    /// <param name="connectionId">The connection identifier to exclude.</param>
    /// <param name="roomUser">The room user name to check.</param>
    /// <returns>True when the room user name is in use by another connection; otherwise false.</returns>
    public bool IsRoomUserInUse(ConnectionId connectionId, RoomUser roomUser)
        => _connectedUsers.Any(entry => entry.Key != connectionId
            && string.Equals(entry.Value.Value, roomUser.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds or updates a connected room user with room capacity and name checks.
    /// </summary>
    /// <param name="connectionId">The connection identifier.</param>
    /// <param name="roomUser">The room user name.</param>
    /// <param name="maxUsers">The maximum number of users allowed in the room.</param>
    /// <exception cref="AddRoomUserException">Thrown when the room is full or the name is already in use.</exception>
    public void AddUser(ConnectionId connectionId, RoomUser roomUser, int maxUsers)
    {
        lock (_addGuard)
        {
            var alreadyInRoom = HasUser(connectionId);
            if (!alreadyInRoom && _connectedUsers.Count >= maxUsers)
            {
                throw new AddRoomUserException($"Room is full (max {maxUsers})");
            }

            if (IsRoomUserInUse(connectionId, roomUser))
            {
                throw new AddRoomUserException("Display name already in use. Choose another name.");
            }

            AddOrUpdateUser(connectionId, roomUser);
        }
    }

    /// <summary>
    /// Adds or replaces a platform-scoped connected room user.
    /// </summary>
    /// <param name="connectionId">The current connection identifier.</param>
    /// <param name="roomUser">The room user name.</param>
    /// <param name="platformSubject">The stable platform subject identifier.</param>
    /// <param name="maxUsers">The maximum number of users allowed in the room.</param>
    /// <exception cref="AddRoomUserException">Thrown when the room is full or the name is already in use.</exception>
    public void AddPlatformUser(ConnectionId connectionId, RoomUser roomUser, string platformSubject, int maxUsers)
    {
        if (string.IsNullOrWhiteSpace(platformSubject))
        {
            AddUser(connectionId, roomUser, maxUsers);
            return;
        }

        lock (_addGuard)
        {
            var subject = platformSubject.Trim();
            if (_platformConnections.TryGetValue(subject, out var existingConnectionId)
                && existingConnectionId != connectionId)
            {
                _ = ImmutableInterlocked.TryRemove(ref _connectedUsers, existingConnectionId, out _);
            }

            var alreadyInRoom = HasUser(connectionId);
            if (!alreadyInRoom && _connectedUsers.Count >= maxUsers)
            {
                throw new AddRoomUserException($"Room is full (max {maxUsers})");
            }

            if (IsRoomUserInUse(connectionId, roomUser))
            {
                throw new AddRoomUserException("Display name already in use. Choose another name.");
            }

            AddOrUpdateUser(connectionId, roomUser);
            _ = ImmutableInterlocked.AddOrUpdate(
                ref _platformConnections,
                subject,
                connectionId,
                static (_, value) => value);
        }
    }

    private void AddOrUpdateUser(ConnectionId connectionId, RoomUser roomUser)
        => ImmutableInterlocked.AddOrUpdate(
            ref _connectedUsers,
            connectionId,
            roomUser,
            static (_, value) => value);

    /// <summary>
    /// Tries to get a connected room user name.
    /// </summary>
    /// <param name="connectionId">The connection identifier.</param>
    /// <param name="roomUser">The room user name.</param>
    /// <returns>True when found; otherwise false.</returns>
    public bool TryGetRoomUser(ConnectionId connectionId, out RoomUser roomUser) => _connectedUsers.TryGetValue(connectionId, out roomUser);

    private bool HasUser(ConnectionId connectionId) => _connectedUsers.ContainsKey(connectionId);

    /// <summary>
    /// Removes a connected room user.
    /// </summary>
    /// <param name="connectionId">The connection identifier.</param>
    /// <param name="roomUser">The removed room user name.</param>
    /// <returns>True when removed; otherwise false.</returns>
    public bool RemoveUser(ConnectionId connectionId, out RoomUser roomUser)
    {
        var removed = ImmutableInterlocked.TryRemove(ref _connectedUsers, connectionId, out roomUser);
        if (removed)
        {
            foreach (var entry in _platformConnections)
            {
                if (entry.Value == connectionId)
                {
                    _ = ImmutableInterlocked.TryRemove(ref _platformConnections, entry.Key, out _);
                    break;
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Updates the room text and bumps the version.
    /// </summary>
    /// <param name="text">The new text.</param>
    /// <param name="updatedUtc">The update timestamp in UTC.</param>
    /// <returns>The new room version.</returns>
    public RoomVersion UpdateText(RoomText text, DateTimeOffset updatedUtc)
    {
        lock (_versionGuard)
        {
            Text = text;
            Version = Version.Next();
            LastUpdatedUtc = updatedUtc;
            return Version;
        }
    }

    /// <summary>
    /// Updates the room language and bumps the version.
    /// </summary>
    /// <param name="language">The new language.</param>
    /// <param name="updatedUtc">The update timestamp in UTC.</param>
    /// <param name="languageValidator">The language validator.</param>
    /// <returns>The new room version.</returns>
    /// <exception cref="ArgumentException">Thrown when the language is invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when the validator is null.</exception>
    public RoomVersion UpdateLanguage(RoomLanguage language, DateTimeOffset updatedUtc, ILanguageValidator languageValidator)
    {
        ArgumentNullException.ThrowIfNull(languageValidator);

        if (!languageValidator.IsValid(language))
        {
            throw new ArgumentException("Invalid language", nameof(language));
        }

        lock (_versionGuard)
        {
            Language = language;
            Version = Version.Next();
            LastUpdatedUtc = updatedUtc;
            return Version;
        }
    }

    /// <summary>
    /// Updates the stored Yjs state and text snapshot, bumping the version.
    /// </summary>
    /// <param name="state">The serialized Yjs document state.</param>
    /// <param name="text">The plain text snapshot.</param>
    /// <param name="updatedUtc">The update timestamp in UTC.</param>
    /// <param name="version">The resulting room version.</param>
    /// <returns>True when the state changed and the version was bumped; otherwise false.</returns>
    public bool TryUpdateYjsState(byte[] state, RoomText text, DateTimeOffset updatedUtc, out RoomVersion version)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_versionGuard)
        {
            var sameState = state.AsSpan().SequenceEqual(YjsState);
            var sameText = string.Equals(text.Value, Text.Value, StringComparison.Ordinal);
            if (sameState && sameText)
            {
                version = Version;
                return false;
            }

            YjsState = state;
            Text = text;
            Version = Version.Next();
            LastUpdatedUtc = updatedUtc;
            version = Version;
            return true;
        }
    }

    private readonly Lock _addGuard = new();
    private readonly Lock _versionGuard = new();
    private ImmutableDictionary<ConnectionId, RoomUser> _connectedUsers = [];
    private ImmutableDictionary<string, ConnectionId> _platformConnections = ImmutableDictionary.Create<string, ConnectionId>(StringComparer.OrdinalIgnoreCase);
}
