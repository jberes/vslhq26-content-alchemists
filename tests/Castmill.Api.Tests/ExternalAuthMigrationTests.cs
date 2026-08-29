using Castmill.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class ExternalAuthMigrationTests(CastmillApiFactory factory)
{
    [Fact]
    public void Callback_proof_rollback_deletes_ephemeral_attempts_before_non_null_alter()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var migrator = db.GetService<IMigrator>();

        var script = migrator.GenerateScript(
            "20260829213041_ExternalAuthCallbackProof",
            "20260829182753_ExternalAuthFoundation");

        var deleteAt = script.IndexOf(
            "DELETE FROM [ExternalAuthAttempts]",
            StringComparison.Ordinal);
        var alterAt = script.IndexOf(
            "ALTER TABLE [ExternalAuthAttempts] ALTER COLUMN [ExchangeCodeHash]",
            StringComparison.Ordinal);
        Assert.True(deleteAt >= 0, "Rollback must explicitly delete ephemeral attempts.");
        Assert.True(alterAt > deleteAt, "Rollback cleanup must precede the non-null column alter.");
    }
}