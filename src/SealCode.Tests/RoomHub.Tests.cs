using Abstractions;

using FluentAssertions;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

using Models;
using Models.Exceptions;

using Moq;

namespace SealCode.Tests;

public sealed class RoomHubTests
{
    private static readonly string DefaultRoomId = RoomId.New().Value;

    [Fact(DisplayName = "CtorShouldThrowWhenRoomManagerIsNull")]
    [Trait("Category", "Unit")]
    public void CtorShouldThrowWhenRoomManagerIsNull()
    {
        var validator = new Mock<ILanguageValidator>(MockBehavior.Strict).Object;
        var accessValidator = new Mock<IPlatformAccessValidator>(MockBehavior.Strict).Object;
        var logger = new Mock<ILogger<RoomHub>>(MockBehavior.Strict).Object;

        var action = () => new RoomHub(null!, validator, accessValidator, logger);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact(DisplayName = "CtorShouldThrowWhenLanguageValidatorIsNull")]
    [Trait("Category", "Unit")]
    public void CtorShouldThrowWhenLanguageValidatorIsNull()
    {
        var roomManager = new Mock<IRoomManager>(MockBehavior.Strict).Object;
        var accessValidator = new Mock<IPlatformAccessValidator>(MockBehavior.Strict).Object;
        var logger = new Mock<ILogger<RoomHub>>().Object;

        var action = () => new RoomHub(roomManager, null!, accessValidator, logger);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact(DisplayName = "CtorShouldThrowWhenLoggerIsNull")]
    [Trait("Category", "Unit")]
    public void CtorShouldThrowWhenLoggerIsNull()
    {
        var roomManager = new Mock<IRoomManager>(MockBehavior.Strict).Object;
        var validator = new Mock<ILanguageValidator>(MockBehavior.Strict).Object;
        var accessValidator = new Mock<IPlatformAccessValidator>(MockBehavior.Strict).Object;

        var action = () => new RoomHub(roomManager, validator, accessValidator, null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact(DisplayName = "JoinRoomAsyncShouldThrowWhenRoomIdIsEmpty")]
    [Trait("Category", "Unit")]
    public async Task JoinRoomAsyncShouldThrowWhenRoomIdIsEmpty()
    {
        using var hub = CreateHub();

        Func<Task> action = () => hub.JoinRoomAsync(" ", "Alice");

        await action.Should().ThrowAsync<HubException>();
    }

    [Fact(DisplayName = "JoinRoomAsyncShouldThrowWhenRoomIsNotFound")]
    [Trait("Category", "Unit")]
    public async Task JoinRoomAsyncShouldThrowWhenRoomIsNotFound()
    {
        var roomManager = new Mock<IRoomManager>(MockBehavior.Strict);
        var registerUserCalls = 0;
        roomManager.Setup(m => m.RegisterUserInRoom(
                It.IsAny<RoomId>(),
                It.IsAny<ConnectionId>(),
                It.IsAny<RoomUser>()))
            .Callback<RoomId, ConnectionId, RoomUser>((_, _, _) => registerUserCalls++)
            .Throws(new RoomNotFoundException());
        using var hub = CreateHub(roomManager.Object);

        Func<Task> action = () => hub.JoinRoomAsync(DefaultRoomId, "Alice");

        await action.Should().ThrowAsync<HubException>();
        registerUserCalls.Should().Be(1);
    }

    [Fact(DisplayName = "JoinRoomAsyncShouldThrowWhenRoomUserIsEmpty")]
    [Trait("Category", "Unit")]
    public async Task JoinRoomAsyncShouldThrowWhenRoomUserIsEmpty()
    {
        using var hub = CreateHub();

        Func<Task> action = () => hub.JoinRoomAsync(DefaultRoomId, " ");

        await action.Should().ThrowAsync<HubException>();
    }

    [Fact(DisplayName = "JoinRoomAsyncShouldAddCallerToGroupAndNotifyOthers")]
    [Trait("Category", "Unit")]
    public async Task JoinRoomAsyncShouldAddCallerToGroupAndNotifyOthers()
    {
        var roomId = DefaultRoomId;
        using var cts = new CancellationTokenSource();
        var room = CreateRoomState(roomId);
        room.AddUser(new ConnectionId("conn-1"), new RoomUser("Alice"), 5);
        room.AddUser(new ConnectionId("conn-2"), new RoomUser("Bob"), 5);
        var roomManager = new Mock<IRoomManager>(MockBehavior.Strict);
        var registerUserCalls = 0;
        roomManager.Setup(m => m.RegisterUserInRoom(
                It.Is<RoomId>(id => id.Value == roomId),
                It.Is<ConnectionId>(id => id.Value == "conn-1"),
                It.Is<RoomUser>(user => user.Value == "Alice")))
            .Callback<RoomId, ConnectionId, RoomUser>((_, _, _) => registerUserCalls++)
            .Returns(room);

        var userJoinedCalls = 0;
        var proxy = new Mock<IClientProxy>(MockBehavior.Strict);
        proxy.Setup(p => p.SendCoreAsync(
                "UserJoined",
                It.Is<object?[]>(args => MatchUserJoinedArgs(args, "Alice", new[] { "Alice", "Bob" })),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .Callback(() => userJoinedCalls++)
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>(MockBehavior.Strict);
        var groupExceptCalls = 0;
        clients.Setup(c => c.GroupExcept(
                It.Is<string>(group => group == roomId),
                It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "conn-1")))
            .Callback(() => groupExceptCalls++)
            .Returns(proxy.Object);

        var addToGroupCalls = 0;
        var groups = new Mock<IGroupManager>(MockBehavior.Strict);
        groups.Setup(g => g.AddToGroupAsync(
                It.Is<string>(conn => conn == "conn-1"),
                It.Is<string>(group => group == roomId),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .Callback(() => addToGroupCalls++)
            .Returns(Task.CompletedTask);

        var items = new Dictionary<object, object?>();
        using var hub = CreateHub(
            roomManager.Object,
            clients: clients.Object,
            groups: groups.Object,
            items: items,
            cancellationToken: cts.Token);

        var result = await hub.JoinRoomAsync(roomId, " Alice ");

        result.Users.Should().Equal("Alice", "Bob");
        items["roomId"].Should().Be(roomId);
        items["displayName"].Should().Be("Alice");
        registerUserCalls.Should().Be(1);
        groupExceptCalls.Should().Be(1);
        userJoinedCalls.Should().Be(1);
        addToGroupCalls.Should().Be(1);
    }

    [Fact(DisplayName = "JoinRoomAsyncShouldThrowWhenAddUserFails")]
    [Trait("Category", "Unit")]
    public async Task JoinRoomAsyncShouldThrowWhenAddUserFails()
    {
        var roomManager = new Mock<IRoomManager>(MockBehavior.Strict);
        var registerUserCalls = 0;
        roomManager.Setup(m => m.RegisterUserInRoom(
                It.IsAny<RoomId>(),
                It.IsAny<ConnectionId>(),
                It.IsAny<RoomUser>()))
            .Callback<RoomId, ConnectionId, RoomUser>((_, _, _) => registerUserCalls++)
            .Throws(new AddRoomUserException("Display name already in use. Choose another name."));
        using var hub = CreateHub(roomManager.Object);

        Func<Task> action = () => hub.JoinRoomAsync(DefaultRoomId, "Alice");

        await action.Should().ThrowAsync<HubException>();
        registerUserCalls.Should().Be(1);
    }

    [Fact(DisplayName = "UpdateTextAsyncShouldThrowWhenRoomIsNotFound")]
    [Trait("Category", "Unit")]
    public async Task UpdateTextAsyncShouldThrowWhenRoomIsNotFound()
    {
        RoomState missing = null!;
        var roomManager = new Mock<IRoomManager>(MockBehavior.Strict);
        var tryGetRoomCalls = 0;
        roomManager.Setup(m => m.TryGetRoom(It.IsAny<RoomId>(), out missing))
            .Callback(() => tryGetRoomCalls++)
            .Returns(false);
        using var hub = CreateHub(roomManager.Object);

        Func<Task> action = () => hub.UpdateTextAsync(DefaultRoomId, "hello", 1);

        await action.Should().ThrowAsync<HubException>();
        tryGetRoomCalls.Should().Be(1);
    }

    [Fact(DisplayName = "UpdateTextAsyncShouldBroadcastUpdate")]
    [Trait("Category", "Unit")]
    public async Task UpdateTextAsyncShouldBroadcastUpdate()
    {
        var roomId = DefaultRoomId;
        using var cts = new CancellationTokenSource();
        var room = CreateRoomState(roomId, text: "before");
        var tryGetRoomCounter = SetupTryGetRoom(roomId, room, out var roomManager);

        var textUpdatedCalls = 0;
        var proxy = new Mock<IClientProxy>(MockBehavior.Strict);
        proxy.Setup(p => p.SendCoreAsync(
                "TextUpdated",
                It.Is<object?[]>(args => MatchTextUpdatedArgs(args, "after", 2, "unknown")),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .Callback(() => textUpdatedCalls++)
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>(MockBehavior.Strict);
        var groupExceptCalls = 0;
        clients.Setup(c => c.GroupExcept(
                It.Is<string>(group => group == roomId),
                It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "conn-1")))
            .Callback(() => groupExceptCalls++)
            .Returns(proxy.Object);

        using var hub = CreateHub(roomManager.Object, clients: clients.Object, cancellationToken: cts.Token);

        await hub.UpdateTextAsync(roomId, "after", 1);

        room.Text.Value.Should().Be("after");
        room.Version.Value.Should().Be(2);
        tryGetRoomCounter.Count.Should().Be(1);
        groupExceptCalls.Should().Be(1);
        textUpdatedCalls.Should().Be(1);
    }

    [Fact(DisplayName = "UpdateYjsAsyncShouldThrowWhenPayloadIsInvalid")]
    [Trait("Category", "Unit")]
    public async Task UpdateYjsAsyncShouldThrowWhenPayloadIsInvalid()
    {
        var room = CreateRoomState(DefaultRoomId);
        var tryGetRoomCounter = SetupTryGetRoom(DefaultRoomId, room, out var roomManager);
        using var hub = CreateHub(roomManager.Object);

        Func<Task> action = () => hub.UpdateYjsAsync(DefaultRoomId, "not-base64", "still-not-base64", "text");

        await action.Should().ThrowAsync<HubException>();
        tryGetRoomCounter.Count.Should().Be(1);
    }

    [Fact(DisplayName = "UpdateYjsAsyncShouldBroadcastWhenStateChanges")]
    [Trait("Category", "Unit")]
    public async Task UpdateYjsAsyncShouldBroadcastWhenStateChanges()
    {
        var roomId = DefaultRoomId;
        using var cts = new CancellationTokenSource();
        var updateBase64 = Convert.ToBase64String([1, 2]);
        var stateBase64 = Convert.ToBase64String([1, 2, 3]);
        var room = CreateRoomState(roomId, text: "before");
        room.AddUser(new ConnectionId("conn-1"), new RoomUser("Alice"), 5);
        var tryGetRoomCounter = SetupTryGetRoom(roomId, room, out var roomManager);

        var yjsUpdatedCalls = 0;
        var proxy = new Mock<IClientProxy>(MockBehavior.Strict);
        proxy.Setup(p => p.SendCoreAsync(
                "YjsUpdated",
                It.Is<object?[]>(args => MatchYjsUpdatedArgs(args, updateBase64, 2, "Alice", stateBase64)),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .Callback(() => yjsUpdatedCalls++)
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>(MockBehavior.Strict);
        var groupCalls = 0;
        clients.Setup(c => c.Group(It.Is<string>(group => group == roomId)))
            .Callback(() => groupCalls++)
            .Returns(proxy.Object);

        using var hub = CreateHub(roomManager.Object, clients: clients.Object, cancellationToken: cts.Token);

        await hub.UpdateYjsAsync(roomId, updateBase64, stateBase64, "after");

        room.Text.Value.Should().Be("after");
        room.Version.Value.Should().Be(2);
        room.YjsState.Should().Equal([1, 2, 3]);
        tryGetRoomCounter.Count.Should().Be(1);
        groupCalls.Should().Be(1);
        yjsUpdatedCalls.Should().Be(1);
    }

    [Fact(DisplayName = "SetLanguageAsyncShouldThrowWhenLanguageIsInvalid")]
    [Trait("Category", "Unit")]
    public async Task SetLanguageAsyncShouldThrowWhenLanguageIsInvalid()
    {
        var room = CreateRoomState(DefaultRoomId);
        var tryGetRoomCounter = SetupTryGetRoom(DefaultRoomId, room, out var roomManager);
        var validator = new Mock<ILanguageValidator>(MockBehavior.Strict);
        var isValidCalls = 0;
        validator.Setup(v => v.IsValid(It.IsAny<RoomLanguage>()))
            .Callback<RoomLanguage>(_ => isValidCalls++)
            .Returns(false);
        using var hub = CreateHub(roomManager.Object, validator.Object);

        Func<Task> action = () => hub.SetLanguageAsync(DefaultRoomId, "javascript");

        await action.Should().ThrowAsync<HubException>();
        tryGetRoomCounter.Count.Should().Be(1);
        isValidCalls.Should().Be(1);
    }

    [Fact(DisplayName = "SetLanguageAsyncShouldBroadcastWhenLanguageIsValid")]
    [Trait("Category", "Unit")]
    public async Task SetLanguageAsyncShouldBroadcastWhenLanguageIsValid()
    {
        using var cts = new CancellationTokenSource();
        var room = CreateRoomState(DefaultRoomId);
        var tryGetRoomCounter = SetupTryGetRoom(DefaultRoomId, room, out var roomManager);

        var validator = new Mock<ILanguageValidator>(MockBehavior.Strict);
        var isValidCalls = 0;
        validator.Setup(v => v.IsValid(It.Is<RoomLanguage>(language => language.Value == "javascript")))
            .Callback<RoomLanguage>(_ => isValidCalls++)
            .Returns(true);

        var languageUpdatedCalls = 0;
        var proxy = new Mock<IClientProxy>(MockBehavior.Strict);
        proxy.Setup(p => p.SendCoreAsync(
                "LanguageUpdated",
                It.Is<object?[]>(args => MatchLanguageUpdatedArgs(args, "javascript", 2)),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .Callback(() => languageUpdatedCalls++)
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>(MockBehavior.Strict);
        var groupCalls = 0;
        clients.Setup(c => c.Group(It.Is<string>(group => group == DefaultRoomId)))
            .Callback(() => groupCalls++)
            .Returns(proxy.Object);

        using var hub = CreateHub(roomManager.Object, validator.Object, clients.Object, cancellationToken: cts.Token);

        await hub.SetLanguageAsync(DefaultRoomId, "JavaScript");

        room.Language.Value.Should().Be("javascript");
        room.Version.Value.Should().Be(2);
        tryGetRoomCounter.Count.Should().Be(1);
        isValidCalls.Should().Be(1);
        groupCalls.Should().Be(1);
        languageUpdatedCalls.Should().Be(1);
    }

    [Fact(DisplayName = "UpdateCursorAsyncShouldReturnWhenUserNotFound")]
    [Trait("Category", "Unit")]
    public async Task UpdateCursorAsyncShouldReturnWhenUserNotFound()
    {
        var room = CreateRoomState(DefaultRoomId);
        var tryGetRoomCounter = SetupTryGetRoom(DefaultRoomId, room, out var roomManager);
        var clients = new Mock<IHubCallerClients>(MockBehavior.Strict);
        using var hub = CreateHub(roomManager.Object, clients: clients.Object);

        await hub.UpdateCursorAsync(DefaultRoomId, 3);

        tryGetRoomCounter.Count.Should().Be(1);
        clients.VerifyNoOtherCalls();
    }

    [Fact(DisplayName = "UpdateCursorAsyncShouldBroadcastWhenUserFound")]
    [Trait("Category", "Unit")]
    public async Task UpdateCursorAsyncShouldBroadcastWhenUserFound()
    {
        using var cts = new CancellationTokenSource();
        var room = CreateRoomState(DefaultRoomId);
        room.AddUser(new ConnectionId("conn-1"), new RoomUser("Alice"), 5);
        var tryGetRoomCounter = SetupTryGetRoom(DefaultRoomId, room, out var roomManager);

        var cursorUpdatedCalls = 0;
        var proxy = new Mock<IClientProxy>(MockBehavior.Strict);
        proxy.Setup(p => p.SendCoreAsync(
                "CursorUpdated",
                It.Is<object?[]>(args => MatchCursorUpdatedArgs(args, "Alice", 3)),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .Callback(() => cursorUpdatedCalls++)
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>(MockBehavior.Strict);
        var groupExceptCalls = 0;
        clients.Setup(c => c.GroupExcept(
                It.Is<string>(group => group == DefaultRoomId),
                It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "conn-1")))
            .Callback(() => groupExceptCalls++)
            .Returns(proxy.Object);

        using var hub = CreateHub(roomManager.Object, clients: clients.Object, cancellationToken: cts.Token);

        await hub.UpdateCursorAsync(DefaultRoomId, 3);

        tryGetRoomCounter.Count.Should().Be(1);
        groupExceptCalls.Should().Be(1);
        cursorUpdatedCalls.Should().Be(1);
    }

    [Fact(DisplayName = "UpdateSelectionAsyncShouldBroadcastWhenUserFound")]
    [Trait("Category", "Unit")]
    public async Task UpdateSelectionAsyncShouldBroadcastWhenUserFound()
    {
        using var cts = new CancellationTokenSource();
        var room = CreateRoomState(DefaultRoomId);
        room.AddUser(new ConnectionId("conn-1"), new RoomUser("Alice"), 5);
        var tryGetRoomCounter = SetupTryGetRoom(DefaultRoomId, room, out var roomManager);

        var userSelectionCalls = 0;
        var proxy = new Mock<IClientProxy>(MockBehavior.Strict);
        proxy.Setup(p => p.SendCoreAsync(
                "UserSelection",
                It.Is<object?[]>(args => MatchUserSelectionArgs(args, "Alice", true)),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .Callback(() => userSelectionCalls++)
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>(MockBehavior.Strict);
        var groupCalls = 0;
        clients.Setup(c => c.Group(It.Is<string>(group => group == DefaultRoomId)))
            .Callback(() => groupCalls++)
            .Returns(proxy.Object);

        using var hub = CreateHub(roomManager.Object, clients: clients.Object, cancellationToken: cts.Token);

        await hub.UpdateSelectionAsync(DefaultRoomId, true);

        tryGetRoomCounter.Count.Should().Be(1);
        groupCalls.Should().Be(1);
        userSelectionCalls.Should().Be(1);
    }

    [Fact(DisplayName = "UpdateCopyAsyncShouldBroadcastWhenUserFound")]
    [Trait("Category", "Unit")]
    public async Task UpdateCopyAsyncShouldBroadcastWhenUserFound()
    {
        using var cts = new CancellationTokenSource();
        var room = CreateRoomState(DefaultRoomId);
        room.AddUser(new ConnectionId("conn-1"), new RoomUser("Alice"), 5);
        var tryGetRoomCounter = SetupTryGetRoom(DefaultRoomId, room, out var roomManager);

        var userCopyCalls = 0;
        var proxy = new Mock<IClientProxy>(MockBehavior.Strict);
        proxy.Setup(p => p.SendCoreAsync(
                "UserCopy",
                It.Is<object?[]>(args => MatchUserCopyArgs(args, "Alice")),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .Callback(() => userCopyCalls++)
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>(MockBehavior.Strict);
        var groupCalls = 0;
        clients.Setup(c => c.Group(It.Is<string>(group => group == DefaultRoomId)))
            .Callback(() => groupCalls++)
            .Returns(proxy.Object);

        using var hub = CreateHub(roomManager.Object, clients: clients.Object, cancellationToken: cts.Token);

        await hub.UpdateCopyAsync(DefaultRoomId);

        tryGetRoomCounter.Count.Should().Be(1);
        groupCalls.Should().Be(1);
        userCopyCalls.Should().Be(1);
    }

    [Fact(DisplayName = "LeaveRoomAsyncShouldRemoveUserAndNotifyGroup")]
    [Trait("Category", "Unit")]
    public async Task LeaveRoomAsyncShouldRemoveUserAndNotifyGroup()
    {
        using var cts = new CancellationTokenSource();
        var room = CreateRoomState(DefaultRoomId);
        room.AddUser(new ConnectionId("conn-1"), new RoomUser("Alice"), 5);
        room.AddUser(new ConnectionId("conn-2"), new RoomUser("Bob"), 5);
        var tryGetRoomCounter = SetupTryGetRoom(DefaultRoomId, room, out var roomManager);

        var removeFromGroupCalls = 0;
        var groups = new Mock<IGroupManager>(MockBehavior.Strict);
        groups.Setup(g => g.RemoveFromGroupAsync(
                It.Is<string>(conn => conn == "conn-1"),
                It.Is<string>(group => group == DefaultRoomId),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .Callback(() => removeFromGroupCalls++)
            .Returns(Task.CompletedTask);

        var userLeftCalls = 0;
        var proxy = new Mock<IClientProxy>(MockBehavior.Strict);
        proxy.Setup(p => p.SendCoreAsync(
                "UserLeft",
                It.Is<object?[]>(args => MatchUserLeftArgs(args, "Alice", new[] { "Bob" })),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .Callback(() => userLeftCalls++)
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>(MockBehavior.Strict);
        var groupCalls = 0;
        clients.Setup(c => c.Group(It.Is<string>(group => group == DefaultRoomId)))
            .Callback(() => groupCalls++)
            .Returns(proxy.Object);

        using var hub = CreateHub(
            roomManager.Object,
            clients: clients.Object,
            groups: groups.Object,
            cancellationToken: cts.Token);

        await hub.LeaveRoomAsync(DefaultRoomId);

        room.ConnectedUserCount.Should().Be(1);
        tryGetRoomCounter.Count.Should().Be(1);
        groupCalls.Should().Be(1);
        removeFromGroupCalls.Should().Be(1);
        userLeftCalls.Should().Be(1);
    }

    private static bool MatchUserJoinedArgs(object?[] args, string user, string[] users)
    {
        if (args.Length != 2)
        {
            return false;
        }

        if (!string.Equals(args[0] as string, user, StringComparison.Ordinal))
        {
            return false;
        }

        var actualUsers = args[1] as string[];
        return actualUsers is not null && actualUsers.SequenceEqual(users);
    }

    private static bool MatchTextUpdatedArgs(object?[] args, string text, int version, string author)
    {
        if (args.Length != 3)
        {
            return false;
        }

        if (!string.Equals(args[0] as string, text, StringComparison.Ordinal))
        {
            return false;
        }

        if (args[1] is not int actualVersion || actualVersion != version)
        {
            return false;
        }

        return string.Equals(args[2] as string, author, StringComparison.Ordinal);
    }

    private static bool MatchYjsUpdatedArgs(object?[] args, string updateBase64, int version, string author, string stateBase64)
    {
        if (args.Length != 4)
        {
            return false;
        }

        if (!string.Equals(args[0] as string, updateBase64, StringComparison.Ordinal))
        {
            return false;
        }

        if (args[1] is not int actualVersion || actualVersion != version)
        {
            return false;
        }

        if (!string.Equals(args[2] as string, author, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(args[3] as string, stateBase64, StringComparison.Ordinal);
    }

    private static bool MatchLanguageUpdatedArgs(object?[] args, string language, int version)
    {
        if (args.Length != 2)
        {
            return false;
        }

        if (!string.Equals(args[0] as string, language, StringComparison.Ordinal))
        {
            return false;
        }

        return args[1] is int actualVersion && actualVersion == version;
    }

    private static bool MatchCursorUpdatedArgs(object?[] args, string author, int position)
    {
        if (args.Length != 2)
        {
            return false;
        }

        if (!string.Equals(args[0] as string, author, StringComparison.Ordinal))
        {
            return false;
        }

        return args[1] is int actualPosition && actualPosition == position;
    }

    private static bool MatchUserSelectionArgs(object?[] args, string author, bool isMultiLine)
    {
        if (args.Length != 2)
        {
            return false;
        }

        if (!string.Equals(args[0] as string, author, StringComparison.Ordinal))
        {
            return false;
        }

        return args[1] is bool actualIsMultiLine && actualIsMultiLine == isMultiLine;
    }

    private static bool MatchUserCopyArgs(object?[] args, string author)
    {
        if (args.Length != 1)
        {
            return false;
        }

        return string.Equals(args[0] as string, author, StringComparison.Ordinal);
    }

    private static bool MatchUserLeftArgs(object?[] args, string user, string[] users)
    {
        if (args.Length != 2)
        {
            return false;
        }

        if (!string.Equals(args[0] as string, user, StringComparison.Ordinal))
        {
            return false;
        }

        var actualUsers = args[1] as string[];
        return actualUsers is not null && actualUsers.SequenceEqual(users);
    }

    private static RoomHub CreateHub(
        IRoomManager? roomManager = null,
        ILanguageValidator? languageValidator = null,
        IHubCallerClients? clients = null,
        IGroupManager? groups = null,
        IPlatformAccessValidator? accessValidator = null,
        IDictionary<object, object?>? items = null,
        CancellationToken cancellationToken = default)
    {
        roomManager ??= new Mock<IRoomManager>(MockBehavior.Strict).Object;
        languageValidator ??= new Mock<ILanguageValidator>(MockBehavior.Strict).Object;
        clients ??= new Mock<IHubCallerClients>(MockBehavior.Strict).Object;
        groups ??= new Mock<IGroupManager>(MockBehavior.Strict).Object;
        items ??= new Dictionary<object, object?>();
        if (accessValidator is null)
        {
            var platformAccess = new Mock<IPlatformAccessValidator>(MockBehavior.Strict);
            var access = new PlatformAccessToken
            {
                RoomId = DefaultRoomId,
                AssignmentId = "assignment",
                Name = "",
                Subject = "user",
                Role = "participant",
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
            };
            platformAccess.Setup(v => v.TryValidateRoomToken(
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    out access))
                .Returns(true);
            accessValidator = platformAccess.Object;
        }
        var logger = new Mock<ILogger<RoomHub>>().Object;
        var context = new Mock<HubCallerContext>(MockBehavior.Strict);
        context.SetupGet(c => c.ConnectionId).Returns("conn-1");
        context.SetupGet(c => c.ConnectionAborted).Returns(cancellationToken);
        context.SetupGet(c => c.Items).Returns(items);
        context.SetupGet(c => c.Features).Returns(new FeatureCollection());

        var hub = new RoomHub(roomManager, languageValidator, accessValidator, logger)
        {
            Context = context.Object,
            Clients = clients,
            Groups = groups
        };

        return hub;
    }

    private static CallCounter SetupTryGetRoom(string roomId, RoomState room, out Mock<IRoomManager> roomManager)
    {
        var counter = new CallCounter();
        roomManager = new Mock<IRoomManager>(MockBehavior.Strict);
        roomManager.Setup(m => m.TryGetRoom(It.Is<RoomId>(id => id.Value == roomId), out room))
            .Callback(() => counter.Count++)
            .Returns(true);
        return counter;
    }

    private sealed class CallCounter
    {
        public int Count { get; set; }
    }

    private static RoomState CreateRoomState(string roomId, string text = "")
        => new(
            new RoomId(roomId),
            new RoomName("Room"),
            new RoomLanguage("csharp"),
            new RoomText(text),
            new RoomVersion(1),
            DateTimeOffset.UtcNow,
            new AdminUser("admin", true));
}
