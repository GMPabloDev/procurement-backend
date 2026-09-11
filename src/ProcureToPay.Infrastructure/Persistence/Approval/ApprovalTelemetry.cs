using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Minimized OpenTelemetry instrumentation for the approval workflow (NFR-05). Tags carry only
/// operation, outcome, stage, attempt counts, duration and the correlation reference: never
/// decision reasons, snapshots, tokens, subject identifiers or any other personal data.
/// </summary>
public static class ApprovalTelemetry
{
    public const string SourceName = "ProcureToPay.Approval";
    public const string Version = "1.0.0";

    public static readonly ActivitySource Source = new(SourceName, Version);
    public static readonly Meter Meter = new(SourceName, Version);

    public static readonly Counter<long> Submissions = Meter.CreateCounter<long>(
        "approval.submissions",
        description: "Approval case submissions by result.");

    public static readonly Counter<long> Assignments = Meter.CreateCounter<long>(
        "approval.assignments",
        description: "Approval task assignments and reassignments.");

    public static readonly Counter<long> Unassigned = Meter.CreateCounter<long>(
        "approval.unassigned",
        description: "Approval requirements left without an eligible candidate.");

    public static readonly Counter<long> Decisions = Meter.CreateCounter<long>(
        "approval.decisions",
        description: "Approval decisions by action and result.");

    public static readonly Counter<long> Conflicts = Meter.CreateCounter<long>(
        "approval.conflicts",
        description: "Approval conflicts such as stale versions or reused keys.");

    public static readonly Counter<long> Reconciliations = Meter.CreateCounter<long>(
        "approval.reconciliations",
        description: "Authority reconciliation passes and their effects.");

    public static readonly Counter<long> Dispatches = Meter.CreateCounter<long>(
        "approval.dispatches",
        description: "Outbox dispatch attempts by result.");

    public static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>(
        "approval.dead_letters",
        description: "Outbox events parked as dead letters.");

    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "approval.operation.duration",
        unit: "ms",
        description: "Duration of approval operations.");

    public static Activity? Start(string operation, string correlationReference)
    {
        var activity = Source.StartActivity($"approval.{operation.ToLowerInvariant()}");
        activity?.SetTag("approval.operation", operation);
        activity?.SetTag("approval.correlation_reference", correlationReference);
        return activity;
    }

    /// <summary>Monotonic start mark for a duration measurement.</summary>
    public static long Now() => Stopwatch.GetTimestamp();

    public static double ElapsedMilliseconds(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    /// <summary>Records one ingestion outcome (REQ-01, NFR-05).</summary>
    public static void RecordSubmission(string outcome) => Submissions.Add(1, Tags("SUBMIT", outcome));

    /// <summary>Records one assignment or reassignment, and the absence of a candidate (REQ-04).</summary>
    public static void RecordAssignment(string outcome) => Assignments.Add(1, Tags("ASSIGN", outcome));

    /// <summary>Records a requirement left without an eligible candidate (REQ-04, NFR-05).</summary>
    public static void RecordUnassigned() => Unassigned.Add(1, Tags("ASSIGN", "UNASSIGNED"));

    /// <summary>Records one decision outcome; only the action name and outcome, never its reason.</summary>
    public static void RecordDecision(string action, string outcome) =>
        Decisions.Add(1, Tags("DECIDE", $"{action.ToUpperInvariant()}_{outcome.ToUpperInvariant()}"));

    /// <summary>Records a contract conflict by the operation that detected it (NFR-05).</summary>
    public static void RecordConflict(string operation) => Conflicts.Add(1, Tags(operation, "CONFLICT"));

    /// <summary>Records one completed reconciliation pass and what it changed (REQ-04, NFR-03).</summary>
    public static void RecordReconciliation(int reassigned, int unassigned)
    {
        Reconciliations.Add(1, Tags("RECONCILE", "COMPLETED"));
        if (reassigned > 0)
        {
            Assignments.Add(reassigned, Tags("ASSIGN", "REASSIGNED"));
        }

        if (unassigned > 0)
        {
            Unassigned.Add(unassigned, Tags("ASSIGN", "UNASSIGNED"));
        }
    }

    /// <summary>Records the duration of one approval operation.</summary>
    public static void RecordDuration(string operation, string outcome, long startTimestamp) =>
        OperationDuration.Record(ElapsedMilliseconds(startTimestamp), Tags(operation, outcome));

    public static void Complete(Activity? activity, string outcome, long startTimestamp)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("approval.outcome", outcome);
        activity.SetTag("approval.duration_ms", ElapsedMilliseconds(startTimestamp));
    }

    /// <summary>Minimized metric tags: no reasons, snapshots or personal data (NFR-05).</summary>
    public static TagList Tags(string operation, string outcome) => new()
    {
        { "approval.operation", operation },
        { "approval.outcome", outcome }
    };

    /// <summary>
    /// Records one outbox dispatch attempt. Only the outcome label and the attempt count are
    /// emitted; the error, when present, is reduced to its exception type by the caller.
    /// </summary>
    public static void RecordDispatch(string outcome, int attempts)
    {
        Dispatches.Add(1, Tags("DISPATCH", outcome));
        if (string.Equals(outcome, "DEAD_LETTER", StringComparison.Ordinal))
        {
            DeadLetters.Add(1, Tags("DISPATCH", outcome));
        }
    }
}
