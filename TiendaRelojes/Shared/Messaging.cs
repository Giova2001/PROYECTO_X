using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Shared;

public record EventMessage(Guid Id, string Type, string Data, DateTime CreatedAt);
public record OrderLine(int ProductId, int Quantity, decimal UnitPrice);
public record OrderPlaced(Guid OrderId, List<OrderLine> Items);
public record StockReserved(Guid OrderId);
public record StockRejected(Guid OrderId, string Reason);
public record PaymentConfirmed(Guid OrderId);
public record PaymentFailed(Guid OrderId);
public record OrderCancelled(Guid OrderId);

/// <summary>
/// El pedido cambió de cantidades. Inventario compara las líneas anteriores
/// con las nuevas y ajusta la reserva o el stock, según el estado previo.
/// </summary>
public record OrderRevised(
    Guid OrderId,
    string PreviousStatus,
    List<OrderLine> PreviousItems,
    List<OrderLine> Items);

/// <summary>
/// El pedido se eliminó. Inventario deshace la reserva o devuelve el stock.
/// </summary>
public record OrderRemoved(
    Guid OrderId,
    string PreviousStatus,
    List<OrderLine> Items);

/// <summary>
/// Contratos y cliente Kafka reutilizados por inventario, pedidos y pagos.
/// </summary>
public static class ShopDatabase
{
    public static bool IsSqlite(string connectionString)
    {
        var value = connectionString.Trim();
        return value.Contains(".db", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("postgres", StringComparison.OrdinalIgnoreCase);
    }
}

public static class Events
{
    public const string Topic = "shop-events";

    public static string Json<T>(T value) => JsonSerializer.Serialize(value);

    public static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value)!;

    public static async Task Publish(
        IConfiguration config,
        string type,
        object data,
        CancellationToken ct = default)
    {
        EnsureTopic(config);

        var options = new ProducerConfig
        {
            BootstrapServers = Bootstrap(config),
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 12000,
            SocketTimeoutMs = 8000,
            RequestTimeoutMs = 8000
        };

        using var producer = new ProducerBuilder<string, string>(options).Build();
        var message = new EventMessage(Guid.NewGuid(), type, Json(data), DateTime.UtcNow);

        await producer.ProduceAsync(
            Topic,
            new Message<string, string>
            {
                Key = message.Id.ToString(),
                Value = Json(message)
            },
            ct);
    }

    public static IConsumer<string, string> Consumer(IConfiguration config, string group)
    {
        EnsureTopic(config);

        var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = Bootstrap(config),
            GroupId = group,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            AllowAutoCreateTopics = true
        }).Build();

        consumer.Subscribe(Topic);
        return consumer;
    }

    static string Bootstrap(IConfiguration config) =>
        config["KAFKA_BOOTSTRAP"] ?? "localhost:9092";

    static readonly object TopicGate = new();
    static volatile bool topicReady;

    /// <summary>
    /// Crea el tópico si todavía no existe y espera a que el broker
    /// lo publique en los metadatos. Sin esa espera, los consumidores
    /// fallan con "Unknown topic or partition".
    /// </summary>
    static void EnsureTopic(IConfiguration config)
    {
        if (topicReady) return;

        lock (TopicGate)
        {
            if (topicReady) return;

            using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = Bootstrap(config),
                SocketTimeoutMs = 5000,
                ApiVersionRequestTimeoutMs = 5000
            }).Build();

            try
            {
                admin.CreateTopicsAsync(new[]
                {
                    new TopicSpecification
                    {
                        Name = Topic,
                        NumPartitions = 1,
                        ReplicationFactor = 1
                    }
                }).GetAwaiter().GetResult();
            }
            catch (CreateTopicsException ex) when (ex.Results.All(result =>
                result.Error.Code is ErrorCode.TopicAlreadyExists or ErrorCode.NoError))
            {
            }

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var metadata = admin.GetMetadata(Topic, TimeSpan.FromSeconds(4));
                    var topic = metadata.Topics.FirstOrDefault(item => item.Topic == Topic);
                    if (topic is { Error.Code: ErrorCode.NoError } && topic.Partitions.Count > 0)
                    {
                        topicReady = true;
                        return;
                    }
                }
                catch (KafkaException)
                {
                }

                Thread.Sleep(400);
            }

            throw new InvalidOperationException(
                "Kafka no dejó listo el tópico shop-events.");
        }
    }
}
