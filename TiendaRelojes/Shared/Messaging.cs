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
/// Contratos y cliente Kafka reutilizados por inventario, pedidos y pagos.
/// </summary>
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
            EnableIdempotence = true
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

    /// <summary>
    /// Crea el tópico si todavía no existe. Un broker nuevo no lo tiene
    /// hasta el primer mensaje, y los consumidores fallan al suscribirse.
    /// </summary>
    static void EnsureTopic(IConfiguration config)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = Bootstrap(config)
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
    }
}
