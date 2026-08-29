using Castmill.Api.Data;
using Castmill.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Auth;

public sealed record ExternalLoginMapping(
    string LoginProvider,
    string ProviderKey,
    string ProviderDisplayName);

public sealed record AccountCreationResult(CastmillUser? User, IdentityResult Result)
{
    public bool Succeeded => User is not null && Result.Succeeded;
}

public interface IAccountService
{
    Task<AccountCreationResult> CreateAsync(
        string email,
        string displayName,
        string? password = null,
        ExternalLoginMapping? externalLogin = null,
        CancellationToken ct = default);

    Task<CastmillUser?> FindByExternalLoginAsync(
        string loginProvider,
        string providerKey,
        CancellationToken ct = default);

    Task<IdentityResult> LinkExternalLoginAsync(
        CastmillUser user,
        ExternalLoginMapping externalLogin,
        CancellationToken ct = default);
}

public sealed class AccountService(
    CastmillDbContext db,
    UserManager<CastmillUser> users,
    TimeProvider clock) : IAccountService
{
    public async Task<AccountCreationResult> CreateAsync(
        string email,
        string displayName,
        string? password = null,
        ExternalLoginMapping? externalLogin = null,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = displayName, CreatedAt = now };
        var user = new CastmillUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            TenantId = tenant.Id,
            DisplayName = displayName,
            CreatedAt = now,
        };

        if (db.Database.CurrentTransaction is not null)
        {
            return await CreateCoreAsync(tenant, user, password, externalLogin, ct);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        AccountCreationResult? outcome = null;

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            outcome = await CreateCoreAsync(tenant, user, password, externalLogin, ct);
            if (!outcome.Succeeded)
            {
                await transaction.RollbackAsync(ct);
                return;
            }
            await transaction.CommitAsync(ct);
        });

        if (outcome is null)
        {
            throw new InvalidOperationException("Account creation completed without a result.");
        }

        if (!outcome.Succeeded)
        {
            db.ChangeTracker.Clear();
        }

        return outcome;
    }

    private async Task<AccountCreationResult> CreateCoreAsync(
        Tenant tenant,
        CastmillUser user,
        string? password,
        ExternalLoginMapping? externalLogin,
        CancellationToken ct)
    {
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        var createResult = password is null
            ? await users.CreateAsync(user)
            : await users.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            return new(null, createResult);
        }

        if (externalLogin is not null)
        {
            var loginResult = await users.AddLoginAsync(user, ToUserLoginInfo(externalLogin));
            if (!loginResult.Succeeded)
            {
                return new(null, loginResult);
            }
        }

        return new(user, IdentityResult.Success);
    }

    public Task<CastmillUser?> FindByExternalLoginAsync(
        string loginProvider,
        string providerKey,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return users.FindByLoginAsync(loginProvider, providerKey);
    }

    public async Task<IdentityResult> LinkExternalLoginAsync(
        CastmillUser user,
        ExternalLoginMapping externalLogin,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await users.AddLoginAsync(user, ToUserLoginInfo(externalLogin));
    }

    private static UserLoginInfo ToUserLoginInfo(ExternalLoginMapping login) =>
        new(login.LoginProvider, login.ProviderKey, login.ProviderDisplayName);
}