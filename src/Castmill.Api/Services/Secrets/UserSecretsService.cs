using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Secrets;

/// <summary>The closed set of secret kinds the app may store for a user.</summary>
public enum SecretKind
{
    FoundryEndpoint,
    FoundryKey,
    BrokerToken,
    /// <summary>Credential for an optional non-Foundry image provider (ADR-015).</summary>
    ImageProviderKey,
}

public interface IUserSecretsService
{
    Task SetAsync(Guid userId, SecretKind kind, string value, CancellationToken ct);
    /// <summary>Decrypted value for server-side use only — must never be written to a response or log.</summary>
    Task<string?> GetAsync(Guid userId, SecretKind kind, CancellationToken ct);
    Task<bool> RemoveAsync(Guid userId, SecretKind kind, CancellationToken ct);
    Task<IReadOnlyDictionary<SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct);
}

public sealed class UserSecretsService(
    CastmillDbContext db,
    ISecretCipher cipher,
    ITenantProvider tenant,
    TimeProvider clock) : IUserSecretsService
{
    // Stored under the reserved prefix that the plaintext /settings group refuses.
    private static string KeyFor(SecretKind kind) => $"secret.{kind}";

    public async Task SetAsync(Guid userId, SecretKind kind, string value, CancellationToken ct)
    {
        var key = KeyFor(kind);
        var encrypted = cipher.Encrypt(value);
        var now = clock.GetUtcNow();

        var setting = await db.UserSettings.SingleOrDefaultAsync(s => s.UserId == userId && s.Key == key, ct);
        if (setting is null)
        {
            db.UserSettings.Add(new UserSetting
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId ?? throw new InvalidOperationException("Secret writes require a tenant."),
                UserId = userId,
                Key = key,
                Value = encrypted,
                IsEncrypted = true,
                UpdatedAt = now,
            });
        }
        else
        {
            setting.Value = encrypted;
            setting.IsEncrypted = true;
            setting.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetAsync(Guid userId, SecretKind kind, CancellationToken ct)
    {
        var key = KeyFor(kind);
        var setting = await db.UserSettings
            .SingleOrDefaultAsync(s => s.UserId == userId && s.Key == key && s.IsEncrypted, ct);
        return setting is null ? null : cipher.Decrypt(setting.Value);
    }

    public async Task<bool> RemoveAsync(Guid userId, SecretKind kind, CancellationToken ct)
    {
        var key = KeyFor(kind);
        var setting = await db.UserSettings.SingleOrDefaultAsync(s => s.UserId == userId && s.Key == key, ct);
        if (setting is null)
        {
            return false;
        }
        db.UserSettings.Remove(setting);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyDictionary<SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct)
    {
        var keys = await db.UserSettings
            .Where(s => s.UserId == userId && s.IsEncrypted)
            .Select(s => new { s.Key, s.UpdatedAt })
            .ToListAsync(ct);

        var result = new Dictionary<SecretKind, DateTimeOffset>();
        foreach (var kind in Enum.GetValues<SecretKind>())
        {
            var match = keys.FirstOrDefault(k => k.Key == KeyFor(kind));
            if (match is not null)
            {
                result[kind] = match.UpdatedAt;
            }
        }
        return result;
    }
}
