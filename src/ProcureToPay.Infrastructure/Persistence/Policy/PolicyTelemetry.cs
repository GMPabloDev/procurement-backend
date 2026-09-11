using System.Diagnostics;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

/// <summary>
/// Minimized OpenTelemetry instrumentation for the policy engine (NFR-05).
/// Tags carry only correlation, policy version, scope, outcome and duration; no
/// snapshots, tokens, rule content or personal data are recorded.
/// </summary>
public static class PolicyTelemetry
{
    public const string SourceName = "ProcureToPay.Policy";
    public const string Version = "1.0.0";

    public static readonly ActivitySource Source = new(SourceName, Version);
}
