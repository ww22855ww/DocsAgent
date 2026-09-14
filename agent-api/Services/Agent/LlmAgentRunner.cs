using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentApi.Models;
using AgentApi.Services.Llm;

namespace AgentApi.Services.Agent;

/// <summary>
/// The agent loop: the model chooses a tool, the tool runs, the result goes
/// back, repeat until it stops calling tools.
///
/// The model decides what to do. It never decides how a step is carried out,
/// and it never decides whether it may stop early: max_steps, the timeout and
/// the repeat detector are enforced here, not asked of the model.
/// </summary>
public sealed class LlmAgentRunner(
    LlmClient llm,
    ToolRegistry tools,
    AppOptions options,
    ILogger<LlmAgentRunner> log) : IAgentRunner
{
    public string Mode => "llm";

    /// <summary>How many times the same tool with the same arguments is tolerated before aborting.</summary>
    private const int RepeatLimit = 2;

    private const string SystemPromptTemplate = """
        You are a document processing agent for a supply chain department.
        Today is {0}.

        Work through the user's task using the tools. Call one tool at a time and
        read each result before deciding the next step.

        The portal keeps two kinds of work on separate screens, so start by
        deciding which the task is asking for:
        - SCM documents - invoices, quality reports, debit notes, correspondence.
          Fetch these with download_documents.
        - ESG questionnaires - supplier sustainability survey returns. Fetch these
          with download_esg_surveys.
        Fetch only the one the task asks for. If the task is genuinely about both,
        fetch each in turn.

        A normal run then looks like:

        1. The fetch tool for the screen the task named.
        2. extract_documents once, for everything in staging.
        3. classify_document for each file, one at a time.
        4. For each classified document:
           - If needs_manual_review is true, call notify_manual_review with the
             reason and move on to the next document.
           - Otherwise resolve the supplier with search_supplier. If the document
             also has a part_no, call search_part for it as well.
           - Then call archive_record with just the filename. The classification
             and the mapping are attached for you; do not repeat them.

        Passing identifiers:
        - Prefer calling search_supplier and search_part with just filename. The
          code, name and part number extracted from that document are filled in
          for you, exactly as they were printed.
        - Only type a code or name yourself when you are searching for something
          that did not come from a document. Never retranscribe a value you have
          already seen: use the filename instead.

        Handling a supplier you cannot resolve directly:
        - search_supplier with the filename tries the code when the document has
          one and the name when it does not. Do not give up on a document just
          because the code is missing.
        - If the search returns match "ambiguous" or "not_found", the mapping is
          not settled: send the document to manual review, saying in the reason
          how many candidates were found and what you searched for.
        - Only a match of "unique" settles a mapping. Never pick one row out of
          an ambiguous result; deciding between them is the reviewer's job.
        - A part lookup that does not resolve is not a blocker. Archive the
          document anyway; only the supplier has to be settled.

        Rules:
        - Every document must end up either archived or in manual review.
        - Never invent a supplier code, a part number or a document number. Use
          only values the tools returned.
        - Do not repeat a tool call that already succeeded.
        - When every document is resolved, stop calling tools and reply with a
          short plain-text summary of what you did.

        Writing that summary:
        - Refer to documents by file name, and give counts.
        - Do NOT write out supplier names, supplier codes or part numbers. They
          are already displayed beside your summary, and copying them back is
          how they get corrupted.
        """;

    /// <summary>
    /// The exact system prompt a real run uses.
    ///
    /// Exposed so the routing probe asks the model the same question the agent
    /// would. A probe with its own copy of the prompt would keep passing after
    /// the real one changed, which is worse than having no probe.
    /// </summary>
    public static string BuildSystemPrompt(DateTime today)
        => string.Format(SystemPromptTemplate, today.ToString("yyyy-MM-dd"));

    public async Task RunAsync(AgentContext context, CancellationToken ct)
    {
        var task = context.Task;
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        var definitions = await tools.GetDefinitionsAsync(ct);
        log.LogInformation("agent loop starting with {Count} tools", definitions.Count);

        var messages = new List<LlmMessage>
        {
            LlmMessage.System(BuildSystemPrompt(DateTime.Now)),
            LlmMessage.User(task.Prompt),
        };

        var recentCalls = new List<string>();
        task.State = TaskState.Running;

        for (var step = 1; step <= options.MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            task.State = TaskState.WaitingLlm;
            var decision = await llm.CompleteAsync(new LlmRequest
            {
                Messages = messages,
                Tools = definitions,
                MaxTokens = options.LlmMaxTokensTool,
            }, ct);

            if (!decision.HasToolCalls)
            {
                Finish(context, decision.Content);
                return;
            }

            // One tool per turn keeps the trace readable and matches the prompt.
            var call = decision.ToolCalls[0];
            var name = call.Function.Name;
            var args = call.ParseArguments();

            var signature = $"{name}:{args.ToJsonString()}";
            var repeats = recentCalls.Count(c => c == signature);
            recentCalls.Add(signature);

            if (repeats >= RepeatLimit)
            {
                throw new InvalidOperationException(
                    $"Agent 連續 {repeats + 1} 次用相同參數呼叫 {name}，沒有進展，已中止。");
            }

            messages.Add(new LlmMessage("assistant", null) { ToolCalls = [call] });

            var (resultForModel, ok) = await ExecuteAsync(context, decision, call, name, args, ct);
            messages.Add(LlmMessage.ToolResult(call.Id, resultForModel.ToJsonString()));

            if (!ok && repeats + 1 >= RepeatLimit)
            {
                log.LogWarning("tool {Tool} failed repeatedly; letting the model see it once more", name);
            }
        }

        throw new InvalidOperationException(
            $"Agent 用滿 {options.MaxSteps} 步仍未結束，已中止。");
    }

    private async Task<(JsonNode ForModel, bool Ok)> ExecuteAsync(
        AgentContext context,
        LlmResult decision,
        LlmToolCall call,
        string name,
        JsonObject args,
        CancellationToken ct)
    {
        var thought = Summarise(decision.Reasoning);

        // A hallucinated tool is a correctable mistake, not a failure: tell the
        // model what actually exists and let it try again.
        if (!await tools.ExistsAsync(name, ct))
        {
            var known = string.Join(", ", (await tools.GetDefinitionsAsync(ct)).Select(d => d.Name));
            var error = new JsonObject
            {
                ["status"] = "error",
                ["error"] = $"There is no tool called '{name}'. Available tools: {known}",
            };
            context.AddStep("error", $"不存在的工具：{name}", thought: thought,
                toolName: name, arguments: args.DeepClone(), result: error.DeepClone(), success: false);
            return (error, false);
        }

        context.Task.State = name == ToolRegistry.ClassifyTool ? TaskState.WaitingLlm : TaskState.WaitingTool;

        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ToolTimeoutSec));

        JsonNode result;
        bool ok;
        try
        {
            result = await tools.ExecuteAsync(context, name, args, timeout.Token);
            ok = result["status"]?.GetValue<string>() != "error";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new JsonObject
            {
                ["status"] = "error",
                ["error"] = $"{name} timed out after {options.ToolTimeoutSec}s.",
            };
            ok = false;
        }
        catch (Exception ex)
        {
            result = new JsonObject { ["status"] = "error", ["error"] = $"{name} failed: {ex.Message}" };
            ok = false;
        }
        sw.Stop();

        TrackOutcome(context, name, args, result, ok);

        var forModel = context.ForModel(name, result);
        var forTrace = context.ForTrace(name, result);

        var (note, tone) = Annotate(context, name, args, result, ok);

        context.AddStep(
            kind: name == ToolRegistry.ClassifyTool ? "classify" : "tool",
            title: Describe(context, name, args),
            thought: thought,
            toolName: name,
            arguments: args.DeepClone(),
            result: forTrace.DeepClone(),
            success: ok,
            durationMs: (int)sw.ElapsedMilliseconds,
            decidedBy: "model",
            note: note,
            noteTone: tone,
            executedBy: name == ToolRegistry.ClassifyTool ? "model" : "mcp");

        return (forModel, ok);
    }

    /// <summary>
    /// Label the two steps the demo turns on.
    ///
    /// Only the moments that carry the argument get a note: the agent finding a
    /// second route when the code is missing, and the agent refusing to pick one
    /// of several candidates. Everything else stays unlabelled so these stand out.
    /// </summary>
    private static (string? Note, string? Tone) Annotate(
        AgentContext context, string name, JsonObject args, JsonNode result, bool ok)
    {
        if (!ok) return (null, null);

        if (name == "search_supplier")
        {
            var file = args["filename"]?.GetValue<string>();
            var searchedByName = args["supplier_name"] is not null
                || (file is not null && context.Outcome(file).SupplierCode is null);

            var match = result["match"]?.GetValue<string>();

            if (searchedByName && match == "unique")
                return ("文件上沒有供應商代碼，agent 自己改用廠商名稱查詢，並且查到唯一一筆", "win");

            if (match == "ambiguous")
            {
                var n = result["count"]?.GetValue<int>() ?? 0;
                return ($"查到 {n} 家同名廠商，agent 沒有從中挑一個，改送人工複核", "win");
            }
        }

        return (null, null);
    }

    /// <summary>Keep the per-document outcome in step with what the agent just did.</summary>
    private static void TrackOutcome(
        AgentContext context, string name, JsonObject args, JsonNode result, bool ok)
    {
        if (!ok) return;
        var filename = args["filename"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(filename)) return;

        var outcome = context.Outcome(filename!);
        switch (name)
        {
            case "archive_record":
                outcome.Status = "archived";
                break;
            case "notify_manual_review":
                outcome.Status = "manual_review";
                outcome.ReviewReason = args["reason"]?.GetValue<string>();
                outcome.Notified = result["notified"]?.GetValue<bool>() ?? false;
                break;
        }
    }

    private void Finish(AgentContext context, string? closing)
    {
        var task = context.Task;
        var docs = task.Documents.Values.ToList();
        var archived = docs.Count(d => d.Status == "archived");
        var review = docs.Count(d => d.Status == "manual_review");
        var unresolved = docs.Count(d => d.Status == "pending");

        if (unresolved > 0)
        {
            throw new InvalidOperationException(
                $"Agent 停止時仍有 {unresolved} 份文件既未歸檔也未轉人工複核。");
        }

        task.Summary = Vet(context, closing)
            ?? $"處理 {docs.Count} 份文件：{archived} 份歸檔，{review} 份轉人工複核。";

        task.State = review > 0 ? TaskState.ManualReview : TaskState.Completed;
        context.AddStep("summary", "任務完成", task.Summary);
        log.LogInformation("agent run finished in {Steps} steps: {Archived} archived, {Review} review",
            task.Steps.Count, archived, review);
    }

    /// <summary>
    /// Human-readable step title.
    ///
    /// A lookup may be addressed by filename rather than by value, so the label
    /// falls back to what that document actually carries. Otherwise the trace
    /// would read "Looking up supplier" with nothing after it.
    /// </summary>
    /// <summary>
    /// Reject a closing summary that spells out a company name.
    ///
    /// gemma4 cannot reproduce Traditional Chinese proper nouns reliably: asked
    /// to write a summary it turned 華碩電腦股份有限公司 into 鈺玮电讯股份有限公司
    /// and 聯強國際 into 鼎瑞国际. Wrong vendor names in front of a procurement
    /// audience destroy trust in everything else on the screen, so any summary
    /// naming a company is discarded in favour of a generated one. The prompt
    /// asks for counts and file names; this enforces it.
    /// </summary>
    private static string? Vet(AgentContext context, string? closing)
    {
        if (string.IsNullOrWhiteSpace(closing)) return null;
        var text = closing.Trim();

        // Company-name markers common to both scripts. A summary written to the
        // brief has no reason to contain any of them.
        string[] markers = ["公司", "有限", "股份", "科技", "電腦", "电脑", "國際", "国际"];
        if (markers.Any(m => text.Contains(m, StringComparison.Ordinal)))
            return null;

        return text;
    }

    private static string Describe(AgentContext context, string name, JsonObject args)
    {
        var file = args["filename"]?.GetValue<string>();

        string Supplier()
        {
            var value = args["supplier_code"]?.GetValue<string>()
                     ?? args["supplier_name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(file))
            {
                var o = context.Outcome(file!);
                value = o.SupplierCode ?? o.SupplierName;
            }
            return string.IsNullOrWhiteSpace(value) ? "查詢供應商" : $"查詢供應商 {value}";
        }

        string Part()
        {
            var value = args["part_no"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(file))
                value = context.Outcome(file!).PartNo;
            return string.IsNullOrWhiteSpace(value) ? "查詢料號" : $"查詢料號 {value}";
        }

        return name switch
        {
            "download_documents" => $"下載 {args["department"]?.GetValue<string>() ?? "SCM"} 文件",
            "download_esg_surveys" => "下載 ESG 問卷",
            "extract_documents" => "解析暫存區的文件",
            "list_staging_files" => "查看暫存區",
            ToolRegistry.ClassifyTool => $"分類 {file}",
            "search_supplier" => Supplier(),
            "search_part" => Part(),
            "archive_record" => $"歸檔 {file}",
            "notify_manual_review" => $"{file} 轉人工複核",
            _ => name,
        };
    }

    /// <summary>Condense the model's chain of thought into one line for the trace.</summary>
    private static string? Summarise(string? reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning)) return null;

        var line = reasoning
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 20 && !l.StartsWith('*') && !l.StartsWith('-'));

        line ??= reasoning.Trim();
        return line.Length <= 200 ? line : line[..200] + "...";
    }
}
