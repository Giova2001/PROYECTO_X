
/* ============================================================
   MICROSERVICIO DE PEDIDOS - ORDERS SERVICE

   Tecnologías:
   - ASP.NET Core
   - Entity Framework Core
   - PostgreSQL (Supabase)
   - Apache Kafka
   - HttpClient

   FUNCIONALIDADES:
   1. Configuración del servidor
   2. Inicialización de la base de datos
   3. Verificación del servicio
   4. Consulta de pedidos
   5. Registro de nuevos pedidos
   6. Cancelación de pedidos
   7. Modelos de entrada (DTO)
   8. Entidades de base de datos
   9. Contexto Entity Framework
   10. Consumidor de eventos Kafka
   11. Actualización de estados
   12. Manejo de errores
============================================================ */


// ============================================================
// 1. IMPORTACIÓN DE DEPENDENCIAS
// ============================================================

// Permite trabajar con Entity Framework Core y PostgreSQL (Supabase).
using Microsoft.EntityFrameworkCore;

// Contiene las clases y eventos compartidos
// entre los diferentes microservicios.
using Shared;


// ============================================================
// 2. CONFIGURACIÓN DEL SERVIDOR
// ============================================================

// Crea el constructor de la aplicación.
var builder = WebApplication.CreateBuilder(args);


// ------------------------------------------------------------
// 2.1. CONFIGURACIÓN DE LA BASE DE DATOS
// ------------------------------------------------------------

// Registra OrdersDb contra el esquema orders de Supabase.
var supabaseDb = builder.Configuration.GetConnectionString("Db");
if (string.IsNullOrWhiteSpace(supabaseDb))
{
    throw new InvalidOperationException(
        "Falta ConnectionStrings:Db con la cadena de Postgres de Supabase."
    );
}

builder.Services.AddDbContext<OrdersDb>(options =>
    options.UseNpgsql(supabaseDb, npgsql =>
        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders")
    )
);


// ------------------------------------------------------------
// 2.2. REGISTRO DE SERVICIOS
// ------------------------------------------------------------

// Registra el servicio que escucha eventos Kafka.
builder.Services.AddHostedService<OrderWorker>();

// Habilita HttpClient para comunicarse con el
// microservicio de inventario.
builder.Services.AddHttpClient();


// ------------------------------------------------------------
// 2.3. CONFIGURACIÓN DE CORS
// ------------------------------------------------------------

// Permite solicitudes desde cualquier origen.
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
    )
);


// ------------------------------------------------------------
// 2.4. CONSTRUCCIÓN DE LA APLICACIÓN
// ------------------------------------------------------------

var app = builder.Build();

// Habilita la política CORS.
app.UseCors();


// ============================================================
// 3. INICIALIZACIÓN DE LA BASE DE DATOS
// ============================================================

// Crea un ámbito de servicios para obtener OrdersDb.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<OrdersDb>();

    // El historial de migraciones vive en este esquema, así que debe existir antes.
    db.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS orders;");
    db.Database.Migrate();
}


// ============================================================
// 4. ENDPOINT DE VERIFICACIÓN
// ============================================================

/*
   GET /health

   Comprueba que el microservicio esté funcionando.

   Respuesta:
   {
       "status": "ok"
   }
*/

app.MapGet("/health", () =>
    Results.Ok(new
    {
        status = "ok"
    })
);


// ============================================================
// 5. CONSULTA DE PEDIDOS
// ============================================================


// ------------------------------------------------------------
// 5.1. OBTENER TODOS LOS PEDIDOS
// ------------------------------------------------------------

/*
   GET /orders

   Devuelve todos los pedidos registrados.

   Incluye:
   - Datos del cliente.
   - Productos solicitados.
   - Cantidades.
   - Precio unitario.
   - Total.
   - Estado del pedido.

   Los pedidos se ordenan desde el más reciente.
*/

app.MapGet("/orders", async (OrdersDb db) =>
    await db.Orders
        .Include(order => order.Items)
        .OrderByDescending(order => order.CreatedAt)
        .ToListAsync()
);


// ------------------------------------------------------------
// 5.2. OBTENER PEDIDO POR IDENTIFICADOR
// ------------------------------------------------------------

