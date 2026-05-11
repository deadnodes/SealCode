using System.Collections.Immutable;

using Abstractions;

using Microsoft.Extensions.Logging;

using Models;

namespace Logic;

/// <summary>
/// Provides in-memory storage and management for rooms.
/// </summary>
public sealed class RoomRegistry : IRoomRegistry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoomRegistry"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="notifier">The room notifier.</param>
    public RoomRegistry(ILogger<RoomRegistry> logger, IRoomNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(notifier);

        _logger = logger;
        _notifier = notifier;
    }

    /// <inheritdoc />
    public bool TryGetRoom(RoomId roomId, out RoomState room) => _rooms.TryGetValue(roomId, out room!);

    /// <inheritdoc />
    public RoomState CreateRoom(RoomName name, RoomLanguage language, AdminUser createdBy, RoomText? initialText = null)
    {
        var room = new RoomState(
            RoomId.New(),
            name,
            language,
            initialText ?? new RoomText(string.Empty),
            new RoomVersion(1),
            DateTimeOffset.UtcNow,
            createdBy);

        _ = ImmutableInterlocked.TryAdd(ref _rooms, room.RoomId, room);

#pragma warning disable CA1873 // Avoid potentially expensive logging
        _logger.LogInformation("Room created {RoomId} ({Name}) by {CreatedBy}", room.RoomId.Value, room.Name.Value, room.CreatedBy.Name);
#pragma warning restore CA1873 // Avoid potentially expensive logging

        return room;
    }

    /// <inheritdoc />
    public IEnumerable<RoomState> GetRoomsSnapshot() => _rooms.Values;

    /// <inheritdoc />
    public async Task<bool> DeleteRoomAsync(RoomId roomId, RoomDeletionReason reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ImmutableInterlocked.TryRemove(ref _rooms, roomId, out var room))
        {
            return false;
        }

#pragma warning disable CA1873 // Avoid potentially expensive logging
        _logger.LogInformation("Room deleted {RoomId} ({Name})", room.RoomId.Value, room.Name.Value);
#pragma warning restore CA1873 // Avoid potentially expensive logging

        await _notifier.RoomKilledAsync(roomId, reason, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private ImmutableDictionary<RoomId, RoomState> _rooms = [];
    private readonly ILogger<RoomRegistry> _logger;
    private readonly IRoomNotifier _notifier;
}
