using System.Collections.Concurrent;
using AgentApi.Models;
using AgentApi.Services.Agent;

namespace AgentApi.Services.Tasks;

/// <summary>
/// Creates tasks, runs them in the background, and keeps their traces.
///
/// State is in memory: a demo restarts often and nothing here needs to outlive
/// the process. The durable record of what happened is in Postgres, written by
/// archive_record and notify_manual_review.
/// </summary>
public sealed class TaskService(
    ScriptedAgentRunner scripted,
    LlmAgentRunner llmRunner,
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
            context.AddStep("agent", $"Task accepted ({task.Mode} mode)", task.Prompt);

            if (_runGate.CurrentCount == 0)
                context.AddStep("agent", "Waiting for the running task to finish");

            await _runGate.WaitAsync(cts.Token);
            try
            {
                await runner.RunAsync(context, cts.Token);
            }
            finally
            {
                _runGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            task.State = TaskState.Failed;
            task.Error = $"Task exceeded the {options.TaskTimeoutMin}-minute limit.";
            context.AddStep("error", "Task timed out", task.Error, success: false);
            log.LogWarning("task {Id} timed out", task.Id);
        }
        catch (Exception ex)
        {
            task.State = TaskState.Failed;
            task.Error = ex.Message;
            context.AddStep("error", "Task failed", ex.Message, success: false);
            log.LogError(ex, "task {Id} failed", task.Id);
        }
        finally
        {
            task.FinishedAt = DateTimeOffset.UtcNow;
        }
    }
}