/*
   GET /orders/{id}

   Busca un pedido mediante su identificador GUID.

   Respuestas:
   - 200 OK: pedido encontrado.
   - 404 Not Found: pedido inexistente.
*/

app.MapGet(
    "/orders/{id:guid}",
    async (Guid id, OrdersDb db) =>
        await db.Orders
            .Include(order => order.Items)
            .FirstOrDefaultAsync(order => order.Id == id)
            is { } order

            ? Results.Ok(order)
            : Results.NotFound()
);


// ============================================================
// 6. REGISTRO DE NUEVOS PEDIDOS
// ============================================================

/*
   POST /orders

   Registra un nuevo pedido en la tienda.

   PROCESO:

   1. Validar datos del cliente.
   2. Consultar productos al microservicio de inventario.
   3. Validar productos y cantidades.
   4. Calcular el total.
   5. Guardar el pedido en Supabase.
   6. Publicar evento OrderPlaced en Kafka.
   7. Devolver el pedido registrado.
*/

app.MapPost(
    "/orders",
    async (
        CreateOrder request,
        OrdersDb db,
        IHttpClientFactory http,
        IConfiguration config
    ) =>
    {

        // ----------------------------------------------------
        // 6.1. VALIDACIÓN DE DATOS DEL CLIENTE
        // ----------------------------------------------------

        if (
            string.IsNullOrWhiteSpace(request.Customer) ||
            string.IsNullOrWhiteSpace(request.Phone) ||
            string.IsNullOrWhiteSpace(request.Address) ||
            request.Items is null ||
            request.Items.Count == 0 ||
            request.Items.Count > 30
        )
        {
            return Results.BadRequest(
                "Datos de pedido incompletos"
            );
        }


        // ----------------------------------------------------
        // 6.2. CONEXIÓN CON EL MICROSERVICIO DE INVENTARIO
        // ----------------------------------------------------

        // Crea un cliente HTTP.
        var client = http.CreateClient();

        // Obtiene la dirección del servicio de inventario.
        var baseUrl = config["INVENTORY_URL"]
            ?? "http://localhost:5101";

        // Consulta los productos disponibles.
        var products = await client.GetFromJsonAsync<
            List<CatalogProduct>
        >(
            baseUrl + "/products"
        ) ?? new();


        // ----------------------------------------------------
        // 6.3. PREPARACIÓN DE PRODUCTOS DEL PEDIDO
        // ----------------------------------------------------

        var lines = new List<OrderItem>();

        // Agrupa productos repetidos y suma sus cantidades.
        var groupedItems = request.Items
            .GroupBy(item => item.ProductId)
            .Select(group => new OrderInput(
                group.Key,
                group.Sum(item => item.Quantity)
            ));


        // ----------------------------------------------------
        // 6.4. VALIDACIÓN DE PRODUCTOS Y CANTIDADES
        // ----------------------------------------------------

        foreach (var input in groupedItems)
        {
            // Busca el producto en el catálogo.
            var product = products.FirstOrDefault(
                x => x.Id == input.ProductId
            );

            // Comprueba que exista y tenga cantidad válida.
            if (
                product is null ||
                input.Quantity <= 0 ||
                input.Quantity > 100
            )
            {
                return Results.BadRequest(
                    "Producto o cantidad inválida"
                );
            }

            // Agrega el producto al detalle del pedido.
            lines.Add(new OrderItem
            {
                ProductId = product.Id,
                Name = product.Name,
                Quantity = input.Quantity,
                UnitPrice = product.Price
            });
        }


        // ----------------------------------------------------
        // 6.5. CREACIÓN DEL PEDIDO
        // ----------------------------------------------------

        var order = new ShopOrder
        {
            // Genera un identificador único.
            Id = Guid.NewGuid(),

            // Información del cliente.
            Customer = request.Customer,
            Phone = request.Phone,
            Address = request.Address,

            // Define el método de pago.
            PaymentMethod =
                request.PaymentMethod == "contra-entrega"
                    ? "contra-entrega"
                    : "transferencia",

            // Estado inicial del pedido.
            Status = "pending-stock",

            // Productos solicitados.
            Items = lines,

            // Calcula el total del pedido.
            Total = lines.Sum(
                item => item.UnitPrice * item.Quantity
            )
        };


        // ----------------------------------------------------
        // 6.6. GUARDAR PEDIDO EN LA BASE DE DATOS
        // ----------------------------------------------------

        db.Orders.Add(order);

        await db.SaveChangesAsync();


        // ----------------------------------------------------
        // 6.7. PUBLICAR EVENTO EN KAFKA
        // ----------------------------------------------------

        try
        {
            // Notifica al servicio de inventario
            // que se ha registrado un nuevo pedido.
            await Events.Publish(
                config,
                "OrderPlaced",
                new OrderPlaced(
                    order.Id,

                    lines.Select(item => new OrderLine(
                        item.ProductId,
                        item.Quantity,
                        item.UnitPrice
                    )).ToList()
                )
            );
        }
        catch
        {
            // El pedido permanece guardado en Supabase,
            // aunque falle la publicación del evento.
            return Results.Problem(
                "Pedido guardado, pero no se pudo publicar " +
                "el evento. Requiere revisión.",
                statusCode: 503
            );
        }


        // ----------------------------------------------------
        // 6.8. RESPUESTA DEL SERVIDOR
        // ----------------------------------------------------

        // Devuelve HTTP 202 Accepted.
        // El pedido todavía debe pasar por la
        // validación de existencias.
        return Results.Accepted(
            $"/orders/{order.Id}",
            order
        );
    }
);


