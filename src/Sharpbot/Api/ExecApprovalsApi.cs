using System.Text.Json;
using Sharpbot.Agent;
using Sharpbot.Config;

namespace Sharpbot.Api;

/// <summary>Exec approvals API — pending approvals + resolution.</summary>
public static class ExecApprovalsApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void MapExecApprovalsApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/exec/approvals").WithTags("ExecApprovals");

        group.MapGet("/pending", GetPending);
        group.MapPost("/{id}/resolve", ResolveApproval);
        group.MapGet("/config", GetConfig);
    }

    private static IResult GetPending(ExecApprovalManager approvals)
    {
        var pending = approvals.GetPending()
            .Select(p => new
            {
                id = p.Id,
                command = p.Command,
                workingDirectory = p.WorkingDirectory,
                resolvedExecutablePath = p.ResolvedExecutablePath,
                security = p.Security,
                ask = p.Ask,
                createdAtUtc = p.CreatedAtUtc,
                expiresAtUtc = p.ExpiresAtUtc,
            })
            .ToList();

        return Results.Json(new
        {
            count = pending.Count,
            pending,
        }, JsonOptions);
    }

    private static async Task<IResult> ResolveApproval(
        string id,
        HttpRequest request,
        ExecApprovalManager approvals)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("decision", out var decisionProp))
                return Results.BadRequest(new { error = true, message = "Missing 'decision'. Use allow-once, allow-always, or deny." });

            var raw = decisionProp.GetString()?.Trim().ToLowerInvariant();
            var decision = raw switch
            {
                "allow-once" => ExecApprovalDecision.AllowOnce,
                "allow-always" => ExecApprovalDecision.AllowAlways,
                "deny" => ExecApprovalDecision.Deny,
                _ => (ExecApprovalDecision?)null,
            };

            if (decision is null)
                return Results.BadRequest(new { error = true, message = "Invalid decision. Use allow-once, allow-always, or deny." });

            var ok = approvals.Resolve(id, decision.Value);
            if (!ok)
                return Results.NotFound(new { error = true, message = $"Approval '{id}' not found or already resolved." });

            return Results.Json(new
            {
                success = true,
                id,
                decision = raw,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = true, message = ex.Message }, statusCode: 500);
        }
    }

    private static IResult GetConfig(SharpbotConfig config, ExecApprovalManager approvals)
    {
        return Results.Json(new
        {
            security = config.Tools.Exec.Security,
            ask = config.Tools.Exec.Ask,
            askFallback = config.Tools.Exec.AskFallback,
            approvalTimeoutSec = config.Tools.Exec.ApprovalTimeoutSec,
            safeBins = config.Tools.Exec.SafeBins,
            allowlist = approvals.GetAllowlist(),
        }, JsonOptions);
    }
}
