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
    /// <summary>
    /// Legacy shared credential for a non-Foundry image provider (ADR-015). Kept as a
    /// fallback for workspaces that stored a key before each vendor had its own slot —
    /// new keys go in <see cref="NanoBananaKey"/> / <see cref="OpenAiImageKey"/>.
    /// </summary>
    ImageProviderKey,
    /// <summary>Google AI Studio (Gemini) API key — the "Nano Banana" image models.</summary>
    NanoBananaKey,
    /// <summary>OpenAI API key for the gpt-image family, called directly rather than through Foundry.</summary>
    OpenAiImageKey,
    /// <summary>Credential for the non-Foundry second-pass text provider (ADR-020).</summary>
    TechEditKey,
    /// <summary>Bearer token for the customer knowledge-base gateway the Tech Edit consults.</summary>
    KnowledgeBaseToken,
    /// <summary>Fine-grained GitHub PAT for the optional git publishing backend (ADR-021).</summary>
    GitHubToken,
}

public interface IUserSecretsService
{
    Task SetAsync(Guid userId, SecretKind kind, string value, CancellationToken ct);
    /// <summary>Decrypted value for server-side use only — must never be written to a response or log.</summary>
    Task<string?> GetAsync(Guid userId, SecretKind kind, CancellationToken ct);
    Task<bool> RemoveAsync(Guid userId, SecretKind kind, CancellationToken ct);
    Task<IReadOnlyDictionary<SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Stored secrets that no longer decrypt under the current encryption key (the key was
    /// rotated after they were saved). They read as absent everywhere else; the Settings page
    /// asks for them again. Default keeps fakes compiling.
    /// </summary>
    Task<IReadOnlySet<SecretKind>> UnreadableAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<SecretKind>>(new HashSet<SecretKind>());
}

public sealed class UserSecretsService(
    CastmillDbContext db,
    ISecretCipher cipher,
    ITenantProvider tenant,
    TimeProvider clock,
    ILogger<UserSecretsService> logger) : IUserSecretsService, IDisposable
{
    public void Dispose() => _gate.Dispose();

    // Stored under the reserved prefix that the plaintext /settings group refuses.
    private static string KeyFor(SecretKind kind) => $"secret.{kind}";

    // Reads are cached for the life of the scope and serialised behind this gate (ADR-064).
    // Image generation fans several renders out in parallel over ONE request scope, and every
    // provider resolves its credentials on the way through — concurrent reads on a scoped
    // DbContext threw "A second operation was started on this context instance" and failed the
    // take. Secrets cannot change underneath a single request, so one read each is also correct.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<SecretKind, string?> _cache = [];

    public async Task SetAsync(Guid userId, SecretKind kind, string value, CancellationToken ct)
    {
        var key = KeyFor(kind);
        // Trimmed before encryption (ADR-073). A key pasted with a trailing newline or a
        // stray quote was stored and sent verbatim, and the provider answered "API key not
        // valid" — a failure that reads as a wrong key rather than a copy-paste artefact.
        var encrypted = cipher.Encrypt(value.Trim());
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
        _cache.Remove(kind);
    }

    public async Task<string?> GetAsync(Guid userId, SecretKind kind, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(kind, out var cached))
            {
                return cached;
            }
            var value = await ReadAsync(userId, kind, ct);
            _cache[kind] = value;
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> ReadAsync(Guid userId, SecretKind kind, CancellationToken ct)
    {
        var key = KeyFor(kind);
        var setting = await db.UserSettings
            .SingleOrDefaultAsync(s => s.UserId == userId && s.Key == key && s.IsEncrypted, ct);
        if (setting is null)
        {
            return null;
        }
        try
        {
            return cipher.Decrypt(setting.Value);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // A secret saved under a previous Castmill:EncryptionKey. Reading it as "not set"
            // keeps every readiness call answering instead of failing the whole page; the
            // Settings status marks it for re-entry.
            logger.LogWarning(ex, "Stored secret {Kind} cannot be decrypted under the current encryption key.", kind);
            return null;
        }
    }

    public async Task<IReadOnlySet<SecretKind>> UnreadableAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.UserSettings
            .Where(s => s.UserId == userId && s.IsEncrypted)
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);
        var unreadable = new HashSet<SecretKind>();
        foreach (var kind in Enum.GetValues<SecretKind>())
        {
            var row = rows.FirstOrDefault(r => r.Key == KeyFor(kind));
            if (row is null)
            {
                continue;
            }
            try
            {
                cipher.Decrypt(row.Value);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                unreadable.Add(kind);
            }
        }
        return unreadable;
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
        _cache.Remove(kind);
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
