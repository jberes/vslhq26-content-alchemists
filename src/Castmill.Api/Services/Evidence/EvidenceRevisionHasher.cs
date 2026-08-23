using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Castmill.Core;

namespace Castmill.Api.Services.Evidence;

internal static class EvidenceRevisionHasher
{
    private sealed record CanonicalBlock(
        string StableId,
        int Ordinal,
        string ContentHash,
        string LocatorKind,
        string LocatorJson);

    internal static string HashContent(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    internal static string HashApproved(IEnumerable<EvidenceBlock> blocks)
    {
        var canonical = blocks
            .Where(block => !block.IsExcluded)
            .OrderBy(block => block.Ordinal)
            .ThenBy(block => block.StableId, StringComparer.Ordinal)
            .Select(block => new CanonicalBlock(
                block.StableId,
                block.Ordinal,
                block.ContentHash,
                block.LocatorKind,
                block.LocatorJson))
            .ToList();

        return HashContent(JsonSerializer.Serialize(canonical));
    }
}