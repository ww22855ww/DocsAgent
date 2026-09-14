using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentApi.Models;

namespace AgentApi.Services.Agent;

/// <summary>
/// Runs the pipeline in a fixed order, calling exactly the tools the LLM agent
/// would call.
///
/// Two jobs (roadmap.md section 17): it is the development path, so the tools
/// are proven working before an agent loop is wrapped around them, and it is
/// the stage fallback if the model misbehaves during the demo. The trace it
/// emits has the same shape as the LLM runner's, so the UI cannot tell them
/// apart.
///
/// It deliberately does NOT recover from a missing supplier code. That gap is
/// the contrast Phase 7 demonstrates: scripted sends the document to manual
/// review, the agent looks the supplier up by name instead.
/// </summary>
public sealed class ScriptedAgentRunner(
    ToolRegistry tools,
    AppOptions options,
    ILogger<ScriptedAgentRunner> log) : IAgentRunner
{
    public string Mode => "scripted";

    /// <summary>Note to attach to the next step, set just before the call it belongs to.</summary>
    private string? _pendingNote;

    public async Task RunAsync(AgentContext context, CancellationToken ct)
    {
        var task = context.Task;
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        task.State = TaskState.Running;

        // 1. Fetch. The portal has two screens, so even the fixed path has to
        //    choose one. Scripted mode does it by keyword: no judgement, just a
        //    lookup table. The agent reads the same task and reasons about it.
        var wantsEsg = LooksLikeEsg(task.Prompt);

        var downloaded = wantsEsg
            ? await CallAsync(context, "download_esg_surveys", new JsonObject
            {
                ["year"] = DateTime.Now.Year.ToString(),
                ["status"] = "ALL",
            }, "從入口網站取得 ESG 問卷", ct)
            : await CallAsync(context, "download_documents", new JsonObject
            {
                ["date"] = today,
                ["department"] = "SCM",
                ["document_type"] = "ALL",
            }, "從入口網站取得今天的 SCM 文件", ct);

        var files = (downloaded["files"] as JsonArray)?
            .Select(f => f?.GetValue<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .ToList() ?? [];

        if (files.Count == 0)
        {
            context.AddStep("summary", "沒有找到文件", "入口網站今天沒有回傳任何文件。");
            task.State = TaskState.Completed;
            task.Summary = "沒有需要處理的文件。";
            return;
        }

        // 2. Parse everything in one call
        await CallAsync(context, "extract_documents", new JsonObject(),
            $"解析 {files.Count} 份文件", ct);

        // 3. Classify, map and resolve each document. One document failing must
        //    not abandon the rest: record it as failed and carry on, so a demo
        //    run always reaches a summary.
        foreach (var filename in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProcessDocumentAsync(context, filename, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "processing {File} failed", filename);

                var outcome = context.Outcome(filename);
                outcome.Status = "failed";
                outcome.ReviewReason = ex.Message;

                context.AddStep("error", $"{filename} 處理失敗", ex.Message, success: false);
            }
        }

        Summarise(context);
    }

    /// <summary>
    /// Keyword routing for scripted mode. Brittle by design: it is the thing the
    /// agent replaces. A task worded outside this list sends scripted mode to
    /// the wrong screen, which is worth showing.
    /// </summary>
    public static readonly string[] EsgKeywords =
        ["esg", "問卷", "永續", "questionnaire", "survey", "sustainab"];

    private static bool LooksLikeEsg(string prompt) => MatchedKeywords(prompt).Count > 0;

    /// <summary>
    /// Which keywords a prompt trips, using the same rule the runner does.
    ///
    /// Public so the routing probe measures this implementation rather than a
    /// copy of it; a copy is how a comparison quietly stops being true.
    /// </summary>
    public static IReadOnlyList<string> MatchedKeywords(string prompt)
    {
        var text = prompt.ToLowerInvariant();
        return EsgKeywords.Where(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task ProcessDocumentAsync(AgentContext context, string filename, CancellationToken ct)
    {
        var classification = await CallAsync(context, ToolRegistry.ClassifyTool,
            new JsonObject { ["filename"] = filename },
            $"分類 {filename}", ct);

        var outcome = context.Outcome(filename);

        if (classification["needs_manual_review"]?.GetValue<bool>() == true)
        {
            var reason = classification["manual_review_reason"]?.GetValue<string>() ?? "classification was not usable";
            await SendToManualReviewAsync(context, filename, reason, ct);
            return;
        }

        // Map the supplier. Scripted mode only ever tries the code: a document
        // that carries just a vendor name gets no second attempt here.
        var code = classification["supplier_code"]?.GetValue<string>();
        JsonNode? supplier = null;

        if (!string.IsNullOrWhiteSpace(code))
        {
            var lookup = await CallAsync(context, "search_supplier",
                new JsonObject { ["supplier_code"] = code },
                $"查詢供應商 {code}", ct);

            if (lookup["match"]?.GetValue<string>() == "unique")
            {
                supplier = (lookup["results"] as JsonArray)?.FirstOrDefault()?.DeepClone();
                outcome.SupplierCode = supplier?["supplier_code"]?.GetValue<string>();
                outcome.SupplierName = supplier?["supplier_name"]?.GetValue<string>();
            }
            else
            {
                await SendToManualReviewAsync(context, filename,
                    $"supplier lookup for {code} returned {lookup["match"]?.GetValue<string>()}", ct);
                return;
            }
        }
        else
        {
            // The gap the agent closes. Labelled here so the two traces can be
            // read side by side and the difference is visible, not explained.
            _pendingNote = "文件上沒有供應商代碼。固定流程沒有第二條路可走，只能轉人工";
            await SendToManualReviewAsync(context, filename,
                "no supplier code on the document", ct);
            return;
        }

        // Map the part, when the document named one. A miss here is worth
        // recording but does not block archiving.
        JsonNode? part = null;
        var partNo = classification["part_no"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(partNo))
        {
            var lookup = await CallAsync(context, "search_part",
                new JsonObject { ["part_no"] = partNo },
                $"查詢料號 {partNo}", ct);

            if (lookup["match"]?.GetValue<string>() == "unique")
                part = (lookup["results"] as JsonArray)?.FirstOrDefault()?.DeepClone();
        }

        await CallAsync(context, "archive_record", new JsonObject
        {
            ["filename"] = filename,
            ["classification"] = classification.DeepClone(),
            ["mapping"] = new JsonObject { ["supplier"] = supplier, ["part"] = part },
            ["task_id"] = context.Task.Id,
        }, $"歸檔 {filename}", ct);

        outcome.Status = "archived";
    }

    private async Task SendToManualReviewAsync(
        AgentContext context, string filename, string reason, CancellationToken ct)
    {
        var outcome = context.Outcome(filename);
        outcome.Status = "manual_review";
        outcome.ReviewReason = reason;

        var result = await CallAsync(context, "notify_manual_review", new JsonObject
        {
            ["filename"] = filename,
            ["reason"] = reason,
            ["summary"] = $"Category: {outcome.Category ?? "unclassified"}. " +
                          $"Supplier: {outcome.SupplierName ?? outcome.SupplierCode ?? "unknown"}.",
            ["task_id"] = context.Task.Id,
        }, $"{filename} 轉人工複核", ct);

        outcome.Notified = result["notified"]?.GetValue<bool>() ?? false;
    }

    private async Task<JsonNode> CallAsync(
        AgentContext context, string toolName, JsonObject args, string intent, CancellationToken ct)
    {
        context.Task.State = toolName == ToolRegistry.ClassifyTool ? TaskState.WaitingLlm : TaskState.WaitingTool;

        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ToolTimeoutSec));

        try
        {
            var result = await tools.ExecuteAsync(context, toolName, args, timeout.Token);
            sw.Stop();

            var failed = result["status"]?.GetValue<string>() == "error";
            context.AddStep(
                kind: toolName == ToolRegistry.ClassifyTool ? "classify" : "tool",
                title: intent,
                toolName: toolName,
                arguments: args.DeepClone(),
                result: context.ForTrace(toolName, result).DeepClone(),
                success: !failed,
                durationMs: (int)sw.ElapsedMilliseconds,
                decidedBy: "script",
                note: _pendingNote,
                noteTone: _pendingNote is null ? null : "limit",
                executedBy: toolName == ToolRegistry.ClassifyTool ? "model" : "mcp");

            _pendingNote = null;

            if (failed)
                throw new InvalidOperationException($"{toolName} failed: {result["error"]?.GetValue<string>()}");

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            context.AddStep("error", intent, $"{toolName} 超過 {options.ToolTimeoutSec} 秒未回應",
                toolName: toolName, arguments: args.DeepClone(), success: false,
                durationMs: (int)sw.ElapsedMilliseconds);
            throw new TimeoutException($"{toolName} 超過 {options.ToolTimeoutSec} 秒未回應。");
        }
    }

    private void Summarise(AgentContext context)
    {
        var task = context.Task;
        var docs = task.Documents.Values.ToList();
        var archived = docs.Count(d => d.Status == "archived");
        var review = docs.Count(d => d.Status == "manual_review");
        var failed = docs.Count(d => d.Status == "failed");

        var section = LooksLikeEsg(task.Prompt) ? "ESG 問卷" : "SCM 文件";
        task.Summary =
            $"{section}共處理 {docs.Count} 份文件：{archived} 份自動歸檔，{review} 份轉人工複核" +
            (failed > 0 ? $"，{failed} 份處理失敗。" : "。");

        task.State = failed > 0 ? TaskState.Failed
                   : review > 0 ? TaskState.ManualReview
                   : TaskState.Completed;

        context.AddStep("summary", "任務完成", task.Summary, success: failed == 0);
        log.LogInformation("scripted run finished: {Archived} archived, {Review} review, {Failed} failed",
            archived, review, failed);
    }
}
