using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests;

/// <summary>
/// HTTP evidence for assignment, reconciliation and the administrative surface (REQ-04, REQ-05,
/// REQ-09, CA-04, CA-05, CA-08): ADMIN sees UNASSIGNED and triggers reconciliation but cannot
/// choose an assignee, the originator is never a candidate, and a revoked role is corrected.
/// </summary>
public sealed class ApprovalOperationsE2ETests
{
    private const string Issuer = "https://keycloak.test/realms/procure-to-pay";
    private const string WorkloadClientId = "procurement-api";
    private const string AdminSubject = "admin-1";
    private const string NonAdminSubject = "buyer-1";
    // Canonical ascending order: originator < first approver < second approver.
    private static readonly Guid OriginatorId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid FirstApprover = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid SecondApprover = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid SubjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LineId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset AssignedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record SubmissionBody(
        Guid OrganizationId,
        string SubjectType,
        Guid SubjectId,
        int SubjectVersion,
        string Operation,
        string ContractVersion,
        string SubmissionKey,
        Guid? RequesterId,
        Guid OriginatorId);

    [Fact]
    public async Task Unassigned_requirements_and_reconciliation_require_an_administrative_role()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await ApprovalEnvironment.StartAsync(cancellationToken);
        // An active but non-administrative profile: it must not read or trigger operations.
        await sqlServer.SeedApproverAsync(NonAdminSubject, SystemRole.ProcurementBuyer, null, cancellationToken);
        await using var factory = new ApprovalApiFactory(sqlServer.ConnectionString, sqlServer.DepartmentId);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken(AdminSubject));
        using var nonAdmin = factory.CreateClient();
        nonAdmin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken(NonAdminSubject));
        using var workload = factory.CreateClient();
        workload.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateWorkloadToken());

        // No eligible approver exists for the DEPARTMENT:IT requirement, so the task stays unassigned.
        Guid caseId;
        using (var submitted = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            Body(sqlServer.OrganizationId, "submission-unassigned"),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            var response = await submitted.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            caseId = response.GetProperty("caseId").GetGuid();
            Assert.Equal("BLOCKED", response.GetProperty("status").GetString());
        }

        using (var forbidden = await nonAdmin.GetAsync("/api/v1/approval/operations/unassigned", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var forbidden = await nonAdmin.PostAsJsonAsync(
            "/api/v1/approval/operations/reconciliation",
            new { reconciliationKey = "reconcile-forbidden" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var unassigned = await admin.GetAsync("/api/v1/approval/operations/unassigned", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, unassigned.StatusCode);
            var view = await unassigned.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var requirement = Assert.Single(view.EnumerateArray().ToArray());
            Assert.Equal(caseId, requirement.GetProperty("caseId").GetGuid());
            Assert.Equal("DEPARTMENT", requirement.GetProperty("stageCode").GetString());
            Assert.Equal("ITREVIEWER", requirement.GetProperty("role").GetString());
            Assert.Equal(LineId, requirement.GetProperty("targets")[0].GetProperty("id").GetGuid());
        }

        using (var reconciled = await admin.PostAsJsonAsync(
            "/api/v1/approval/operations/reconciliation",
            new { reconciliationKey = "reconcile-unassigned-1" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
            var outcome = await reconciled.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(1, outcome.GetProperty("scanned").GetInt32());
            Assert.Equal(0, outcome.GetProperty("reassigned").GetInt32());
            Assert.Equal(0, outcome.GetProperty("unassigned").GetInt32());
        }

        using (var state = await admin.GetAsync("/api/v1/approval/operations/reconciliation", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
            var view = await state.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.False(view.GetProperty("isDue").GetBoolean());
        }

        // Reconciliation restores operation but never assigns on its own authority.
        await using var verification = sqlServer.CreateContext();
        Assert.Null((await verification.ApprovalTasks.SingleAsync(
            record => record.CaseId == caseId, cancellationToken)).CurrentAssigneeUserId);
        Assert.Empty(await verification.ApprovalAssignments.ToArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task Originator_is_never_a_candidate_and_a_revoked_role_is_reconciled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await ApprovalEnvironment.StartAsync(cancellationToken);
        // The originator holds the lowest canonical UUID and a matching role and scope: if the
        // segregation of duties exclusions were ignored, it would be selected first.
        await sqlServer.SeedApproverWithIdAsync(OriginatorId, "originator", cancellationToken);
        await sqlServer.SeedApproverAsync("approver-b", SystemRole.ItReviewer, FirstApprover, cancellationToken);
        await sqlServer.SeedApproverAsync("approver-c", SystemRole.ItReviewer, SecondApprover, cancellationToken);
        await using var factory = new ApprovalApiFactory(sqlServer.ConnectionString, sqlServer.DepartmentId);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken(AdminSubject));
        using var workload = factory.CreateClient();
        workload.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateWorkloadToken());

        Guid caseId;
        using (var submitted = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            Body(sqlServer.OrganizationId, "submission-sod"),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            var response = await submitted.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            caseId = response.GetProperty("caseId").GetGuid();
            Assert.Equal("OPEN", response.GetProperty("status").GetString());
        }

        await using (var verification = sqlServer.CreateContext())
        {
            var assignedTask = await verification.ApprovalTasks.SingleAsync(
                record => record.CaseId == caseId, cancellationToken);
            Assert.NotEqual(OriginatorId, assignedTask.CurrentAssigneeUserId);
            Assert.Equal(FirstApprover, assignedTask.CurrentAssigneeUserId);
        }

        // Confirmed authority change: the current assignee loses the role.
        await using (var revoking = sqlServer.CreateContext())
        {
            var assignment = await revoking.RoleAssignments.SingleAsync(
                record => record.UserProfileId == FirstApprover, cancellationToken);
            assignment.Status = (int)AssignmentStatus.Revoked;
            assignment.RevokedAt = AssignedAt;
            await revoking.SaveChangesAsync(cancellationToken);
        }

        using (var reconciled = await admin.PostAsJsonAsync(
            "/api/v1/approval/operations/reconciliation",
            new { reconciliationKey = "reconcile-sod-1" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
            var outcome = await reconciled.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(1, outcome.GetProperty("scanned").GetInt32());
            Assert.Equal(1, outcome.GetProperty("reassigned").GetInt32());
            Assert.Equal(0, outcome.GetProperty("unassigned").GetInt32());
        }

        await using var final = sqlServer.CreateContext();
        var task = await final.ApprovalTasks.SingleAsync(record => record.CaseId == caseId, cancellationToken);
        Assert.Equal(SecondApprover, task.CurrentAssigneeUserId);
        var history = await final.ApprovalAssignments
            .Where(record => record.CaseId == caseId)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, history.Length);
        var current = Assert.Single(history, record => record.ReleasedAt is null);
        Assert.Equal(SecondApprover, current.AssigneeUserId);
        Assert.Equal(ApprovalAssignmentCause.Reassigned.ToString(), current.Cause);
        Assert.DoesNotContain(history, record => record.AssigneeUserId == OriginatorId);
    }

    private sealed record DecisionBody(
        string Action,
        string Reason,
        string DecisionKey,
        int ExpectedTaskVersion);

    [Fact]
    public async Task Assignee_decides_once_a_replay_returns_the_original_and_admin_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await ApprovalEnvironment.StartAsync(cancellationToken);
        var approverId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        await sqlServer.SeedApproverWithIdAsync(approverId, "approver-1", cancellationToken);
        await using var factory = new ApprovalApiFactory(sqlServer.ConnectionString, sqlServer.DepartmentId);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken(AdminSubject));
        using var assignee = factory.CreateClient();
        assignee.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken("approver-1"));
        using var workload = factory.CreateClient();
        workload.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateWorkloadToken());

        Guid caseId;
        using (var submitted = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            Body(sqlServer.OrganizationId, "submission-decision"),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            caseId = (await submitted.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("caseId").GetGuid();
        }

        Guid taskId;
        int taskVersion;
        await using (var context = sqlServer.CreateContext())
        {
            var task = await context.ApprovalTasks.SingleAsync(cancellationToken);
            Assert.Equal(approverId, task.CurrentAssigneeUserId);
            (taskId, taskVersion) = (task.Id, task.Version);
        }

        var decision = new DecisionBody("APPROVE", "Aprobado por negocio", "decision-e2e", taskVersion);

        // ADMIN restores operation but never substitutes business authority (CA-05, CA-08).
        using (var forbidden = await admin.PostAsJsonAsync(
            $"/api/v1/approval/tasks/{taskId}/decisions", decision, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var stale = await assignee.PostAsJsonAsync(
            $"/api/v1/approval/tasks/{taskId}/decisions",
            decision with { ExpectedTaskVersion = taskVersion + 4 },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }

        Guid decisionId;
        using (var accepted = await assignee.PostAsJsonAsync(
            $"/api/v1/approval/tasks/{taskId}/decisions", decision, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var outcome = await accepted.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            decisionId = outcome.GetProperty("decisionId").GetGuid();
            Assert.False(outcome.GetProperty("replayed").GetBoolean());
            Assert.Equal("APPROVE", outcome.GetProperty("action").GetString());
            Assert.Equal("COMPLETED", outcome.GetProperty("caseStatus").GetString());
            Assert.Equal(1, outcome.GetProperty("outboxEventIds").GetArrayLength());
        }

        // An identical replay returns the original decision even though the task is terminal.
        using (var replay = await assignee.PostAsJsonAsync(
            $"/api/v1/approval/tasks/{taskId}/decisions", decision, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var outcome = await replay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.True(outcome.GetProperty("replayed").GetBoolean());
            Assert.Equal(decisionId, outcome.GetProperty("decisionId").GetGuid());
        }

        using (var conflict = await assignee.PostAsJsonAsync(
            $"/api/v1/approval/tasks/{taskId}/decisions",
            decision with { Action = "REJECT", Reason = "Rechazado por negocio" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        await using var verification = sqlServer.CreateContext();
        Assert.Equal(1, await verification.ApprovalDecisions.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.ApprovalOutboxEvents.CountAsync(cancellationToken));
        Assert.Equal(
            decisionId,
            (await verification.ApprovalDecisions.SingleAsync(cancellationToken)).Id);
        Assert.Equal(taskVersion, (await verification.ApprovalDecisions.SingleAsync(cancellationToken)).TaskVersion);
        Assert.Equal(caseId, (await verification.ApprovalDecisions.SingleAsync(cancellationToken)).CaseId);
    }

    [Fact]
    public async Task Health_degrades_on_dead_letters_and_admin_alone_replays_them()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await ApprovalEnvironment.StartAsync(cancellationToken);
        await sqlServer.SeedApproverAsync(NonAdminSubject, SystemRole.ProcurementBuyer, null, cancellationToken);
        await using var factory = new ApprovalApiFactory(sqlServer.ConnectionString, sqlServer.DepartmentId);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken(AdminSubject));
        using var nonAdmin = factory.CreateClient();
        nonAdmin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken(NonAdminSubject));

        // A bootstrapped organization with no pending work reports healthy.
        using (var healthy = await admin.GetAsync("/health/approval", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
            var report = await healthy.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal("HEALTHY", report.GetProperty("status").GetString());
            Assert.Equal("APPROVAL_OK", report.GetProperty("code").GetString());
        }

        // A dead letter degrades the operational health and is visible to ADMIN.
        var eventId = await sqlServer.SeedDeadLetterAsync(cancellationToken);
        using (var degraded = await admin.GetAsync("/health/approval", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, degraded.StatusCode);
            var report = await degraded.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal("DEGRADED", report.GetProperty("status").GetString());
            Assert.Contains("APPROVAL_DEAD_LETTER", report.GetProperty("code").GetString()!, StringComparison.Ordinal);
        }

        using (var forbidden = await nonAdmin.GetAsync("/api/v1/approval/operations/outbox", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var backlog = await admin.GetAsync("/api/v1/approval/operations/outbox", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, backlog.StatusCode);
            var view = await backlog.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(1, view.GetProperty("deadLetter").GetInt32());
            Assert.Equal(0, view.GetProperty("pending").GetInt32());
        }

        using (var deadLetters = await admin.GetAsync(
            "/api/v1/approval/operations/outbox/dead-letters", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, deadLetters.StatusCode);
            var view = await deadLetters.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var entry = Assert.Single(view.EnumerateArray().ToArray());
            Assert.Equal(eventId, entry.GetProperty("eventId").GetGuid());
            Assert.Equal(10, entry.GetProperty("attempts").GetInt32());
        }

        using (var forbidden = await nonAdmin.PostAsync(
            $"/api/v1/approval/operations/outbox/{eventId}/replay", null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        string payloadBefore;
        await using (var context = sqlServer.CreateContext())
        {
            payloadBefore = (await context.ApprovalOutboxEvents.SingleAsync(
                record => record.Id == eventId, cancellationToken)).PayloadJson;
        }

        using (var replayed = await admin.PostAsync(
            $"/api/v1/approval/operations/outbox/{eventId}/replay", null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
            var entry = await replayed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal("PENDING", entry.GetProperty("state").GetString());
            Assert.Equal(0, entry.GetProperty("attempts").GetInt32());
        }

        await using var verification = sqlServer.CreateContext();
        var record = await verification.ApprovalOutboxEvents.SingleAsync(
            candidate => candidate.Id == eventId, cancellationToken);
        // The administrative replay never edits the contractual payload.
        Assert.Equal(payloadBefore, record.PayloadJson);
        Assert.Equal((int)ApprovalOutboxState.Pending, record.State);
        Assert.Contains(
            await verification.ApprovalAuditEntries.ToArrayAsync(cancellationToken),
            entry => entry.Action == "OUTBOX_REPLAYED");
    }

    [Fact]
    public async Task Inbox_case_visibility_and_audit_reads_are_minimized_and_scoped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await ApprovalEnvironment.StartAsync(cancellationToken);
        var approverId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var auditorId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var outsiderId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        await sqlServer.SeedApproverWithIdAsync(approverId, "approver-1", cancellationToken);
        await sqlServer.SeedApproverWithIdAsync(auditorId, "auditor-1", cancellationToken, SystemRole.Auditor);
        // The originator and an unrelated user are active profiles without approval authority.
        await sqlServer.SeedApproverWithIdAsync(OriginatorId, "originator-1", cancellationToken, SystemRole.Requester);
        await sqlServer.SeedApproverWithIdAsync(outsiderId, "outsider-1", cancellationToken, SystemRole.Requester);
        await using var factory = new ApprovalApiFactory(sqlServer.ConnectionString, sqlServer.DepartmentId);

        using var workload = factory.CreateClient();
        workload.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateWorkloadToken());
        using var assignee = factory.CreateClient();
        assignee.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken("approver-1"));
        using var auditor = factory.CreateClient();
        auditor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken("auditor-1"));
        using var originator = factory.CreateClient();
        originator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken("originator-1"));
        using var outsider = factory.CreateClient();
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken("outsider-1"));
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateUserToken("admin-1"));

        Guid caseId;
        using (var submitted = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            Body(sqlServer.OrganizationId, "submission-inbox"),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            caseId = (await submitted.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("caseId").GetGuid();
        }

        // The assignee sees exactly one pending task, minimized to what deciding needs.
        using (var inbox = await assignee.GetAsync("/api/v1/approval/inbox", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
            var body = await inbox.Content.ReadAsStringAsync(cancellationToken);
            var item = Assert.Single(JsonDocument.Parse(body).RootElement.EnumerateArray().ToArray());
            Assert.Equal(caseId, item.GetProperty("caseId").GetGuid());
            Assert.Equal("ITREVIEWER", item.GetProperty("role").GetString());
            Assert.Equal(LineId, item.GetProperty("targets")[0].GetProperty("id").GetGuid());
            Assert.True(item.GetProperty("taskVersion").GetInt32() >= 1);
            // No IdP identity, contact data, credentials or eligibility evidence is exposed.
            Assert.DoesNotContain("\"issuer\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"email\"", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("eligibilityEvidence", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authorization", body, StringComparison.OrdinalIgnoreCase);
        }

        // A user without assigned work has an empty inbox, not somebody else's tasks.
        using (var empty = await outsider.GetAsync("/api/v1/approval/inbox", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            Assert.Empty((await empty.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).EnumerateArray());
        }

        using (var history = await assignee.GetAsync("/api/v1/approval/inbox/history", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            Assert.Empty((await history.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).EnumerateArray());
        }

        // The originator reads its own case; an unrelated active user gets 404, which hides existence.
        await using (var context = sqlServer.CreateContext())
        {
            var profile = await context.UserProfiles.SingleAsync(
                record => record.Subject == "originator-1", cancellationToken);
            var caseRecord = await context.ApprovalCases.SingleAsync(
                record => record.Id == caseId, cancellationToken);
            Assert.Equal(profile.OrganizationId, caseRecord.OrganizationId);
            Assert.Equal(profile.Id, caseRecord.OriginatorId);
        }

        using (var own = await originator.GetAsync($"/api/v1/approval/cases/{caseId}", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            // The originator sees its own case and its frozen subject reference.
            var view = await own.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(OriginatorId, view.GetProperty("originatorId").GetGuid());
            Assert.Equal(caseId, view.GetProperty("id").GetGuid());
        }

        using (var hidden = await outsider.GetAsync($"/api/v1/approval/cases/{caseId}", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Contains("/problems/not-found",
                await hidden.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        using (var hiddenAudit = await outsider.GetAsync(
            $"/api/v1/approval/cases/{caseId}/decisions", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, hiddenAudit.StatusCode);
        }

        // The owner workload reads its own case but never acts as a user.
        using (var byWorkload = await workload.GetAsync($"/api/v1/approval/cases/{caseId}", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, byWorkload.StatusCode);
        }

        // AUDITOR reads the assignment evidence of the case.
        using (var assignments = await auditor.GetAsync(
            $"/api/v1/approval/cases/{caseId}/assignments", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, assignments.StatusCode);
            var entry = Assert.Single(
                (await assignments.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).EnumerateArray().ToArray());
            Assert.Equal(approverId, entry.GetProperty("assigneeUserId").GetGuid());
            Assert.Equal("Initial", entry.GetProperty("cause").GetString());
            Assert.True(entry.GetProperty("eligibilityEvidenceJson").GetString()!.Length > 0);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("releasedAt").ValueKind);
        }

        // The assignee decides, and only then does the actor history contain the decision.
        Guid taskId;
        int taskVersion;
        await using (var context = sqlServer.CreateContext())
        {
            var task = await context.ApprovalTasks.SingleAsync(cancellationToken);
            (taskId, taskVersion) = (task.Id, task.Version);
        }

        using (var decided = await assignee.PostAsJsonAsync(
            $"/api/v1/approval/tasks/{taskId}/decisions",
            new DecisionBody("APPROVE", "Aprobado por negocio", "decision-inbox", taskVersion),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        }

        using (var history = await assignee.GetAsync("/api/v1/approval/inbox/history", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            var entry = Assert.Single(
                (await history.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).EnumerateArray().ToArray());
            Assert.Equal("APPROVE", entry.GetProperty("action").GetString());
            Assert.Equal("Aprobado por negocio", entry.GetProperty("reason").GetString());
            var digest = entry.GetProperty("decisionDigest").GetString()!;
            Assert.Equal(64, digest.Length);
            Assert.True(digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        }

        using (var decisions = await auditor.GetAsync(
            $"/api/v1/approval/cases/{caseId}/decisions", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, decisions.StatusCode);
            var entry = Assert.Single(
                (await decisions.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).EnumerateArray().ToArray());
            Assert.Equal("APPROVE", entry.GetProperty("action").GetString());
            Assert.Equal(approverId, entry.GetProperty("actorUserId").GetGuid());
            Assert.Equal(1, entry.GetProperty("targetCount").GetInt32());
        }

        // AUDITOR reads the audit trail: the closed actor union and the causal chain of effects.
        using (var audit = await auditor.GetAsync(
            $"/api/v1/approval/cases/{caseId}/audit", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
            var entries = (await audit.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .EnumerateArray().ToArray();
            var root = Assert.Single(entries, entry => entry.GetProperty("action").GetString() == "CASE_SUBMITTED");
            Assert.Equal("WORKLOAD", root.GetProperty("actorType").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("actorWorkloadIssuer").GetString()));
            Assert.False(string.IsNullOrEmpty(root.GetProperty("actorWorkloadClientId").GetString()));
            Assert.Equal(JsonValueKind.Null, root.GetProperty("causedByAuditId").ValueKind);
            var effect = Assert.Single(entries, entry => entry.GetProperty("action").GetString() == "TASK_ASSIGNED");
            Assert.Equal("SYSTEM", effect.GetProperty("actorType").GetString());
            Assert.Equal("APPROVAL_WORKFLOW", effect.GetProperty("actorSystemId").GetString());
            Assert.Equal("APPROVAL", effect.GetProperty("causedByAuditStream").GetString());
            Assert.Equal(root.GetProperty("auditId").GetGuid(), effect.GetProperty("causedByAuditId").GetGuid());
            Assert.False(string.IsNullOrEmpty(effect.GetProperty("automaticEffectKey").GetString()));
        }

        // ADMIN without AUDITOR restores operation but receives no organizational evidence reads.
        using (var forbidden = await admin.GetAsync(
            $"/api/v1/approval/cases/{caseId}/decisions", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
    }

    private static SubmissionBody Body(Guid organizationId, string submissionKey) => new(
        organizationId,
        "PURCHASE_REQUEST",
        SubjectId,
        1,
        "SUBMIT",
        "v1",
        submissionKey,
        null,
        OriginatorId);

    private sealed class DepartmentAdapter(Guid departmentId) : IApprovalSubmissionAdapter
    {
        public ApprovalAdapterDescriptor Descriptor { get; } =
            new(WorkloadClientId, "PURCHASE_REQUEST", "SUBMIT", "v1", false);

        public Task<ApprovalSubmission> BuildAsync(
            ApprovalSubmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApprovalSubmission(
                request.SubmissionKey,
                request.OrganizationId,
                request.SubjectType,
                request.SubjectId,
                request.SubjectVersion,
                request.Operation,
                new string('a', 64),
                request.RequesterId,
                request.OriginatorId,
                [
                    new ApprovalRequirementDefinition(
                        "DEPARTMENT_REQ",
                        "DEPARTMENT",
                        SystemRole.ItReviewer,
                        AuthorityRequirement.None,
                        DecisionScopeDescriptor.Create(
                            request.OrganizationId,
                            [new DecisionScopeEntry(ScopeDimension.Department, departmentId, 1)]),
                        [
                            ApprovalDecisionAction.Approve,
                            ApprovalDecisionAction.Reject,
                            ApprovalDecisionAction.RequestChanges
                        ],
                        [OriginatorId],
                        [new ApprovalTarget("LINE", LineId, 1, new string('c', 64))],
                        [])
                ],
                []));
    }

    private sealed class ApprovalApiFactory(string connectionString, Guid departmentId)
        : WebApplicationFactory<Program>
    {
        private static readonly SymmetricSecurityKey SigningKey = new(
            Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!"));

        internal static string CreateUserToken(string subject) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                Issuer,
                "procure-to-pay-tests",
                claims: [new Claim(JwtRegisteredClaimNames.Sub, subject)],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        internal static string CreateWorkloadToken() =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                Issuer,
                "procure-to-pay-tests",
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, $"service-account-{WorkloadClientId}"),
                    new Claim("client_id", WorkloadClientId)
                ],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:SqlServer", connectionString);
            builder.UseSetting("Authentication:JwtBearer:Authority", "https://issuer.invalid");
            builder.UseSetting("Authentication:JwtBearer:Audience", "procure-to-pay-tests");
            builder.UseSetting("Approval:Workloads:0:Issuer", Issuer);
            builder.UseSetting("Approval:Workloads:0:ClientId", WorkloadClientId);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] = connectionString,
                    ["Authentication:JwtBearer:Authority"] = "https://issuer.invalid",
                    ["Authentication:JwtBearer:Audience"] = "procure-to-pay-tests",
                    ["AWS:Region"] = "us-east-1",
                    ["Storage:S3:BucketName"] = "procure-to-pay-api-tests",
                    ["Approval:Workloads:0:Issuer"] = Issuer,
                    ["Approval:Workloads:0:ClientId"] = WorkloadClientId
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IApprovalSubmissionAdapter>();
                services.AddSingleton<IApprovalSubmissionAdapter>(new DepartmentAdapter(departmentId));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                });
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.ValidAudience = "procure-to-pay-tests";
                    options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                    options.TokenValidationParameters.ValidateIssuer = true;
                    options.TokenValidationParameters.ValidateAudience = true;
                    options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                });
            });
        }
    }

    private sealed class ApprovalEnvironment : IAsyncDisposable
    {
        private MsSqlContainer container = null!;

        public string ConnectionString { get; private set; } = string.Empty;

        public Guid OrganizationId { get; private set; }

        public Guid DepartmentId { get; private set; }

        public static async Task<ApprovalEnvironment> StartAsync(CancellationToken cancellationToken)
        {
            var environment = new ApprovalEnvironment
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                    .WithPassword("ProcureToPay_test_2026!")
                    .Build()
            };
            await environment.container.StartAsync(cancellationToken);
            environment.ConnectionString = environment.container.GetConnectionString();
            await using (var context = environment.CreateContext())
            {
                await context.Database.MigrateAsync(cancellationToken);
                using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
                await new OrganizationBootstrapper(
                    context, loggerFactory.CreateLogger<OrganizationBootstrapper>())
                    .InitializeAsync(
                        new OrganizationBootstrapOptions(
                            "ACME", "Acme Corporation", "PEN", "America/Lima", 1, "ACME-PE",
                            "Acme Peru S.A.C.", "IT", "Software / IT",
                            Issuer, AdminSubject, "API approval operations bootstrap"),
                        cancellationToken);
                environment.OrganizationId = (await context.Organizations.SingleAsync(cancellationToken)).Id;
                environment.DepartmentId = (await context.Departments
                    .SingleAsync(record => record.Code == "IT", cancellationToken)).Id;
            }

            return environment;
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(ConnectionString)
                .Options);

        public Task SeedApproverAsync(
            string subject,
            SystemRole role,
            Guid? userId,
            CancellationToken cancellationToken) =>
            SeedApproverWithIdAsync(userId ?? Guid.NewGuid(), subject, cancellationToken, role);

        public async Task SeedApproverWithIdAsync(
            Guid userId,
            string subject,
            CancellationToken cancellationToken,
            SystemRole role = SystemRole.ItReviewer)
        {
            await using var context = CreateContext();
            context.UserProfiles.Add(new UserProfileRecord
            {
                Id = userId,
                OrganizationId = OrganizationId,
                Issuer = Issuer,
                Subject = subject,
                Email = $"{subject}@acme.test",
                DisplayName = subject,
                DepartmentId = DepartmentId,
                Status = (int)UserProfileStatus.Active,
                Version = 1
            });
            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = userId,
                Role = (int)role,
                ScopeJson = role == SystemRole.ItReviewer
                    ? "[{\"dimension\":\"DEPARTMENT\",\"reference\":\"IT\"}]"
                    : "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                Status = (int)AssignmentStatus.Active,
                AssignedAt = AssignedAt,
                AssignedBy = userId,
                Version = 1
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task<Guid> SeedDeadLetterAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var eventId = Guid.NewGuid();
            context.ApprovalOutboxEvents.Add(new ApprovalOutboxEventRecord
            {
                Id = eventId,
                CaseId = Guid.NewGuid(),
                OrganizationId = OrganizationId,
                ResultSourceType = "APPROVAL_REQUIREMENT",
                ResultSourceId = Guid.NewGuid(),
                ResultSourceKey = "WR-00000000000000000000000000000000",
                TargetType = "LINE",
                TargetId = LineId,
                TargetVersion = 1,
                MaterialSnapshotDigest = new string('c', 64),
                Result = "APPROVED",
                ContractVersion = ApprovalOutboxPolicy.ContractVersion,
                PayloadJson = "{\"contract_version\":\"approval-result/v2\",\"event_id\":\"" + eventId + "\"}",
                State = (int)ApprovalOutboxState.DeadLetter,
                Attempts = ApprovalOutboxPolicy.MaxAttempts,
                NextAttemptAt = AssignedAt,
                CreatedAt = AssignedAt,
                LastError = nameof(InvalidOperationException),
                CorrelationReference = "correlation-dead-letter",
                Version = ApprovalOutboxPolicy.MaxAttempts + 1
            });
            await context.SaveChangesAsync(cancellationToken);
            return eventId;
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
