using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Castmill.Api.Data;

/// <summary>
/// Establishes EF's ambient execution-strategy scope for an explicit transaction without
/// replaying a non-idempotent HTTP mutation after an ambiguous commit.
/// </summary>
public sealed class NonReplayingExecutionStrategy(DbContext context)
    : ExecutionStrategy(context, maxRetryCount: 0, maxRetryDelay: TimeSpan.Zero)
{
    protected override bool ShouldRetryOn(Exception exception) => false;
}