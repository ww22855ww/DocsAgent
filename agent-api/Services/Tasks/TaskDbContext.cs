using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace AgentApi.Services.Tasks;

// ---------------------------------------------------------------------------
// Entities. These map onto the tables db/init.sql creates; EF Core never
// creates or migrates the schema here, so the SQL file stays the one place the
// shape is defined.
// ---------------------------------------------------------------------------

public sealed class TaskRow
{
    public string Id { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Mode { get; set; } = "";
    public string State { get; set; } = "";
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int DurationMs { get; set; }
}

public sealed class TaskStepRow
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public int StepIndex { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    public string? Thought { get; set; }
    public string? ToolName { get; set; }
    public JsonNode? Arguments { get; set; }
    public JsonNode? Result { get; set; }
    public bool Success { get; set; }
    public int DurationMs { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class DocumentRow
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Category { get; set; }
    public string? SupplierCode { get; set; }
    public string? SupplierName { get; set; }
    public string? PartNo { get; set; }
    public string? DocumentNo { get; set; }
    public string? ReviewReason { get; set; }
    public bool Notified { get; set; }
}

public sealed class ClassificationRow
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal? Confidence { get; set; }
    public string? SupplierCode { get; set; }
    public string? SupplierName { get; set; }
    public string? PartNo { get; set; }
    public string? DocumentNo { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public int LatencyMs { get; set; }
    public bool NeedsManualReview { get; set; }
    public string? ReviewReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MappingRow
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public string? Filename { get; set; }
    public string Kind { get; set; } = "";     // supplier | part
    public JsonNode? Query { get; set; }
    public string Match { get; set; } = "";    // unique | ambiguous | not_found
    public string? Source { get; set; }
    public JsonNode? Resolved { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskRow> Tasks => Set<TaskRow>();
    public DbSet<TaskStepRow> TaskSteps => Set<TaskStepRow>();
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();
    public DbSet<ClassificationRow> Classifications => Set<ClassificationRow>();
    public DbSet<MappingRow> Mappings => Set<MappingRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TaskRow>(e =>
        {
            e.ToTable("tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Prompt).HasColumnName("prompt");
            e.Property(x => x.Mode).HasColumnName("mode");
            e.Property(x => x.State).HasColumnName("state");
            e.Property(x => x.Summary).HasColumnName("summary");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
        });

        b.Entity<TaskStepRow>(e =>
        {
            e.ToTable("task_steps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TaskId).HasColumnName("task_id");
            e.Property(x => x.StepIndex).HasColumnName("step_index");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.Detail).HasColumnName("detail");
            e.Property(x => x.Thought).HasColumnName("thought");
            e.Property(x => x.ToolName).HasColumnName("tool_name");
            e.Property(x => x.Arguments).HasColumnName("arguments").HasColumnType("jsonb");
            e.Property(x => x.Result).HasColumnName("result").HasColumnType("jsonb");
            e.Property(x => x.Success).HasColumnName("success");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.At).HasColumnName("at");
        });

        b.Entity<DocumentRow>(e =>
        {
            e.ToTable("documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TaskId).HasColumnName("task_id");
            e.Property(x => x.Filename).HasColumnName("filename");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Category).HasColumnName("category");
            e.Property(x => x.SupplierCode).HasColumnName("supplier_code");
            e.Property(x => x.SupplierName).HasColumnName("supplier_name");
            e.Property(x => x.PartNo).HasColumnName("part_no");
            e.Property(x => x.DocumentNo).HasColumnName("document_no");
            e.Property(x => x.ReviewReason).HasColumnName("review_reason");
            e.Property(x => x.Notified).HasColumnName("notified");
        });

        b.Entity<ClassificationRow>(e =>
        {
            e.ToTable("document_classifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TaskId).HasColumnName("task_id");
            e.Property(x => x.Filename).HasColumnName("filename");
            e.Property(x => x.Category).HasColumnName("category");
            e.Property(x => x.Confidence).HasColumnName("confidence");
            e.Property(x => x.SupplierCode).HasColumnName("supplier_code");
            e.Property(x => x.SupplierName).HasColumnName("supplier_name");
            e.Property(x => x.PartNo).HasColumnName("part_no");
            e.Property(x => x.DocumentNo).HasColumnName("document_no");
            e.Property(x => x.Provider).HasColumnName("provider");
            e.Property(x => x.Model).HasColumnName("model");
            e.Property(x => x.LatencyMs).HasColumnName("latency_ms");
            e.Property(x => x.NeedsManualReview).HasColumnName("needs_manual_review");
            e.Property(x => x.ReviewReason).HasColumnName("review_reason");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<MappingRow>(e =>
        {
            e.ToTable("document_mappings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TaskId).HasColumnName("task_id");
            e.Property(x => x.Filename).HasColumnName("filename");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Query).HasColumnName("query").HasColumnType("jsonb");
            e.Property(x => x.Match).HasColumnName("match");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.Resolved).HasColumnName("resolved").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
    }
}
