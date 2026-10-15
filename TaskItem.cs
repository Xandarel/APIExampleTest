namespace APIExample;

public class TaskItem
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Заполняется только в момент завершения, не при создании.
    public DateTimeOffset? CompletedAt { get; set; }

    public Priority Priority { get; set; }

    // Optimistic concurrency. Npgsql maps IsRowVersion() to the xmin system column.
    public uint RowVersion { get; set; }
}

public enum Priority
{
    Low,
    Medium,
    High
}
