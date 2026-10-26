
using Microsoft.EntityFrameworkCore;
using Shared;

// =====================================================
// 1. CONFIGURACIÓN DEL MICROSERVICIO
// =====================================================

var builder = WebApplication.CreateBuilder(args);

// Postgres de Supabase. Este servicio solo usa el esquema payments.
var supabaseDb = builder.Configuration.GetConnectionString("Db");
if (string.IsNullOrWhiteSpace(supabaseDb))
{
    throw new InvalidOperationException(
        "Falta ConnectionStrings:Db con la cadena de Postgres de Supabase."
    );
}

builder.Services.AddDbContext<PaymentsDb>(options =>
    options.UseNpgsql(supabaseDb, npgsql =>
        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "payments")
    )
);

// Servicios para Kafka y comunicación HTTP.
builder.Services.AddHostedService<PaymentsWorker>();
builder.Services.AddHttpClient();

// Permitir solicitudes desde otros servicios.
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader()
    )
);

// Token de AuthApi: listar pagos es de administradores;
// simular un pago exige sesión y ser dueño del pedido.
builder.Services.AddShopAuth(builder.Configuration);

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Crear el esquema payments y sus tablas en Supabase si faltan.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<PaymentsDb>();

    db.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS payments;");
    db.Database.Migrate();
}


// =====================================================
// 2. ENDPOINTS DE CONSULTA
// =====================================================

// Verificar funcionamiento del servicio.
app.MapGet("/health", () =>
    Results.Ok(new { status = "ok" })
);

// Consultar todos los pagos.
app.MapGet("/payments", async (PaymentsDb db) =>
    await db.Payments
        .OrderByDescending(p => p.CreatedAt)
        .ToListAsync()
).RequireAuthorization(Policies.Admin);

// Consultar pago mediante ID del pedido.
app.MapGet("/payments/{id:guid}", async (
    Guid id,
    PaymentsDb db
) =>
    await db.Payments
        .FirstOrDefaultAsync(p => p.OrderId == id) is { } payment
            ? Results.Ok(payment)
            : Results.NotFound()
).RequireAuthorization(Policies.Admin);


// =====================================================
// 3. SIMULACIÓN DE PAGOS
// =====================================================

// Simula un pago aprobado o rechazado.
// No realiza transacciones bancarias reales.
app.MapPost("/payments/{id:guid}/simulate", async (
    Guid id,
    SimulateRequest request,
    HttpContext http,
    PaymentsDb db,
    IConfiguration config,
    IHttpClientFactory factory
) =>
{
    // Validar el resultado solicitado.
    if (request.Result is not ("approved" or "rejected"))
        return Results.BadRequest("Resultado no válido");

    // Consultar el pedido en Orders Service con el mismo token.
    // Orders responde 403/404 si el pedido no es de este usuario.
    var ordersUrl = config["ORDERS_URL"]
        ?? "http://localhost:5102";

    using var lookup = new HttpRequestMessage(
        HttpMethod.Get,
        $"{ordersUrl}/orders/{id}"
    );
    lookup.Headers.TryAddWithoutValidation(
        "Authorization",
        http.Request.Headers.Authorization.ToString()
    );

    using var response = await factory.CreateClient().SendAsync(lookup);

    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        return Results.Forbid();

    if (!response.IsSuccessStatusCode)
        return Results.NotFound("Pedido no encontrado");

    var order = await response.Content
        .ReadFromJsonAsync<OrderSnapshot>();

    // El pedido debe tener inventario reservado.
    if (order is null || order.Status != "awaiting-payment")
        return Results.BadRequest(
            "El pedido debe tener inventario reservado " +
            "y estar pendiente de pago"
        );

    // Buscar o crear el registro del pago.
    var payment = await db.Payments
        .FirstOrDefaultAsync(p => p.OrderId == id);

    if (payment is null)
    {
        payment = new Payment { OrderId = id };
        db.Payments.Add(payment);
    }

    // Evitar procesar un pago más de una vez.
    if (payment.Status != "pending")
        return Results.BadRequest("El pago ya fue procesado");

    // Registrar resultado y monto del pago.
    payment.Status = request.Result;
    payment.Amount = order.Total;

    await db.SaveChangesAsync();

    // Notificar el resultado a los demás microservicios.
    await Events.Publish(
        config,
        request.Result == "approved"
            ? "PaymentConfirmed"
            : "PaymentFailed",
        request.Result == "approved"
            ? new PaymentConfirmed(id)
            : new PaymentFailed(id)
    );

    return Results.Ok(payment);
}).RequireAuthorization(Policies.Cliente);