// ============================================================
// 7. CANCELACIÓN DE PEDIDOS
// ============================================================

/*
   POST /orders/{id}/cancel

   Permite cancelar pedidos que estén:

   - pending-stock
   - awaiting-payment

   No permite cancelar pedidos pagados,
   rechazados o previamente cancelados.
*/

app.MapPost(
    "/orders/{id:guid}/cancel",
    async (
        Guid id,
        OrdersDb db,
        IConfiguration config
    ) =>
    {

        // ----------------------------------------------------
        // 7.1. BUSCAR PEDIDO
        // ----------------------------------------------------

        var order = await db.Orders.FindAsync(id);

        if (order is null)
        {
            return Results.NotFound();
        }


        // ----------------------------------------------------
        // 7.2. VALIDAR ESTADO DEL PEDIDO
        // ----------------------------------------------------

        if (
            order.Status is not (
                "pending-stock" or "awaiting-payment"
            )
        )
        {
            return Results.BadRequest(
                "El pedido ya no puede cancelarse"
            );
        }


        // ----------------------------------------------------
        // 7.3. ACTUALIZAR ESTADO
        // ----------------------------------------------------

        order.Status = "cancelled";

        await db.SaveChangesAsync();


        // ----------------------------------------------------
        // 7.4. NOTIFICAR CANCELACIÓN
        // ----------------------------------------------------

        // Publica un evento para que inventario
        // libere las unidades reservadas.
        await Events.Publish(
            config,
            "OrderCancelled",
            new OrderCancelled(id)
        );


        // ----------------------------------------------------
        // 7.5. RESPUESTA
        // ----------------------------------------------------

        return Results.Ok(order);
    }
);


// ============================================================
// 8. INICIALIZACIÓN DEL SERVIDOR
// ============================================================

// Inicia la aplicación ASP.NET Core.
app.Run();


// ============================================================
// 9. MODELOS DE ENTRADA (DTO)
// ============================================================


// ------------------------------------------------------------
// 9.1. MODELO PARA CREAR PEDIDOS
// ------------------------------------------------------------

/// <summary>
/// Representa la información enviada por el cliente
/// cuando registra un nuevo pedido.
/// </summary>
record CreateOrder(
    string Customer,
    string Phone,
    string Address,
    string PaymentMethod,
    List<OrderInput> Items
);


// ------------------------------------------------------------
// 9.2. MODELO DE PRODUCTO SOLICITADO
// ------------------------------------------------------------

/// <summary>
/// Identifica el producto y la cantidad solicitada.
/// </summary>
record OrderInput(
    int ProductId,
    int Quantity
);


// ------------------------------------------------------------
// 9.3. MODELO DE PRODUCTO DEL INVENTARIO
// ------------------------------------------------------------

/// <summary>
/// Representa los datos recibidos desde el
/// microservicio de inventario.
/// </summary>
record CatalogProduct(
    int Id,
    string Name,
    decimal Price,
    int Stock,
    int Reserved
);


