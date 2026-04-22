using DriverGateway.Core.Interfaces;
using DriverGateway.Core.Models;

namespace DriverGateway.Plugins.ModbusTcp;

internal enum ModbusArea
{
    Coil,
    DiscreteInput,
    InputRegister,
    HoldingRegister
}

internal sealed record ModbusAddress(ModbusArea Area, int Offset, int RegisterLength);

internal static class ModbusAddressParser
{
    public static bool TryParse(string rawAddress, TagDataType dataType, out ModbusAddress address)
    {
        address = new ModbusAddress(ModbusArea.HoldingRegister, 0, 1);
        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            return false;
        }

        var normalized = rawAddress.Trim().ToUpperInvariant();
        if (TryParseAreaPrefix(normalized, out address))
        {
            address = address with { RegisterLength = ResolveRegisterLength(dataType, address.Area) };
            return true;
        }

        return TryParseLegacyNumeric(normalized, dataType, out address);
    }

    private static bool TryParseAreaPrefix(string normalized, out ModbusAddress address)
    {
        address = new ModbusAddress(ModbusArea.HoldingRegister, 0, 1);
        var parts = normalized.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var offset) || offset < 0)
        {
            return false;
        }

        var area = parts[0] switch
        {
            "COIL" => ModbusArea.Coil,
            "DI" => ModbusArea.DiscreteInput,
            "IR" => ModbusArea.InputRegister,
            "HR" => ModbusArea.HoldingRegister,
            _ => (ModbusArea?)null
        };

        if (!area.HasValue)
        {
            return false;
        }

        address = new ModbusAddress(area.Value, offset, 1);
        return true;
    }

    private static bool TryParseLegacyNumeric(string normalized, TagDataType dataType, out ModbusAddress address)
    {
        address = new ModbusAddress(ModbusArea.HoldingRegister, 0, 1);
        if (!int.TryParse(normalized, out var value) || value <= 0)
        {
            return false;
        }

        var asText = normalized;
        var first = asText[0];
        ModbusArea area;

        switch (first)
        {
            case '0':
                area = ModbusArea.Coil;
                break;
            case '1':
                area = ModbusArea.DiscreteInput;
                break;
            case '3':
                area = ModbusArea.InputRegister;
                break;
            case '4':
                area = ModbusArea.HoldingRegister;
                break;
            default:
                return false;
        }

        var offset = value % 100000;
        if (offset == 0)
        {
            return false;
        }

        address = new ModbusAddress(area, offset - 1, ResolveRegisterLength(dataType, area));
        return true;
    }

    private static int ResolveRegisterLength(TagDataType dataType, ModbusArea area)
    {
        if (area is ModbusArea.Coil or ModbusArea.DiscreteInput)
        {
            return 1;
        }

        return dataType switch
        {
            TagDataType.Float => 2,
            TagDataType.Int32 => 2,
            TagDataType.UInt32 => 2,
            TagDataType.DWord => 2,
            TagDataType.Double => 4,
            _ => 1
        };
    }
}

public sealed class ModbusBatchPlanner : IBatchPlanner
{
    private readonly int _maxRegisterSpan;

    public ModbusBatchPlanner(int maxRegisterSpan = 120)
    {
        _maxRegisterSpan = maxRegisterSpan <= 0 ? 120 : maxRegisterSpan;
    }

    public IReadOnlyList<ReadBatchPlan> BuildBatches(IReadOnlyCollection<TagDefinition> tags)
    {
        var parsed = new List<(TagDefinition Tag, ModbusAddress Address)>();
        var fallback = new List<TagDefinition>();

        foreach (var tag in tags)
        {
            if (ModbusAddressParser.TryParse(tag.Address, tag.DataType, out var address))
            {
                parsed.Add((tag, address));
            }
            else
            {
                fallback.Add(tag);
            }
        }

        var batches = new List<ReadBatchPlan>();
        foreach (var areaGroup in parsed.GroupBy(static item => item.Address.Area))
        {
            var ordered = areaGroup.OrderBy(static item => item.Address.Offset).ToList();
            if (ordered.Count == 0)
            {
                continue;
            }

            var currentTags = new List<TagDefinition>();
            var start = ordered[0].Address.Offset;
            var endExclusive = ordered[0].Address.Offset + ordered[0].Address.RegisterLength;
            currentTags.Add(ordered[0].Tag);

            for (var index = 1; index < ordered.Count; index++)
            {
                var current = ordered[index];
                var currentStart = current.Address.Offset;
                var currentEndExclusive = current.Address.Offset + current.Address.RegisterLength;
                var mergedEndExclusive = Math.Max(endExclusive, currentEndExclusive);
                var mergedSpan = mergedEndExclusive - start;
                var contiguous = currentStart <= endExclusive;

                if (!contiguous || mergedSpan > _maxRegisterSpan)
                {
                    batches.Add(new ReadBatchPlan(
                        $"{areaGroup.Key}:{start}-{endExclusive - 1}",
                        currentTags.ToArray()));
                    currentTags = [current.Tag];
                    start = currentStart;
                    endExclusive = currentEndExclusive;
                    continue;
                }

                currentTags.Add(current.Tag);
                endExclusive = mergedEndExclusive;
            }

            batches.Add(new ReadBatchPlan(
                $"{areaGroup.Key}:{start}-{endExclusive - 1}",
                currentTags.ToArray()));
        }

        foreach (var tag in fallback)
        {
            batches.Add(new ReadBatchPlan($"RAW:{tag.Address}", [tag]));
        }

        return batches;
    }
}
