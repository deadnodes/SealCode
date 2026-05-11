using System.ComponentModel.DataAnnotations;

namespace Models.Configuration;

/// <summary>
/// Application configuration settings.
/// </summary>
public sealed class ApplicationConfiguration
{
    /// <summary>
    /// Gets or sets the admin users.
    /// </summary>
    [Required]
    [MinLength(1)]
    public required AdminUserConfiguration[] AdminUsers { get; init; }

    /// <summary>
    /// Gets or sets the allowed room languages.
    /// </summary>
    [Required]
    [MinLength(1)]
    public required string[] Languages { get; init; }

    /// <summary>
    /// Gets or sets the maximum number of users per room.
    /// </summary>
    [Range(1, 5)]
    public int MaxUsersPerRoom { get; init; }

    /// <summary>
    /// Gets the platform service token used by the BFF to create interview rooms.
    /// </summary>
    public string? PlatformServiceToken { get; init; }

    /// <summary>
    /// Gets the shared HMAC secret used to validate platform-scoped room access tokens.
    /// </summary>
    public string? PlatformSigningSecret { get; init; }
}

/// <summary>
/// Represents an admin user from configuration.
/// </summary>
public sealed class AdminUserConfiguration
{
    /// <summary>
    /// Gets or sets the admin display name.
    /// </summary>
    [Required]
    [MinLength(2)]
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the admin password.
    /// </summary>
    [Required]
    [MinLength(4)]
    public required string Password { get; init; }

    /// <summary>
    /// Gets or sets whether the admin can manage rooms created by other admins.
    /// </summary>
    public bool IsSuperAdmin { get; init; }
}
