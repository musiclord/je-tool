using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 完整性硬閘的唯一 Application seam：嚴格解析目前 validation summary 的 raw part A／B
/// facts，交由 AuditCore 裁定，並提供所有 handler 共用的 fail-closed 前置方法與 wire renderer。
/// </summary>
internal static class CompletenessEligibilitySupport
{
    internal static async Task<RuleRunRecord> RequireCurrentAsync(
        IRuleRunStore runStore,
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var run = await runStore.FindLatestAsync(
            projectId,
            RuleRunKinds.Validate,
            cancellationToken);

        return Require(run);
    }

    internal static RuleRunRecord Require(RuleRunRecord? run)
    {
        var decision = EvaluateSummary(run);
        if (!decision.IsEligible)
        {
            var reason = decision.Reason
                ?? throw new InvalidOperationException(
                    "AuditCore returned an ineligible completeness decision without a reason.");
            throw new JetActionException(
                JetErrorCodes.CompletenessPrerequisiteFailed,
                reason);
        }

        return run!;
    }

    internal static CompletenessEligibilityDecision EvaluateSummary(RuleRunRecord? run)
    {
        if (run is null || !string.Equals(run.RunKind, RuleRunKinds.Validate, StringComparison.Ordinal))
        {
            return JetAuditProgram.EvaluateCompletenessEligibility(
                new CompletenessEligibilityFacts(
                    HasCurrentValidationRun: false,
                    IsCurrentLogicVersion: false,
                    PartARowCountMatch: null,
                    PartAAmountMatch: null,
                    PartBApplicable: null,
                    PartBDifferenceAccountCount: null));
        }

        var parsed = ParseSummaryFacts(run);
        return JetAuditProgram.EvaluateCompletenessEligibility(
            new CompletenessEligibilityFacts(
                HasCurrentValidationRun: true,
                parsed.IsCurrentLogicVersion,
                parsed.PartARowCountMatch,
                parsed.PartAAmountMatch,
                parsed.PartBApplicable,
                parsed.PartBDifferenceAccountCount));
    }

    internal static CompletenessEligibilityWire ToWire(CompletenessEligibilityDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new CompletenessEligibilityWire(decision.IsEligible, decision.Reason);
    }

    /// <summary>
    /// Resume 兼容：舊的 current-version summary 尚無 eligibility 時，在回應邊界補上；
    /// 已存在的同名欄位也以 raw facts 的後端裁定覆寫，絕不信任存檔的衍生布林值。
    /// </summary>
    internal static JsonElement ToWireSummary(RuleRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);

        using var summary = JsonDocument.Parse(run.SummaryJson);
        if (summary.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Validation run summary must be a JSON object.");
        }
        ValidationSummaryShapeValidator.Require(summary.RootElement);

        var decision = EvaluateSummary(run);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var wroteCompleteness = false;
            foreach (var property in summary.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "completenessTest", StringComparison.Ordinal))
                {
                    property.WriteTo(writer);
                    continue;
                }

                if (!wroteCompleteness)
                {
                    WriteCompletenessWithEligibility(writer, property.Value, decision);
                    wroteCompleteness = true;
                }
            }

            if (!wroteCompleteness)
            {
                WriteCompletenessWithEligibility(writer, default, decision);
            }

            writer.WriteEndObject();
        }

        using var rendered = JsonDocument.Parse(stream.ToArray());
        return rendered.RootElement.Clone();
    }

    private static ParsedSummaryFacts ParseSummaryFacts(RuleRunRecord run)
    {
        try
        {
            using var summary = JsonDocument.Parse(run.SummaryJson);
            var root = summary.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ParsedSummaryFacts.Invalid;
            }

            var isCurrentLogicVersion = TryGetUniqueProperty(root, "resultRef", out var resultRef)
                && resultRef.ValueKind == JsonValueKind.Object
                && TryGetUniqueProperty(resultRef, "logicVersion", out var version)
                && version.ValueKind == JsonValueKind.String
                && string.Equals(
                    version.GetString(),
                    RuleLogicVersions.Validation,
                    StringComparison.Ordinal);

            if (isCurrentLogicVersion)
            {
                ValidationSummaryShapeValidator.Require(root);
            }

            if (!TryGetUniqueProperty(root, "completenessTest", out var completeness)
                || completeness.ValueKind != JsonValueKind.Object
                || !TryGetUniqueProperty(completeness, "partA", out var partA)
                || partA.ValueKind != JsonValueKind.Object)
            {
                return new ParsedSummaryFacts(
                    isCurrentLogicVersion,
                    PartARowCountMatch: null,
                    PartAAmountMatch: null,
                    PartBApplicable: null,
                    PartBDifferenceAccountCount: null);
            }

            var rowCountMatch = ReadBoolean(partA, "rowCountMatch");
            var amountMatch = ReadBoolean(partA, "amountMatch");
            var applicable = ReadPartBApplicability(completeness);
            var differenceCount = ReadNonNegativeInt64(completeness, "diffAccountCount");

            return new ParsedSummaryFacts(
                isCurrentLogicVersion,
                rowCountMatch,
                amountMatch,
                applicable,
                differenceCount);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            return ParsedSummaryFacts.Invalid;
        }
    }

    private static bool? ReadBoolean(JsonElement source, string propertyName)
    {
        if (!TryGetUniqueProperty(source, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool? ReadPartBApplicability(JsonElement completeness)
    {
        if (!TryGetUniqueProperty(completeness, "naReason", out var naReason))
        {
            return null;
        }

        return naReason.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.String when !string.IsNullOrWhiteSpace(naReason.GetString()) => false,
            _ => null
        };
    }

    private static long? ReadNonNegativeInt64(JsonElement source, string propertyName)
    {
        if (!TryGetUniqueProperty(source, propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
            || parsed < 0)
        {
            return null;
        }

        return parsed;
    }

    private static bool TryGetUniqueProperty(
        JsonElement source,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        if (source.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = false;
        foreach (var property in source.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found)
            {
                value = default;
                return false;
            }

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static void WriteCompletenessWithEligibility(
        Utf8JsonWriter writer,
        JsonElement completeness,
        CompletenessEligibilityDecision decision)
    {
        writer.WritePropertyName("completenessTest");
        writer.WriteStartObject();
        if (completeness.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in completeness.EnumerateObject())
            {
                if (!string.Equals(property.Name, "eligibility", StringComparison.Ordinal))
                {
                    property.WriteTo(writer);
                }
            }
        }

        writer.WritePropertyName("eligibility");
        JsonSerializer.Serialize(writer, ToWire(decision), JetJsonStorage.Options);
        writer.WriteEndObject();
    }

    private sealed record ParsedSummaryFacts(
        bool IsCurrentLogicVersion,
        bool? PartARowCountMatch,
        bool? PartAAmountMatch,
        bool? PartBApplicable,
        long? PartBDifferenceAccountCount)
    {
        internal static ParsedSummaryFacts Invalid { get; } =
            new(
                IsCurrentLogicVersion: false,
                PartARowCountMatch: null,
                PartAAmountMatch: null,
                PartBApplicable: null,
                PartBDifferenceAccountCount: null);
    }
}

internal sealed record CompletenessEligibilityWire(
    bool IsEligible,
    string? Reason);