// =====================================================
// 4. INICIAR APLICACIÓN
// =====================================================

app.Run();


// =====================================================
// 5. MODELOS DE ENTRADA (DTO)
// =====================================================

// Resultado de la simulación.
record SimulateRequest(string Result);

// Información obtenida del servicio de pedidos.
record OrderSnapshot(
    Guid Id,
    string Status,
    decimal Total
);


// =====================================================
// 6. ENTIDADES DE BASE DE DATOS
// =====================================================

// Registro de pagos asociados a pedidos.
public class Payment
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }

    // pending, approved o rejected.
    public string Status { get; set; } = "pending";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Evita procesar eventos Kafka duplicados.
public class ProcessedPaymentEvent
{
    public int Id { get; set; }
    public Guid EventId { get; set; }
}


// =====================================================
// 7. CONTEXTO DE BASE DE DATOS
// =====================================================

public class PaymentsDb(
    DbContextOptions<PaymentsDb> options
) : DbContext(options)
{
    public DbSet<Payment> Payments =>
        Set<Payment>();

    public DbSet<ProcessedPaymentEvent> Processed =>
        Set<ProcessedPaymentEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("payments");

        builder.Entity<Payment>()
            .Property(p => p.Amount)
            .HasPrecision(18, 2);

        // Un registro de pago por pedido.
        builder.Entity<Payment>()
            .HasIndex(p => p.OrderId)
            .IsUnique();

        // Cada evento Kafka se registra una sola vez.
        builder.Entity<ProcessedPaymentEvent>()
            .HasIndex(e => e.EventId)
            .IsUnique();
    }
}


// =====================================================
// 8. CONSUMIDOR DE EVENTOS KAFKA
// =====================================================

public class PaymentsWorker(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<PaymentsWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken ct
    )
    {
        // Esperar la inicialización de los servicios.
        await Task.Delay(3000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Conectar con Kafka.
                using var consumer = Events.Consumer(
                    config,
                    "payments-service"
                );

                while (!ct.IsCancellationRequested)
                {
                    // Recibir y convertir evento.
                    var result = consumer.Consume(ct);

                    var ev = Events.Parse<EventMessage>(
                        result.Message.Value
                    );

                    using var scope = scopes.CreateScope();

                    var db = scope.ServiceProvider
                        .GetRequiredService<PaymentsDb>();

                    // Evitar procesar eventos duplicados.
                    if (await db.Processed.AnyAsync(
                        x => x.EventId == ev.Id, ct
                    ))
                    {
                        consumer.Commit(result);
                        continue;
                    }

                    // Crear pago pendiente al reservar stock.
                    if (ev.Type == "StockReserved")
                    {
                        var id = Events.Parse<StockReserved>(
                            ev.Data
                        ).OrderId;

                        if (!await db.Payments.AnyAsync(
                            x => x.OrderId == id, ct
                        ))
                        {
                            db.Payments.Add(new Payment
                            {
                                OrderId = id
                            });
                        }
                    }

                    // Registrar evento procesado.
                    db.Processed.Add(
                        new ProcessedPaymentEvent
                        {
                            EventId = ev.Id
                        }
                    );

                    await db.SaveChangesAsync(ct);

                    // Confirmar procesamiento en Kafka.
                    consumer.Commit(result);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Registrar error y reintentar conexión.
                logger.LogError(
                    ex,
                    "Payments Kafka worker retrying"
                );

                await Task.Delay(2500, ct);
            }
        }
    }
}
