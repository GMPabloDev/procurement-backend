using Microsoft.Extensions.Configuration;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Opaque identity of this application instance, used as the lease owner of the reconciliation
/// runs and of the outbox dispatcher (REQ-10). It is configured with <c>Approval:InstanceId</c>
/// or derived from the machine and process, and is always 1-120 characters of an opaque alphabet.
/// </summary>
public sealed class ApprovalInstanceIdentity
{
    public ApprovalInstanceIdentity(IConfiguration configuration)
    {
        var configured = configuration["Approval:InstanceId"];
        Owner = Normalize(string.IsNullOrWhiteSpace(configured)
            ? $"instance-{Environment.MachineName}-{Environment.ProcessId}"
            : configured);
    }

    public string Owner { get; }

    private static string Normalize(string value)
    {
        var filtered = new string(value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-')
            .ToArray());
        if (filtered.Length == 0)
        {
            filtered = $"instance-{Guid.NewGuid():N}";
        }

        return filtered.Length > 120 ? filtered[..120] : filtered;
    }
}
