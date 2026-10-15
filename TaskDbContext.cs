using Microsoft.EntityFrameworkCore;

namespace APIExample;

public class TaskDbContext : DbContext
{
    public TaskDbContext(DbContextOptions<TaskDbContext> options)
        : base(options)
    {
    }

    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<TaskItem>();

        task.ToTable("TaskItems");
        task.Property(item => item.Title).HasMaxLength(200).IsRequired();
        task.Property(item => item.Priority).HasConversion<string>().HasMaxLength(16);

        // IsRowVersion() для uint — это xmin. Отдельная колонка в таблице не создаётся.
        task.Property(item => item.RowVersion)
            .IsRowVersion()
            .HasColumnName("xmin");
    }
}
