using System.Text.Json.Serialization;
using APIExample;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "APIExample",
        Version = "v1"
    });

    options.MapType<Priority>(() => new OpenApiSchema
    {
        Type = "string",
        Enum = Enum.GetNames<Priority>()
            .Select(name => (IOpenApiAny)new OpenApiString(name))
            .ToList()
    });
});

builder.Services.AddDbContext<TaskDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton<RabbitMqService>();
builder.Services.AddSingleton<ITaskCompletedPublisher>(provider => provider.GetRequiredService<RabbitMqService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<RabbitMqService>());

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
    try
    {
        await db.Database.MigrateAsync();
    }
    catch (Exception exception)
    {
        app.Logger.LogCritical(
            exception,
            "Database migration failed. Start PostgreSQL with 'docker compose up -d' and check ConnectionStrings:Default.");
        throw;
    }
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="ru">
    <head>
      <meta charset="utf-8">
      <title>APIExample</title>
    </head>
    <body>
      <h1>APIExample</h1>
      <p><a href="/swagger">Swagger</a></p>
      <ul>
        <li>POST /tasks</li>
        <li>GET /tasks</li>
        <li>PUT /tasks/{id}/complete</li>
        <li>DELETE /tasks/{id}</li>
      </ul>
    </body>
    </html>
    """,
    "text/html; charset=utf-8"));

app.MapTaskEndpoints();
app.Run();