// ============================================================
// 10. ENTIDADES DE BASE DE DATOS
// ============================================================


// ------------------------------------------------------------
// 10.1. ENTIDAD PEDIDO
// ------------------------------------------------------------

/// <summary>
/// Representa un pedido registrado en la tienda.
/// </summary>
public class ShopOrder
{
    // Identificador único del pedido.
    public Guid Id { get; set; }

    // Información del cliente.
    public string Customer { get; set; } = "";

    public string Phone { get; set; } = "";

    public string Address { get; set; } = "";

    // Método de pago seleccionado.
    public string PaymentMethod { get; set; } = "";

    // Total monetario del pedido.
    public decimal Total { get; set; }

    // Estado actual del pedido.
    public string Status { get; set; } = "pending-stock";

    // Fecha de creación en UTC.
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Productos asociados al pedido.
    public List<OrderItem> Items { get; set; } = new();
}


// ------------------------------------------------------------
// 10.2. ENTIDAD DETALLE DE PEDIDO
// ------------------------------------------------------------

/// <summary>
/// Representa un producto incluido en un pedido.
/// </summary>
public class OrderItem
{
    public int Id { get; set; }

    // Identificador del pedido al que pertenece.
    public Guid ShopOrderId { get; set; }

    // Identificador del producto en inventario.
    public int ProductId { get; set; }

    // Nombre del producto al momento de comprarlo.
    public string Name { get; set; } = "";

    // Cantidad solicitada.
    public int Quantity { get; set; }

    // Precio unitario registrado en el pedido.
    public decimal UnitPrice { get; set; }
}


// ------------------------------------------------------------
// 10.3. ENTIDAD EVENTO PROCESADO
// ------------------------------------------------------------

/// <summary>
/// Registra los identificadores de eventos Kafka
/// que ya fueron procesados.
/// </summary>
public class ProcessedOrderEvent
{
    public int Id { get; set; }

    public Guid EventId { get; set; }
}


// ============================================================
// 11. CONTEXTO DE BASE DE DATOS
// ============================================================

/// <summary>
/// Administra las entidades del microservicio
/// mediante Entity Framework Core.
/// </summary>
public class OrdersDb(
    DbContextOptions<OrdersDb> options
) : DbContext(options)
{
    // Tabla de pedidos y sus detalles.
    public DbSet<ShopOrder> Orders =>
        Set<ShopOrder>();

    // Tabla de eventos procesados.
    public DbSet<ProcessedOrderEvent> Processed =>
        Set<ProcessedOrderEvent>();


    // Configura restricciones de la base de datos.
    protected override void OnModelCreating(
        ModelBuilder builder
    )
    {
        builder.HasDefaultSchema("orders");

        builder.Entity<ShopOrder>()
            .Property(o => o.Total)
            .HasPrecision(18, 2);

        builder.Entity<OrderItem>()
            .Property(i => i.UnitPrice)
            .HasPrecision(18, 2);

        // Impide registrar dos veces el mismo evento.
        builder.Entity<ProcessedOrderEvent>()
            .HasIndex(x => x.EventId)
            .IsUnique();
    }
}


// ============================================================
// 12. SERVICIO DE EVENTOS KAFKA - ORDER WORKER
// ============================================================

