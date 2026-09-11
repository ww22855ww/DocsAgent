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
        read each result before deciding the next step. A normal run looks like:

        1. download_documents to fetch the documents into staging.
        2. extract_documents once, for everything in staging.
        3. classify_document for each file, one at a time.
        4. For each classified document:
           - If needs_manual_review is true, call notify_manual_review with the
             reason and move on to the next document.
           - Otherwise resolve the supplier with search_supplier, then call
             archive_record with just the filename. The classification and the
             mapping are attached for you; you do not need to repeat them.

        Handling a supplier you cannot resolve directly:
        - With a supplier_code, search by code.
        - With no code but a supplier_name, search by name instead. Do not give up
          on a document just because the code is missing.
        - If the search returns match "ambiguous" or "not_found", the mapping is
          not settled: send the document to manual review with that as the reason.
        - Only a match of "unique" settles a mapping.

        Rules:
        - Every document must end up either archived or in manual review.
        - Never invent a supplier code, a part number or a document number. Use
          only values the tools returned.
        - Do not repeat a tool call that already succeeded.
        - When every document is resolved, stop calling tools and reply with a
          short plain-text summary of what you did.
        """;

    public async Task RunAsync(AgentContext context, CancellationToken ct)
    {
        var task = context.Task;
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        var definitions = await tools.GetDefinitionsAsync(ct);
        log.LogInformation("agent loop starting with {Count} tools", definitions.Count);

        var messages = new List<LlmMessage>
        {
            LlmMessage.System(string.Format(SystemPromptTemplate, today)),
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
                    $"Agent repeated the same call to {name} {repeats + 1} times and is not making progress.");
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
            $"Agent reached the {options.MaxSteps}-step limit without finishing.");
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
            context.AddStep("error", $"Unknown tool: {name}", thought: thought,
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

        var forModel = context.Summarise(name, result);

        context.AddStep(
            kind: name == ToolRegistry.ClassifyTool ? "classify" : "tool",
            title: Describe(name, args),
            thought: thought,
            toolName: name,
            arguments: args.DeepClone(),
            result: forModel.DeepClone(),
            success: ok,
            durationMs: (int)sw.ElapsedMilliseconds);

        return (forModel, ok);
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
                $"Agent stopped with {unresolved} document(s) neither archived nor sent to manual review.");
        }

        task.Summary = string.IsNullOrWhiteSpace(closing)
            ? $"Processed {docs.Count} document(s). {archived} archived, {review} sent to manual review."
            : closing!.Trim();

        task.State = review > 0 ? TaskState.ManualReview : TaskState.Completed;
        context.AddStep("summary", "Task complete", task.Summary);
        log.LogInformation("agent run finished in {Steps} steps: {Archived} archived, {Review} review",
            task.Steps.Count, archived, review);
    }

    private static string Describe(string name, JsonObject args)
    {
        var file = args["filename"]?.GetValue<string>();
        return name switch
        {
            "download_documents" => $"Downloading {args["department"]?.GetValue<string>() ?? "SCM"} documents",
            "extract_documents" => "Reading staged documents",
            "list_staging_files" => "Checking staging",
            ToolRegistry.ClassifyTool => $"Classifying {file}",
            "search_supplier" => "Looking up supplier " +
                (args["supplier_code"]?.GetValue<string>() ?? args["supplier_name"]?.GetValue<string>() ?? ""),
            "search_part" => $"Looking up part {args["part_no"]?.GetValue<string>()}",
            "archive_record" => $"Archiving {file}",
            "notify_manual_review" => $"Sending {file} to manual review",
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
