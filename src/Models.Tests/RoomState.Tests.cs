using FluentAssertions;

using Models.Exceptions;

namespace Models.Tests;

public sealed class RoomStateTests
{
    [Fact(DisplayName = "CtorShouldSetProperties")]
    [Trait("Category", "Unit")]
    public void CtorShouldSetProperties()
    {
        var roomId = RoomId.New();
        var name = new RoomName("Room");
        var language = new RoomLanguage("csharp");
        var text = new RoomText("text");
        var version = new RoomVersion(1);
        var updatedUtc = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var createdBy = new AdminUser("admin");
        var yjsState = new byte[] { 1, 2, 3 };

        var state = new RoomState(roomId, name, language, text, version, updatedUtc, createdBy, yjsState);

        state.RoomId.Should().Be(roomId);
        state.Name.Should().Be(name);
        state.Language.Should().Be(language);
        state.Text.Should().Be(text);
        state.Version.Should().Be(version);
        state.LastUpdatedUtc.Should().Be(updatedUtc);
        state.CreatedBy.Should().Be(createdBy);
        state.YjsState.Should().Equal(yjsState);
        state.ConnectedUserCount.Should().Be(0);
        state.ConnectedUsers.Should().BeEmpty();
    }

    [Fact(DisplayName = "CtorShouldUseEmptyYjsStateWhenNull")]
    [Trait("Category", "Unit")]
    public void CtorShouldUseEmptyYjsStateWhenNull()
    {
        var state = CreateState();

        state.YjsState.Should().NotBeNull();
        state.YjsState.Should().BeEmpty();
    }

    [Fact(DisplayName = "AddUserShouldAddUser")]
    [Trait("Category", "Unit")]
    public void AddUserShouldAddUser()
    {
        var state = CreateState();
        var connectionId = new ConnectionId("conn-1");
        var displayName = new RoomUser("Alice");

        state.AddUser(connectionId, displayName, 5);

        state.ConnectedUserCount.Should().Be(1);
        state.ConnectedUsers.Should().ContainKey(connectionId);
        state.ConnectedUsers[connectionId].Should().Be(displayName);
    }

    [Fact(DisplayName = "AddUserShouldThrowWhenRoomIsFull")]
    [Trait("Category", "Unit")]
    public void AddUserShouldThrowWhenRoomIsFull()
    {
        var state = CreateState();
        var connectionId = new ConnectionId("conn-1");
        var displayName = new RoomUser("Alice");

        state.AddUser(connectionId, displayName, 1);

        var action = () => state.AddUser(new ConnectionId("conn-2"), new RoomUser("Bob"), 1);

        action.Should().Throw<AddRoomUserException>();
    }

    [Fact(DisplayName = "AddUserShouldThrowWhenRoomUserAlreadyInUse")]
    [Trait("Category", "Unit")]
    public void AddUserShouldThrowWhenRoomUserAlreadyInUse()
    {
        var state = CreateState();

        state.AddUser(new ConnectionId("conn-1"), new RoomUser("Alice"), 5);

        var action = () => state.AddUser(new ConnectionId("conn-2"), new RoomUser("alice"), 5);

        action.Should().Throw<AddRoomUserException>();
    }

    [Fact(DisplayName = "AddPlatformUserShouldReplacePreviousConnectionForSameSubject")]
    [Trait("Category", "Unit")]
    public void AddPlatformUserShouldReplacePreviousConnectionForSameSubject()
    {
        var state = CreateState();

        state.AddPlatformUser(new ConnectionId("conn-1"), new RoomUser("Alice"), "platform-user", 5);
        state.AddPlatformUser(new ConnectionId("conn-2"), new RoomUser("Alice"), "platform-user", 5);

        state.ConnectedUserCount.Should().Be(1);
        state.ConnectedUsers.Should().NotContainKey(new ConnectionId("conn-1"));
        state.ConnectedUsers.Should().ContainKey(new ConnectionId("conn-2"));
        state.ConnectedUsers[new ConnectionId("conn-2")].Should().Be(new RoomUser("Alice"));
    }

    [Fact(DisplayName = "AddPlatformUserShouldKeepNameConflictForDifferentSubjects")]
    [Trait("Category", "Unit")]
    public void AddPlatformUserShouldKeepNameConflictForDifferentSubjects()
    {
        var state = CreateState();

        state.AddPlatformUser(new ConnectionId("conn-1"), new RoomUser("Alice"), "platform-user-1", 5);

        var action = () => state.AddPlatformUser(new ConnectionId("conn-2"), new RoomUser("alice"), "platform-user-2", 5);

        action.Should().Throw<AddRoomUserException>();
    }