/// <summary>
/// Escucha eventos provenientes de inventario y pagos
/// para actualizar automáticamente los estados de pedidos.
/// </summary>
public class OrderWorker(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<OrderWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken ct
    )
    {
        // Espera inicial para permitir que otros
        // servicios terminen de inicializarse.
        await Task.Delay(3000, ct);


        // ----------------------------------------------------
        // 12.1. CICLO PRINCIPAL DEL CONSUMIDOR
        // ----------------------------------------------------

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Crea un consumidor Kafka.
                using var consumer = Events.Consumer(
                    config,
                    "orders-service"
                );


                // Procesa eventos mientras el servicio esté activo.
                while (!ct.IsCancellationRequested)
                {
                    // Espera un nuevo mensaje.
                    var result = consumer.Consume(ct);

                    // Convierte el mensaje a EventMessage.
                    var ev = Events.Parse<EventMessage>(
                        result.Message.Value
                    );


                    // ----------------------------------------
                    // 12.2. OBTENER CONTEXTO DE BASE DE DATOS
                    // ----------------------------------------

                    using var scope = scopes.CreateScope();

                    var db = scope.ServiceProvider
                        .GetRequiredService<OrdersDb>();


                    // ----------------------------------------
                    // 12.3. VERIFICAR EVENTOS DUPLICADOS
                    // ----------------------------------------

                    // Evita procesar nuevamente eventos
                    // que ya fueron registrados.
                    if (await db.Processed.AnyAsync(
                        x => x.EventId == ev.Id,
                        ct
                    ))
                    {
                        consumer.Commit(result);
                        continue;
                    }


                    // ----------------------------------------
                    // 12.4. IDENTIFICAR TIPO DE EVENTO
                    // ----------------------------------------

                    // Obtiene el identificador del pedido
                    // según el evento recibido.
                    Guid? orderId = ev.Type switch
                    {
                        "StockReserved" =>
                            Events.Parse<StockReserved>(
                                ev.Data
                            ).OrderId,

                        "StockRejected" =>
                            Events.Parse<StockRejected>(
                                ev.Data
                            ).OrderId,

                        "PaymentConfirmed" =>
                            Events.Parse<PaymentConfirmed>(
                                ev.Data
                            ).OrderId,

                        "PaymentFailed" =>
                            Events.Parse<PaymentFailed>(
                                ev.Data
                            ).OrderId,

                        _ => null
                    };


                    // ----------------------------------------
                    // 12.5. BUSCAR PEDIDO RELACIONADO
                    // ----------------------------------------

                    if (orderId.HasValue)
                    {
                        var order = await db.Orders.FindAsync(
                            new object[] { orderId.Value },
                            ct
                        );


                        // Solo procesa pedidos existentes
                        // que no hayan sido cancelados.
                        if (
                            order != null &&
                            order.Status != "cancelled"
                        )
                        {

                            // --------------------------------
                            // 12.6. INVENTARIO RESERVADO
                            // --------------------------------

                            if (
                                ev.Type == "StockReserved" &&
                                order.Status == "pending-stock"
                            )
                            {
                                // Inventario confirmó existencias.
                                // El pedido queda pendiente de pago.
                                order.Status = "awaiting-payment";
                            }


                            // --------------------------------
                            // 12.7. INVENTARIO RECHAZADO
                            // --------------------------------

                            if (
                                ev.Type == "StockRejected" &&
                                order.Status == "pending-stock"
                            )
                            {
                                // No hay suficientes existencias.
                                order.Status = "rejected";
                            }


                            // --------------------------------
                            // 12.8. PAGO CONFIRMADO
                            // --------------------------------

                            if (
                                ev.Type == "PaymentConfirmed" &&
                                order.Status == "awaiting-payment"
                            )
                            {
                                // El pago fue confirmado.
                                order.Status = "paid";
                            }


                            // --------------------------------
                            // 12.9. PAGO RECHAZADO
                            // --------------------------------

                            if (
                                ev.Type == "PaymentFailed" &&
                                order.Status == "awaiting-payment"
                            )
                            {
                                // El pago no fue aprobado.
                                order.Status = "payment-failed";
                            }
                        }
                    }


                    // ----------------------------------------
                    // 12.10. REGISTRAR EVENTO PROCESADO
                    // ----------------------------------------

                    db.Processed.Add(
                        new ProcessedOrderEvent
                        {
                            EventId = ev.Id
                        }
                    );

                    // Guarda el estado actualizado del pedido.
                    await db.SaveChangesAsync(ct);


                    // ----------------------------------------
                    // 12.11. CONFIRMAR MENSAJE EN KAFKA
                    // ----------------------------------------

                    consumer.Commit(result);
                }
            }


            // ------------------------------------------------
            // 12.12. CANCELACIÓN DEL SERVICIO
            // ------------------------------------------------

            catch (OperationCanceledException)
            {
                break;
            }


            // ------------------------------------------------
            // 12.13. MANEJO DE ERRORES Y REINTENTOS
            // ------------------------------------------------

            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Orders Kafka worker retrying"
                );

                // Espera antes de intentar reconectarse.
                await Task.Delay(2500, ct);
            }
        }
    }
}
