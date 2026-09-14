using System.Collections.Concurrent;
using AgentApi.Models;
using AgentApi.Services.Agent;

namespace AgentApi.Services.Tasks;

/// <summary>
/// Creates tasks, runs them in the background, keeps their traces in memory and
/// writes the durable record when they finish.
///
/// The live view reads from memory so the UI stays responsive; Postgres holds
/// the record afterwards. mcp-worker writes archives and manual_reviews as it
/// goes, this writes the task, its trace, and the classification and mapping
/// history.
/// </summary>
public sealed class TaskService(
    ScriptedAgentRunner scripted,
    LlmAgentRunner llmRunner,
    ToolRegistry tools,
    TaskRepository repository,
    AppOptions options,
    ILogger<TaskService> log)
{
    private readonly ConcurrentDictionary<string, AgentTask> _tasks = new();

    /// <summary>Fired on every new trace step, for the Phase 6 SSE stream.</summary>
    public event Action<string, TaskStep>? StepAdded;

    public AgentTask? Get(string id) => _tasks.GetValueOrDefault(id);

    public IReadOnlyList<AgentTask> Recent(int limit = 20) =>
        _tasks.Values.OrderByDescending(t => t.StartedAt).Take(limit).ToList();

    /// <summary>
    /// Serialises task execution. Every task shares one staging directory, so
    /// two running at once would move each other's files out from under them.
    /// </summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public bool IsBusy => _runGate.CurrentCount == 0;

    public AgentTask Create(string prompt, string? modeOverride)
    {
        var mode = (modeOverride ?? options.AgentMode).Trim().ToLowerInvariant();
        if (mode != "llm" && mode != "scripted") mode = "scripted";

        var task = new AgentTask
        {
            Id = $"task-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}",
            Prompt = string.IsNullOrWhiteSpace(prompt) ? "處理今天 SCM 文件" : prompt.Trim(),
            Mode = mode,
        };

        _tasks[task.Id] = task;
        log.LogInformation("task {Id} created in {Mode} mode: {Prompt}", task.Id, mode, task.Prompt);

        // Insert the row up front so a run that dies mid-flight still leaves a
        // record that it was attempted.
        _ = repository.CreateAsync(task);
        _ = Task.Run(() => RunAsync(task));
        return task;
    }

    private async Task RunAsync(AgentTask task)
    {
        var context = new AgentContext(task, step => StepAdded?.Invoke(task.Id, step));
        IAgentRunner runner = task.Mode == "llm" ? llmRunner : scripted;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(options.TaskTimeoutMin));

        try
        {
            context.AddStep("agent", $"任務已接收（{task.Mode} 模式）", task.Prompt);

            if (_runGate.CurrentCount == 0)
                context.AddStep("agent", "等待前一個任務結束");

            await _runGate.WaitAsync(cts.Token);
            try
            {
                await runner.RunAsync(context, cts.Token);
                await WriteWorkbookAsync(context, cts.Token);
            }
            finally
            {
                _runGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            task.State = TaskState.Failed;
            task.Error = $"任務超過 {options.TaskTimeoutMin} 分鐘上限。";
            context.AddStep("error", "任務逾時", task.Error, success: false);
            log.LogWarning("task {Id} timed out", task.Id);
        }
        catch (Exception ex)
        {
            task.State = TaskState.Failed;
            task.Error = ex.Message;
            context.AddStep("error", "任務失敗", ex.Message, success: false);
            log.LogError(ex, "task {Id} failed", task.Id);
        }
        finally
        {
            task.FinishedAt = DateTimeOffset.UtcNow;
            await repository.SaveAsync(context, CancellationToken.None);
        }
    }

    /// <summary>
    /// Export the workbook once the run is done.
    ///
    /// This is reporting, not a decision, so it runs here rather than being
    /// offered to the agent: the agent cannot skip it, call it too early, or
    /// call it twice. A failure here is recorded but does not fail the task,
    /// since the database already holds the result.
    /// </summary>
    private async Task WriteWorkbookAsync(AgentContext context, CancellationToken ct)
    {
        var task = context.Task;
        if (task.Documents.Count == 0) return;

        try
        {
            var result = await tools.ExecuteAsync(context, "write_excel",
                new System.Text.Json.Nodes.JsonObject { ["task_id"] = task.Id }, ct);

            var failed = result["status"]?.GetValue<string>() == "error";
            context.AddStep("tool", "產生 result.xlsx",
                detail: failed ? result["error"]?.GetValue<string>() : result["path"]?.GetValue<string>(),
                toolName: "write_excel",
                result: result.DeepClone(),
                success: !failed,
                executedBy: "mcp");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "excel export failed for {Id}", task.Id);
            context.AddStep("error", "無法產生 result.xlsx", ex.Message, success: false);
        }
    }
}
