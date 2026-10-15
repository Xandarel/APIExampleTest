using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace APIExample;

public interface ITaskCompletedPublisher
{
    void Publish(TaskCompletedMessage message);
}

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string VirtualHost { get; set; } = "/";

    public string Exchange { get; set; } = "task.events";

    public string RoutingKey { get; set; } = "task.completed";
}

/// <summary>
/// Публикует task.completed в обменник task.events.
/// Запрос только кладёт сообщение в очередь и не ждёт publisher confirms.
/// Если брокер недоступен, задача уже сохранена, а payload пишется в лог.
/// При остановке даём до 5 секунд дослать начатые публикации и закрываем канал.
/// </summary>
public sealed class RabbitMqService : ITaskCompletedPublisher, IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IOptions<RabbitMqOptions> _options;
    private readonly ILogger<RabbitMqService> _logger;
    private readonly Channel<TaskCompletedMessage> _queue = Channel.CreateBounded<TaskCompletedMessage>(
        new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly CancellationTokenSource _runCancellation = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private Task _runner = Task.CompletedTask;
    private IConnection? _connection;
    private IChannel? _channel;
    private int _drained;

    public RabbitMqService(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqService> logger,
        IHostApplicationLifetime lifetime)
    {
        _options = options;
        _logger = logger;
        // Kestrel останавливается после этого hosted service, поэтому досылку и Close
        // делаем в ApplicationStopped — когда активные запросы уже завершились.
        lifetime.ApplicationStopped.Register(DrainAndClose);
    }

    public void Publish(TaskCompletedMessage message)
    {
        if (_queue.Writer.TryWrite(message))
            return;

        _logger.LogError(
            "RabbitMQ publish queue is closed or full. task.completed was not sent. Payload: {Payload}",
            Serialize(message));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runner = RunAsync(_runCancellation.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void DrainAndClose()
    {
        if (Interlocked.Exchange(ref _drained, 1) == 1)
            return;

        _queue.Writer.TryComplete();

        try
        {
            if (!_runner.Wait(TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning("RabbitMQ publisher did not drain within 5 seconds. Closing the connection.");
                _runCancellation.Cancel();
                _runner.Wait(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "RabbitMQ publisher loop did not exit cleanly.");
        }

        CloseConnectionAsync().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        DrainAndClose();
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(cancellationToken))
                await PublishCoreAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "RabbitMQ publisher loop stopped.");
        }
    }

    private async Task PublishCoreAsync(TaskCompletedMessage message, CancellationToken cancellationToken)
    {
        var payload = Serialize(message);

        try
        {
            if (!await EnsureChannelAsync(cancellationToken))
            {
                _logger.LogError(
                    "Failed to publish task.completed because RabbitMQ is unavailable. Payload: {Payload}",
                    payload);
                return;
            }

            var body = System.Text.Encoding.UTF8.GetBytes(payload);
            await _channel!.BasicPublishAsync(
                exchange: _options.Value.Exchange,
                routingKey: _options.Value.RoutingKey,
                mandatory: false,
                basicProperties: new BasicProperties
                {
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Persistent
                },
                body: body,
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Published task.completed for {TaskId} to {Exchange}",
                message.TaskId,
                _options.Value.Exchange);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to publish task.completed. Payload: {Payload}", payload);

            if (exception is not OperationCanceledException)
                await CloseConnectionAsync();
        }
    }

    private async Task<bool> EnsureChannelAsync(CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true })
                return true;

            await CloseCoreAsync();

            var settings = _options.Value;
            var factory = new ConnectionFactory
            {
                HostName = settings.Host,
                Port = settings.Port,
                UserName = settings.UserName,
                Password = settings.Password,
                VirtualHost = settings.VirtualHost,
                AutomaticRecoveryEnabled = false,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(3)
            };

            _connection = await factory.CreateConnectionAsync("APIExample", cancellationToken);
            _channel = await _connection.CreateChannelAsync(options: null, cancellationToken: cancellationToken);
            await _channel.ExchangeDeclareAsync(
                exchange: settings.Exchange,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken: cancellationToken);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not connect to RabbitMQ at {Host}:{Port}", _options.Value.Host, _options.Value.Port);
            await CloseCoreAsync();
            return false;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task CloseConnectionAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            await CloseCoreAsync();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task CloseCoreAsync()
    {
        if (_channel is not null)
        {
            try
            {
                if (_channel.IsOpen)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _channel.CloseAsync(200, "Application stopping", false, timeout.Token);
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "RabbitMQ channel close failed.");
            }

            await _channel.DisposeAsync();
            _channel = null;
        }

        if (_connection is not null)
        {
            try
            {
                if (_connection.IsOpen)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _connection.CloseAsync(200, "Application stopping", TimeSpan.FromSeconds(2), false, timeout.Token);
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "RabbitMQ connection close failed.");
            }

            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private static string Serialize(TaskCompletedMessage message) =>
        JsonSerializer.Serialize(message, JsonOptions);
}
