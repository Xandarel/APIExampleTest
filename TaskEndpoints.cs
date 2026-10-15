using Microsoft.EntityFrameworkCore;

namespace APIExample;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this WebApplication app)
    {
        var tasks = app.MapGroup("/tasks").WithTags("Tasks");

        tasks.MapPost("/", CreateTask);
        tasks.MapGet("/", GetTasks);
        tasks.MapPut("/{id}/complete", CompleteTask);
        tasks.MapDelete("/{id}", DeleteTask);
    }

    private static async Task<IResult> CreateTask(
        CreateTaskRequest? request,
        TaskDbContext db,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Title))
            return Results.BadRequest(new ErrorResponse("Title is required and must not be empty."));

        var title = request.Title.Trim();
        if (title.Length > 200)
            return Results.BadRequest(new ErrorResponse("Title must be at most 200 characters."));

        if (request.Priority is { } priority && !Enum.IsDefined(priority))
            return Results.BadRequest(new ErrorResponse("Priority must be Low, Medium or High."));

        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            Title = title,
            IsCompleted = false,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
            Priority = request.Priority ?? Priority.Medium
        };

        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/tasks/{task.Id}", TaskResponse.From(task));
    }

    private static async Task<IResult> GetTasks(TaskDbContext db, CancellationToken cancellationToken)
    {
        var tasks = await db.Tasks
            .AsNoTracking()
            .OrderBy(task => task.CreatedAt)
            .Select(task => new TaskResponse(
                task.Id,
                task.Title,
                task.IsCompleted,
                task.CreatedAt,
                task.CompletedAt,
                task.Priority))
            .ToListAsync(cancellationToken);

        return Results.Ok(tasks);
    }

    private static async Task<IResult> CompleteTask(
        Guid id,
        TaskDbContext db,
        ITaskCompletedPublisher publisher,
        CancellationToken cancellationToken)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (task is null)
            return Results.NotFound();

        if (task.IsCompleted)
            return Results.Conflict(new ErrorResponse("Task is already completed."));

        task.IsCompleted = true;
        task.CompletedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var stillExists = await db.Tasks.AsNoTracking()
                .AnyAsync(item => item.Id == id, cancellationToken);

            if (!stillExists)
                return Results.NotFound();

            return Results.Conflict(new ErrorResponse("Task was already completed."));
        }

        // Сообщение уходит после успешного сохранения, запрос его не ждёт.
        publisher.Publish(new TaskCompletedMessage(
            task.Id,
            task.Title,
            task.CompletedAt.Value,
            task.Priority));

        return Results.Ok(TaskResponse.From(task));
    }

    private static async Task<IResult> DeleteTask(
        Guid id,
        TaskDbContext db,
        CancellationToken cancellationToken)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (task is null)
            return Results.NotFound();

        db.Tasks.Remove(task);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}

public sealed record CreateTaskRequest(string? Title, Priority? Priority);

public sealed record TaskResponse(
    Guid Id,
    string Title,
    bool IsCompleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    Priority Priority)
{
    public static TaskResponse From(TaskItem task) => new(
        task.Id,
        task.Title,
        task.IsCompleted,
        task.CreatedAt,
        task.CompletedAt,
        task.Priority);
}

public sealed record ErrorResponse(string Error);

public sealed record TaskCompletedMessage(
    Guid TaskId,
    string Title,
    DateTimeOffset CompletedAt,
    Priority Priority);