    [Fact(DisplayName = "IsRoomUserInUseShouldIgnoreSameConnection")]
    [Trait("Category", "Unit")]
    public void IsRoomUserInUseShouldIgnoreSameConnection()
    {
        var state = CreateState();
        var connectionId = new ConnectionId("conn-1");

        state.AddUser(connectionId, new RoomUser("Alice"), 5);

        state.IsRoomUserInUse(connectionId, new RoomUser("alice")).Should().BeFalse();
    }

    [Fact(DisplayName = "TryGetRoomUserShouldReturnTrueWhenUserExists")]
    [Trait("Category", "Unit")]
    public void TryGetRoomUserShouldReturnTrueWhenUserExists()
    {
        var state = CreateState();
        var connectionId = new ConnectionId("conn-1");
        var displayName = new RoomUser("Alice");

        state.AddUser(connectionId, displayName, 5);

        var result = state.TryGetRoomUser(connectionId, out var found);

        result.Should().BeTrue();
        found.Should().Be(displayName);
    }

    [Fact(DisplayName = "TryGetRoomUserShouldReturnFalseWhenUserDoesNotExist")]
    [Trait("Category", "Unit")]
    public void TryGetRoomUserShouldReturnFalseWhenUserDoesNotExist()
    {
        var state = CreateState();

        var result = state.TryGetRoomUser(new ConnectionId("conn-missing"), out _);

        result.Should().BeFalse();
    }

    [Fact(DisplayName = "CreateUsersSnapshotShouldReturnCaseInsensitiveOrderedNames")]
    [Trait("Category", "Unit")]
    public void CreateUsersSnapshotShouldReturnCaseInsensitiveOrderedNames()
    {
        var state = CreateState();
        state.AddUser(new ConnectionId("conn-1"), new RoomUser("zoe"), 5);
        state.AddUser(new ConnectionId("conn-2"), new RoomUser("Alice"), 5);
        state.AddUser(new ConnectionId("conn-3"), new RoomUser("bob"), 5);

        var snapshot = state.CreateUsersSnapshot();

        snapshot.Should().Equal([new RoomUser("Alice"), new RoomUser("bob"), new RoomUser("zoe")]);
    }

    [Fact(DisplayName = "RemoveUserShouldRemoveUser")]
    [Trait("Category", "Unit")]
    public void RemoveUserShouldRemoveUser()
    {
        var state = CreateState();
        var connectionId = new ConnectionId("conn-1");
        var displayName = new RoomUser("Alice");

        state.AddUser(connectionId, displayName, 5);

        var removed = state.RemoveUser(connectionId, out var removedName);

        removed.Should().BeTrue();
        removedName.Should().Be(displayName);
        state.ConnectedUserCount.Should().Be(0);
    }

    [Fact(DisplayName = "UpdateTextShouldUpdateAndIncrementVersion")]
    [Trait("Category", "Unit")]
    public void UpdateTextShouldUpdateAndIncrementVersion()
    {
        var state = CreateState();
        var updatedUtc = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);

        var version = state.UpdateText(new RoomText("new"), updatedUtc);

