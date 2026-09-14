using AgentApi.Models;
using AgentApi.Services.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace AgentApi.Controllers;

[ApiController]
[Route("api/tasks")]
public sealed class TasksController(TaskService tasks) : ControllerBase
{
    /// <summary>Start a task. Returns immediately; poll the task for progress.</summary>
    [HttpPost]
    public IActionResult Create([FromBody] CreateTaskRequest? body)
    {
        var task = tasks.Create(body?.Prompt ?? "", body?.Mode);
        return Accepted(new { task.Id, task.Mode, state = task.State.ToString(), task.Prompt });
    }

    [HttpGet]
    public IActionResult List() => Ok(tasks.Recent().Select(Brief));

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var task = tasks.Get(id);
        if (task is null) return NotFound(new { error = $"No task {id}" });

        return Ok(new
        {
            task.Id,
            task.Prompt,
            task.Mode,
            state = task.State.ToString(),
            // A terminal-looking state is not the end: the runner sets it on its
            // summary step and TaskService still has the Excel export to add.
            // Poll this, not the state, to know the trace is complete.
            finished = task.FinishedAt is not null,
            task.Summary,
            task.Error,
            durationMs = task.DurationMs,
            counts = task.BuildSummaryCounts(),
            documents = task.Documents.Values,
            steps = task.Steps.Select(s => new
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
                s.Note,
                s.NoteTone,
            }),
        });
    }

    private static object Brief(AgentTask t) => new
    {
        t.Id,
        t.Prompt,
        t.Mode,
        state = t.State.ToString(),
        t.Summary,
        durationMs = t.DurationMs,
        steps = t.Steps.Count,
        counts = t.BuildSummaryCounts(),
    };

    public sealed record CreateTaskRequest(string? Prompt, string? Mode);
}
