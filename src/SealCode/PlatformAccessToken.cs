using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Models.Configuration;

namespace SealCode;

#pragma warning disable CA1515 // Public because RoomHub is public for tests.
/// <summary>
/// Represents a validated platform-scoped room access token.
/// </summary>
public sealed class PlatformAccessToken
{
    /// <summary>
    /// Gets the authorized room identifier.
    /// </summary>
    public required string RoomId { get; init; }

    /// <summary>
    /// Gets the platform interview assignment identifier.
    /// </summary>
    public required string AssignmentId { get; init; }

    /// <summary>
    /// Gets the participant display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the platform subject identifier.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Gets the platform participant role.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Gets the expiration timestamp as Unix seconds.
    /// </summary>
    public long ExpiresAtUnix { get; init; }
}

/// <summary>
/// Defines platform request and room-token validation.
/// </summary>
public interface IPlatformAccessValidator
{
    /// <summary>
    /// Determines whether the request came from the trusted platform BFF.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>True when the service token is valid; otherwise false.</returns>
    bool IsServiceRequest(HttpContext context);

    /// <summary>
    /// Validates a room token and returns its decoded access payload.
    /// </summary>
    /// <param name="token">The signed token.</param>
    /// <param name="expectedRoomId">The room identifier expected by the route or hub.</param>
    /// <param name="access">The decoded access payload when validation succeeds.</param>
    /// <returns>True when the token is valid for the expected room; otherwise false.</returns>
    bool TryValidateRoomToken(string? token, string expectedRoomId, out PlatformAccessToken access);
}

/// <summary>
/// Validates BFF service requests and signed room access tokens.
/// </summary>
public sealed class PlatformAccessValidator : IPlatformAccessValidator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlatformAccessValidator"/> class.
    /// </summary>
    /// <param name="options">The application configuration.</param>
    public PlatformAccessValidator(IOptions<ApplicationConfiguration> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _settings = options.Value;
    }

    /// <summary>
    /// Determines whether the request came from the trusted platform BFF.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>True when the service token is valid; otherwise false.</returns>
    public bool IsServiceRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var expected = (_settings.PlatformServiceToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var provided = context.Request.Headers["X-Service-Token"].ToString().Trim();
        return FixedTimeEquals(provided, expected);
    }

    /// <summary>
    /// Validates a room token and returns its decoded access payload.
    /// </summary>
    /// <param name="token">The signed token.</param>
    /// <param name="expectedRoomId">The room identifier expected by the route or hub.</param>
    /// <param name="access">The decoded access payload when validation succeeds.</param>
    /// <returns>True when the token is valid for the expected room; otherwise false.</returns>
    public bool TryValidateRoomToken(string? token, string expectedRoomId, out PlatformAccessToken access)
    {
        access = default!;
        var secret = (_settings.PlatformSigningSecret ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(secret))
        {
            access = new PlatformAccessToken
            {
                RoomId = expectedRoomId,
                AssignmentId = string.Empty,
                Name = string.Empty,
                Subject = string.Empty,
                Role = "legacy",
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
            };
            return true;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        var expectedSignature = Sign(parts[0], secret);
        if (!FixedTimeEquals(parts[1], expectedSignature))
        {
            return false;
        }

        PlatformTokenPayload? payload;
        try
        {
            var raw = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            payload = JsonSerializer.Deserialize<PlatformTokenPayload>(raw, _jsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }

        if (payload is null || !FixedTimeEquals(payload.RoomId ?? string.Empty, expectedRoomId))
        {
            return false;
        }

        if (payload.Exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return false;
        }

        access = new PlatformAccessToken
        {
            RoomId = payload.RoomId ?? string.Empty,
            AssignmentId = payload.AssignmentId ?? string.Empty,
            Name = string.IsNullOrWhiteSpace(payload.Name) ? "Participant" : payload.Name.Trim(),
            Subject = payload.Sub ?? string.Empty,
            Role = payload.Role ?? "participant",
            ExpiresAtUnix = payload.Exp
        };
        return true;
    }

    private static string Sign(string body, string secret)
    {
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(body));
        return Base64UrlEncode(signature);
    }

    private static string Base64UrlEncode(byte[] input)
        => Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return (leftBytes.Length == rightBytes.Length) && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed class PlatformTokenPayload
    {
        [JsonPropertyName("room_id")]
        public string? RoomId { get; init; }

        [JsonPropertyName("assignment_id")]
        public string? AssignmentId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("sub")]
        public string? Sub { get; init; }

        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("exp")]
        public long Exp { get; init; }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ApplicationConfiguration _settings;
}
#pragma warning restore CA1515 // Public because RoomHub is public for tests.