        version.Value.Should().Be(2);
        state.Text.Value.Should().Be("new");
        state.Version.Value.Should().Be(2);
        state.LastUpdatedUtc.Should().Be(updatedUtc);
    }

    [Fact(DisplayName = "UpdateLanguageShouldUpdateAndIncrementVersion")]
    [Trait("Category", "Unit")]
    public void UpdateLanguageShouldUpdateAndIncrementVersion()
    {
        var state = CreateState();
        var updatedUtc = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var validator = new AllowAllLanguageValidator();

        var version = state.UpdateLanguage(new RoomLanguage("sql"), updatedUtc, validator);

        version.Value.Should().Be(2);
        state.Language.Value.Should().Be("sql");
        state.Version.Value.Should().Be(2);
        state.LastUpdatedUtc.Should().Be(updatedUtc);
    }

    [Fact(DisplayName = "UpdateLanguageShouldThrowWhenLanguageIsInvalid")]
    [Trait("Category", "Unit")]
    public void UpdateLanguageShouldThrowWhenLanguageIsInvalid()
    {
        var state = CreateState();
        var validator = new DenyAllLanguageValidator();

        var action = () => state.UpdateLanguage(new RoomLanguage("python"), DateTimeOffset.UtcNow, validator);

        action.Should().Throw<ArgumentException>();
    }

    [Fact(DisplayName = "TryUpdateYjsStateShouldReturnFalseWhenUnchanged")]
    [Trait("Category", "Unit")]
    public void TryUpdateYjsStateShouldReturnFalseWhenUnchanged()
    {
        var updatedUtc = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var initialUpdatedUtc = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var state = new RoomState(
            RoomId.New(),
            new RoomName("Room"),
            new RoomLanguage("csharp"),
            new RoomText("hello"),
            new RoomVersion(1),
            initialUpdatedUtc,
            new AdminUser("admin"),
            [1, 2]);

        var result = state.TryUpdateYjsState([1, 2], new RoomText("hello"), updatedUtc, out var version);

        result.Should().BeFalse();
        version.Value.Should().Be(1);
        state.Version.Value.Should().Be(1);
        state.LastUpdatedUtc.Should().Be(initialUpdatedUtc);
        state.YjsState.Should().Equal([1, 2]);
    }

    [Fact(DisplayName = "TryUpdateYjsStateShouldUpdateWhenChanged")]
    [Trait("Category", "Unit")]
    public void TryUpdateYjsStateShouldUpdateWhenChanged()
    {
        var updatedUtc = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var state = new RoomState(
            RoomId.New(),
            new RoomName("Room"),
            new RoomLanguage("csharp"),
            new RoomText("hello"),
            new RoomVersion(1),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new AdminUser("admin"),
            [1, 2]);

        var result = state.TryUpdateYjsState("\t"u8.ToArray(), new RoomText("updated"), updatedUtc, out var version);

        result.Should().BeTrue();
        version.Value.Should().Be(2);
        state.Version.Value.Should().Be(2);
        state.Text.Value.Should().Be("updated");
        state.YjsState.Should().Equal("\t"u8.ToArray());
        state.LastUpdatedUtc.Should().Be(updatedUtc);
    }

    [Fact(DisplayName = "TryUpdateYjsStateShouldThrowWhenStateIsNull")]
    [Trait("Category", "Unit")]
    public void TryUpdateYjsStateShouldThrowWhenStateIsNull()
    {
        var state = CreateState();

        var action = () => state.TryUpdateYjsState(null!, new RoomText("text"), DateTimeOffset.UtcNow, out _);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact(DisplayName = "IsCreatedByShouldReturnTrueWhenAdminMatches")]
    [Trait("Category", "Unit")]
    public void IsCreatedByShouldReturnTrueWhenAdminMatches()
    {
        var state = new RoomState(
            RoomId.New(),
            new RoomName("Room"),
            new RoomLanguage("csharp"),
            new RoomText("text"),
            new RoomVersion(1),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new AdminUser("Admin"));

        var admin = new AdminUser("admin");

        var result = state.IsCreatedBy(admin);

        result.Should().BeTrue();
    }

    [Fact(DisplayName = "IsCreatedByShouldReturnFalseWhenAdminDoesNotMatch")]
    [Trait("Category", "Unit")]
    public void IsCreatedByShouldReturnFalseWhenAdminDoesNotMatch()
    {
        var state = CreateState();
        var admin = new AdminUser("other");

        var result = state.IsCreatedBy(admin);

        result.Should().BeFalse();
    }

    [Fact(DisplayName = "CanDeleteShouldReturnTrueForSuperAdmin")]
    [Trait("Category", "Unit")]
    public void CanDeleteShouldReturnTrueForSuperAdmin()
    {
        var state = CreateState();
        var admin = new AdminUser("root", true);

        var result = state.CanDelete(admin);

        result.Should().BeTrue();
    }

    [Fact(DisplayName = "CanDeleteShouldReturnTrueForOwner")]
    [Trait("Category", "Unit")]
    public void CanDeleteShouldReturnTrueForOwner()
    {
        var state = CreateState();
        var admin = new AdminUser("admin");

        var result = state.CanDelete(admin);

        result.Should().BeTrue();
    }

    [Fact(DisplayName = "CanDeleteShouldReturnFalseForOtherAdmin")]
    [Trait("Category", "Unit")]
    public void CanDeleteShouldReturnFalseForOtherAdmin()
    {
        var state = CreateState();
        var admin = new AdminUser("other");

        var result = state.CanDelete(admin);

        result.Should().BeFalse();
    }

    private static RoomState CreateState(byte[]? yjsState = null)
        => new(
            RoomId.New(),
            new RoomName("Room"),
            new RoomLanguage("csharp"),
            new RoomText("text"),
            new RoomVersion(1),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new AdminUser("admin"),
            yjsState);

    private sealed class AllowAllLanguageValidator : ILanguageValidator
    {
        public IReadOnlyList<string> Languages => throw new NotImplementedException();

        public bool IsValid(RoomLanguage language) => true;
    }

    private sealed class DenyAllLanguageValidator : ILanguageValidator
    {
        public IReadOnlyList<string> Languages => throw new NotImplementedException();

        public bool IsValid(RoomLanguage language) => false;
    }
}
