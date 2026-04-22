using DriverGateway.Core.Interfaces;
using DriverGateway.Core.Models;

namespace DriverGateway.Plugins.S7Comm;

internal enum S7ValueKind
{
    Bit,
    Byte,
    Word,
    DWord
}

internal sealed record S7Address(int DbNumber, int ByteOffset, int? BitOffset, int ByteLength, S7ValueKind Kind);

internal static class S7AddressParser
{
    public static bool TryParse(string rawAddress, out S7Address address)
    {
        address = new S7Address(0, 0, null, 0, S7ValueKind.Byte);

        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            return false;
        }

        // Supported forms:
        // DB1.DBX0.0
        // DB1.DBB2
        // DB1.DBW4
        // DB1.DBD8
        var normalized = rawAddress.Trim().ToUpperInvariant();
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].StartsWith("DB", StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[0][2..], out var dbNumber) || dbNumber < 1)
        {
            return false;
        }

        var area = parts[1];
        if (area.StartsWith("DBX", StringComparison.Ordinal))
        {
            if (parts.Length != 3)
            {
                return false;
            }

            if (!int.TryParse(area[3..], out var byteOffset) || byteOffset < 0)
            {
                return false;
            }

            if (!int.TryParse(parts[2], out var bitOffset) || bitOffset is < 0 or > 7)
            {
                return false;
            }

            address = new S7Address(dbNumber, byteOffset, bitOffset, 1, S7ValueKind.Bit);
            return true;
        }

        if (area.StartsWith("DBB", StringComparison.Ordinal) &&
            int.TryParse(area[3..], out var byteAddress) &&
            byteAddress >= 0)
        {
            address = new S7Address(dbNumber, byteAddress, null, 1, S7ValueKind.Byte);
            return true;
        }

        if (area.StartsWith("DBW", StringComparison.Ordinal) &&
            int.TryParse(area[3..], out var wordAddress) &&
            wordAddress >= 0)
        {
            address = new S7Address(dbNumber, wordAddress, null, 2, S7ValueKind.Word);
            return true;
        }

        if (area.StartsWith("DBD", StringComparison.Ordinal) &&
            int.TryParse(area[3..], out var dWordAddress) &&
            dWordAddress >= 0)
        {
            address = new S7Address(dbNumber, dWordAddress, null, 4, S7ValueKind.DWord);
            return true;
        }

        return false;
    }
}

public sealed class S7BatchPlanner : IBatchPlanner
{
    private readonly int _maxBlockBytes;

    public S7BatchPlanner(int maxBlockBytes = 222)
    {
        _maxBlockBytes = maxBlockBytes <= 0 ? 222 : maxBlockBytes;
    }

    public IReadOnlyList<ReadBatchPlan> BuildBatches(IReadOnlyCollection<TagDefinition> tags)
    {
        var parsed = new List<(TagDefinition Tag, S7Address Address)>();
        var fallback = new List<TagDefinition>();

        foreach (var tag in tags)
        {
            if (S7AddressParser.TryParse(tag.Address, out var address))
            {
                parsed.Add((tag, address));
            }
            else
            {
                fallback.Add(tag);
            }
        }

        var batches = new List<ReadBatchPlan>();
        foreach (var group in parsed.GroupBy(static item => item.Address.DbNumber))
        {
            var ordered = group.OrderBy(static item => item.Address.ByteOffset).ToList();
            if (ordered.Count == 0)
            {
                continue;
            }

            var currentTags = new List<TagDefinition>();
            var batchStart = ordered[0].Address.ByteOffset;
            var batchEndExclusive = ordered[0].Address.ByteOffset + ordered[0].Address.ByteLength;
            currentTags.Add(ordered[0].Tag);

            for (var index = 1; index < ordered.Count; index++)
            {
                var current = ordered[index];
                var currentStart = current.Address.ByteOffset;
                var currentEndExclusive = current.Address.ByteOffset + current.Address.ByteLength;
                var mergedEndExclusive = Math.Max(batchEndExclusive, currentEndExclusive);
                var mergedSpan = mergedEndExclusive - batchStart;
                var contiguous = currentStart <= batchEndExclusive;

                if (!contiguous || mergedSpan > _maxBlockBytes)
                {
                    batches.Add(new ReadBatchPlan(
                        BatchKey: $"DB{group.Key}:{batchStart}-{batchEndExclusive - 1}",
                        Tags: currentTags.ToArray()));

                    currentTags = [current.Tag];
                    batchStart = currentStart;
                    batchEndExclusive = currentEndExclusive;
                    continue;
                }

                currentTags.Add(current.Tag);
                batchEndExclusive = mergedEndExclusive;
            }

            batches.Add(new ReadBatchPlan(
                BatchKey: $"DB{group.Key}:{batchStart}-{batchEndExclusive - 1}",
                Tags: currentTags.ToArray()));
        }

        foreach (var tag in fallback)
        {
            batches.Add(new ReadBatchPlan($"RAW:{tag.Address}", [tag]));
        }

        return batches;
    }
}
