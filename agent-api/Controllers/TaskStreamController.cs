using System.Text.Json;
using System.Threading.Channels;
using AgentApi.Models;
using AgentApi.Services.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace AgentApi.Controllers;

/// <summary>
/// Server-sent events for one task's execution trace.
///
/// The path is /api/tasks/stream/{id} rather than /api/tasks/{id}/stream so it
/// sits under a single nginx location with buffering disabled; a buffered proxy
/// would hold events back and deliver the whole run in one burst at the end.
/// </summary>
[ApiController]
[Route("api/tasks/stream")]
public sealed class TaskStreamController(TaskService tasks, ILogger<TaskStreamController> log) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Heartbeat interval. Also the cadence at which state changes are pushed.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    [HttpGet("{id}")]
    public async Task Stream(string id, CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";   // belt and braces alongside nginx

        var task = tasks.Get(id);
        if (task is null)
        {
            await SendAsync("error", new { error = $"No task {id}" }, ct);
            return;
        }

        // Subscribe before snapshotting the trace, then skip anything the
        // snapshot already covered. Subscribing afterwards would drop steps
        // added in between.
        var channel = Channel.CreateUnbounded<TaskStep>();
        void OnStep(string taskId, TaskStep step)
        {
            if (taskId == id) channel.Writer.TryWrite(step);
        }

        tasks.StepAdded += OnStep;
        try
        {
            var sentUpTo = 0;
            foreach (var step in task.Steps.ToList())
            {
                await SendAsync("step", Shape(step), ct);
                sentUpTo = step.Index;
            }

            var lastState = "";
            while (!ct.IsCancellationRequested)
            {
                if (task.State.ToString() != lastState)
                {
                    lastState = task.State.ToString();
                    await SendAsync("state", new { state = lastState, durationMs = task.DurationMs }, ct);
                }

                // Completion is FinishedAt, not a terminal state. The runner sets
                // the state on its summary step, but TaskService still has the
                // Excel export to add afterwards; closing on state alone drops
                // that step from the live stream. Also drain the queue first, so
                // the last steps of a fast run are never cut off.
                if (task.FinishedAt is not null && channel.Reader.Count == 0)
                {
                    await SendAsync("done", Final(task), ct);
                    return;
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(Tick);
                try
                {
                    var step = await channel.Reader.ReadAsync(wait.Token);
                    if (step.Index > sentUpTo)
                    {
                        await SendAsync("step", Shape(step), ct);
                        sentUpTo = step.Index;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await SendAsync("ping", new { at = DateTimeOffset.UtcNow }, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The browser navigated away or closed the tab. Nothing to do.
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "stream for {Id} ended unexpectedly", id);
        }
        finally
        {
            tasks.StepAdded -= OnStep;
        }
    }

    private static object Shape(TaskStep s) => new
    {
        s.Index,
        s.Kind,
        s.Title,
        s.Detail,
        s.Thought,
        s.ToolName,
        s.Arguments,
        s.Result,
        s.Success,
        s.DurationMs,
        s.At,
        s.DecidedBy,
        s.ExecutedBy,
        s.Note,
        s.NoteTone,
    };

    private static object Final(AgentTask task) => new
    {
        state = task.State.ToString(),
        task.Summary,
        task.Error,
        durationMs = task.DurationMs,
        counts = task.BuildSummaryCounts(),
        documents = task.Documents.Values,
    };

    private async Task SendAsync(string eventName, object payload, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(payload, Json);
        await Response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
