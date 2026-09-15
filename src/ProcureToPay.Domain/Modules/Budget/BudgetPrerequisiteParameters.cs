using System.Text.Json;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Budget;

/// <summary>
/// Parameters persisted by the Approval prerequisite of a budget check (SPEC 08 Parámetros
/// <c>budget-check-owner/v1</c>). The exact wire form is
/// <c>{base_currency,contract_version,demands}</c> and every demand carries the attested position,
/// the line amount and the full Approval target; the owner parses this form to know exactly what to
/// reserve and never accepts a caller-built replacement.
/// </summary>
public sealed record BudgetPrerequisiteParameters(
    string BaseCurrency,
    string ContractVersion,
    IReadOnlyList<BudgetDemand> Demands)
{
    public string BaseCurrency { get; } = BudgetCodes.RequireBaseCurrency(BaseCurrency);

    public string ContractVersion { get; } =
        ContractVersion == BudgetCodes.PrerequisiteContractVersion
            ? ContractVersion
            : throw new DomainValidationException(
                $"The budget prerequisite contract version must be '{BudgetCodes.PrerequisiteContractVersion}'.");

    public IReadOnlyList<BudgetDemand> Demands { get; } =
        Demands is { Count: > 0 }
            ? Demands
            : throw new DomainValidationException("A budget prerequisite requires at least one demand.");

    /// <summary>
    /// Serializes the canonical wire form of the parameters. The result is persisted verbatim as
    /// <c>ExternalPrerequisite.parameters</c>, so its bytes are what the evidence digest covers.
    /// </summary>
    public string ToCanonicalJson() =>
        Approval.ApprovalCanonicalJson.Serialize(Approval.ApprovalCanonicalJson.Object(
            ("base_currency", Approval.ApprovalCanonicalJson.String(BaseCurrency)),
            ("contract_version", Approval.ApprovalCanonicalJson.String(ContractVersion)),
            ("demands", Approval.ApprovalCanonicalJson.Set(Demands.Select(
                BudgetFingerprints.DemandValue)))));

    /// <summary>
    /// Parses the persisted parameters with a closed schema: unknown properties, a duplicated target
    /// or a missing position fail closed instead of reserving something the contract never confirmed.
    /// </summary>
    public static BudgetPrerequisiteParameters Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new BudgetDependencyUnavailableException("The budget prerequisite has no parameters.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new BudgetDependencyUnavailableException("The budget prerequisite parameters are corrupted.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new BudgetDependencyUnavailableException("The budget prerequisite parameters are corrupted.");
            }

            var properties = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (properties.Length != 3 ||
                !properties.Contains("base_currency", StringComparer.Ordinal) ||
                !properties.Contains("contract_version", StringComparer.Ordinal) ||
                !properties.Contains("demands", StringComparer.Ordinal))
            {
                throw new BudgetDependencyUnavailableException(
                    "The budget prerequisite parameters do not match the published schema.");
            }

            var demands = new List<BudgetDemand>();
            foreach (var demand in root.GetProperty("demands").EnumerateArray())
            {
                demands.Add(ParseDemand(demand));
            }

            return new BudgetPrerequisiteParameters(
                root.GetProperty("base_currency").GetString() ?? string.Empty,
                root.GetProperty("contract_version").GetString() ?? string.Empty,
                demands);
        }
    }

    private static BudgetDemand ParseDemand(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new BudgetDependencyUnavailableException("A budget demand is corrupted.");
        }

        var costCenter = element.GetProperty("cost_center_ref");
        var spendCategory = element.GetProperty("spend_category_ref");
        var sourceLine = element.GetProperty("source_line");
        var costCenterRef = new BudgetCostCenterRef(
            costCenter.GetProperty("id").GetGuid(),
            costCenter.GetProperty("version").GetInt32());
        var spendCategoryRef = new BudgetSpendCategoryRef(
            spendCategory.GetProperty("code").GetString() ?? string.Empty,
            spendCategory.GetProperty("version").GetInt32(),
            spendCategory.GetProperty("digest").GetString() ?? string.Empty);
        var payload = BudgetPositionPayload.For(
            costCenterRef.Id,
            element.GetProperty("fiscal_year").GetInt32(),
            costCenterRef,
            spendCategoryRef);
        BudgetTarget? target = null;
        if (element.GetProperty("target").ValueKind == JsonValueKind.Object)
        {
            var targetElement = element.GetProperty("target");
            target = new BudgetTarget(
                targetElement.GetProperty("id").GetGuid(),
                targetElement.GetProperty("version").GetInt32(),
                targetElement.GetProperty("material_snapshot_digest").GetString() ?? string.Empty,
                targetElement.GetProperty("type").GetString() ?? string.Empty);
        }

        return new BudgetDemand(
            ParseDecimal(element.GetProperty("amount_base").GetString()),
            payload,
            sourceLine.GetProperty("id").GetGuid(),
            sourceLine.GetProperty("version").GetInt32(),
            sourceLine.GetProperty("content_digest").GetString() ?? string.Empty,
            target);
    }

    /// <summary>Wire decimals are invariant strings, never JSON numbers (SPEC 08 Canonicalización).</summary>
    private static decimal ParseDecimal(string? value) =>
        decimal.TryParse(
            value,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new BudgetDependencyUnavailableException("A budget demand amount is corrupted.");
}

/// <summary>
/// Registration of the real budget prerequisite processor (REQ-05, REQ-09): exactly one for
/// <c>budget-check-owner/v1</c>, and its workload must match the identity Approval persisted.
/// </summary>
public sealed record BudgetPrerequisiteProcessorRegistration(string AdapterId, string AdapterVersion, string ProcessorId)
{
    public string AdapterId { get; } = BudgetCodes.RequireBoundedToken(AdapterId, "Adapter id");

    public string AdapterVersion { get; } = BudgetCodes.RequireBoundedToken(AdapterVersion, "Adapter version");

    public string ProcessorId { get; } = BudgetCodes.RequireBoundedToken(ProcessorId, "Processor id");
}
