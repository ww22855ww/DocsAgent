using AgentApi.Models;
using AgentApi.Services.Agent;
using Microsoft.EntityFrameworkCore;

namespace AgentApi.Services.Tasks;

/// <summary>
/// Persists a task's record to Postgres.
///
/// The row is inserted when the task starts so a run that crashes still leaves
/// a trace of having existed; everything else is written once at the end, in a
/// single transaction. The live UI reads from memory, so nothing depends on
/// these writes being incremental.
/// </summary>
public sealed class TaskRepository(
    IDbContextFactory<TaskDbContext> factory,
    ILogger<TaskRepository> log)
{
    public async Task CreateAsync(AgentTask task, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            db.Tasks.Add(new TaskRow
            {
                Id = task.Id,
                Prompt = task.Prompt,
                Mode = task.Mode,
                State = task.State.ToString(),
                StartedAt = task.StartedAt,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Persistence must never take down a demo run.
            log.LogError(ex, "could not insert task {Id}", task.Id);
        }
    }

    public async Task SaveAsync(AgentContext context, CancellationToken ct = default)
    {
        var task = context.Task;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var row = await db.Tasks.FirstOrDefaultAsync(t => t.Id == task.Id, ct);
            if (row is null)
            {
                row = new TaskRow { Id = task.Id, StartedAt = task.StartedAt };
                db.Tasks.Add(row);
            }

            row.Prompt = task.Prompt;
            row.Mode = task.Mode;
            row.State = task.State.ToString();
            row.Summary = task.Summary;
            row.Error = task.Error;
            row.FinishedAt = task.FinishedAt;
            row.DurationMs = task.DurationMs;

            // Replace rather than append: a re-save of the same task must not
            // double the trace.
            db.TaskSteps.RemoveRange(db.TaskSteps.Where(s => s.TaskId == task.Id));
            db.Documents.RemoveRange(db.Documents.Where(d => d.TaskId == task.Id));
            db.Classifications.RemoveRange(db.Classifications.Where(c => c.TaskId == task.Id));
            db.Mappings.RemoveRange(db.Mappings.Where(m => m.TaskId == task.Id));
            await db.SaveChangesAsync(ct);

            db.TaskSteps.AddRange(task.Steps.Select(s => new TaskStepRow
            {
                TaskId = task.Id,
                StepIndex = s.Index,
                Kind = s.Kind,
                Title = s.Title,
                Detail = s.Detail,
                Thought = s.Thought,
                ToolName = s.ToolName,
                Arguments = s.Arguments?.DeepClone(),
                Result = s.Result?.DeepClone(),
                Success = s.Success,
                DurationMs = s.DurationMs,
                At = s.At,
            }));

            db.Documents.AddRange(task.Documents.Values.Select(d => new DocumentRow
            {
                TaskId = task.Id,
                Filename = d.Filename,
                Status = d.Status,
                Category = d.Category,
                SupplierCode = d.SupplierCode,
                SupplierName = d.SupplierName,
                PartNo = d.PartNo,
                DocumentNo = d.DocumentNo,
                ReviewReason = d.ReviewReason,
                Notified = d.Notified,
            }));

            db.Classifications.AddRange(context.Classifications.Select(c => new ClassificationRow
            {
                TaskId = task.Id,
                Filename = c.Filename,
                Category = c.Category,
                Confidence = (decimal)Math.Round(c.Confidence, 3),
                SupplierCode = c.SupplierCode,
                SupplierName = c.SupplierName,
                PartNo = c.PartNo,
                DocumentNo = c.DocumentNo,
                Provider = c.Provider,
                Model = c.Model,
                LatencyMs = c.LatencyMs,
                NeedsManualReview = c.NeedsManualReview,
                ReviewReason = c.ReviewReason,
            }));

            db.Mappings.AddRange(context.Mappings.Select(m => new MappingRow
            {
                TaskId = task.Id,
                Filename = m.Filename,
                Kind = m.Kind,
                Query = m.Query?.DeepClone(),
                Match = m.Match,
                Source = m.Source,
                Resolved = m.Resolved?.DeepClone(),
            }));

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            log.LogInformation("persisted task {Id}: {Steps} steps, {Docs} documents",
                task.Id, task.Steps.Count, task.Documents.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "could not persist task {Id}", task.Id);
        }
    }
}
