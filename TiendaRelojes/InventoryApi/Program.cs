
/* ============================================================
   MICROSERVICIO DE INVENTARIO
   Tecnología: ASP.NET Core + Entity Framework Core + PostgreSQL (Supabase)
   Comunicación: Apache Kafka

   FUNCIONALIDADES:
   1. Configuración del servidor
   2. Inicialización de la base de datos
   3. Productos iniciales
   4. API de productos
   5. Movimientos de inventario
   6. Ajustes de existencias
   7. Modelos de entrada
   8. Entidades de base de datos
   9. Contexto Entity Framework
   10. Procesamiento de eventos Kafka
   11. Reservas y confirmaciones de pagos
   12. Ejecución del servidor
============================================================ */


// ============================================================
// 1. IMPORTACIÓN DE DEPENDENCIAS
// ============================================================

// Permite trabajar con Entity Framework Core y PostgreSQL (Supabase).
using Microsoft.EntityFrameworkCore;

// Contiene las clases compartidas entre microservicios,
// incluyendo los eventos y sus respectivos mensajes.
using Shared;


// ============================================================
// 2. CONFIGURACIÓN DEL SERVIDOR
// ============================================================

// Crea el constructor de la aplicación ASP.NET Core.
var builder = WebApplication.CreateBuilder(args);

// SQLite conserva los datos de la demostración local.
// Postgres se usa cuando la cadena es de Supabase.
var shopDb = builder.Configuration.GetConnectionString("Db");
if (string.IsNullOrWhiteSpace(shopDb))
{
    throw new InvalidOperationException(
        "Falta ConnectionStrings:Db."
    );
}

var inventoryUsesSqlite = ShopDatabase.IsSqlite(shopDb);

builder.Services.AddDbContext<InventoryDb>(options =>
{
    if (inventoryUsesSqlite)
    {
        options.UseSqlite(shopDb);
    }
    else
    {
        options.UseNpgsql(shopDb, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "inventory")
        );
    }
});

// Registra el servicio que escucha eventos de Kafka.
builder.Services.AddHostedService<InventoryWorker>();

// Configura CORS para permitir solicitudes HTTP
// desde diferentes orígenes.
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
    )
);

// Valida el token emitido por AuthApi. El catálogo sigue siendo público;
// crear, editar, eliminar y ajustar productos exige rol admin.
builder.Services.AddShopAuth(builder.Configuration);

// Construye la aplicación.
var app = builder.Build();

// Habilita CORS.
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();


// ============================================================
// 3. INICIALIZACIÓN DE LA BASE DE DATOS
// ============================================================

// Crea un ámbito para acceder al contexto de base de datos.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<InventoryDb>();

    if (inventoryUsesSqlite)
    {
        db.Database.EnsureCreated();
    }
    else
    {
        // El historial de migraciones vive en este esquema, así que debe existir antes.
        db.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS inventory;");
        db.Database.Migrate();
    }

    // Inserta productos de ejemplo únicamente cuando
    // la tabla de productos está vacía.
    if (!db.Products.Any())
    {
        db.Products.AddRange(

            new Product
            {
                Name = "Casio Rose Gold Diamond",
                Category = "Casio",
                Price = 32,
                Stock = 12,
                Image = "/images/rose.svg",
                Description =
                    "Reloj de estilo vintage en tono rosa dorado."
            },

            new Product
            {
                Name = "Casio Old Money Plata",
                Category = "Casio",
                Price = 32,
                Stock = 8,
                Image = "/images/silver.svg",
                Description =
                    "Diseño clásico plateado con esfera clara."
            },

            new Product
            {
                Name = "Casio Classic Dorado",
                Category = "Casio",
                Price = 32,
                Stock = 10,
                Image = "/images/gold.svg",
                Description =
                    "Reloj dorado de estilo elegante."
            },

            new Product
            {
                Name = "Casio Classic Plata Azul",
                Category = "Casio",
                Price = 32,
                Stock = 6,
                Image = "/images/blue.svg",
                Description =
                    "Reloj plateado con esfera azul."
            }
        );

        // Guarda los productos iniciales.
        db.SaveChanges();
    }
}


// ============================================================
// 4. ENDPOINT DE VERIFICACIÓN DEL SERVICIO
// ============================================================

/*
   GET /health

   Comprueba que el microservicio esté respondiendo.

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
// 5. ENDPOINTS DE ADMINISTRACIÓN DE PRODUCTOS
// ============================================================


// ------------------------------------------------------------
// 5.1. OBTENER TODOS LOS PRODUCTOS
// ------------------------------------------------------------

/*
   GET /products

   Consulta todos los productos registrados y los
   devuelve ordenados por identificador.
*/

app.MapGet("/products", async (InventoryDb db) =>
    await db.Products
        .OrderBy(p => p.Id)
        .ToListAsync()
);


// ------------------------------------------------------------
// 5.2. OBTENER UN PRODUCTO POR ID
// ------------------------------------------------------------

/*
   GET /products/{id}

   Busca un producto utilizando su identificador.

   Devuelve:
   - 200 OK si existe.
   - 404 Not Found si no existe.
*/

app.MapGet(
    "/products/{id:int}",
    async (int id, InventoryDb db) =>
        await db.Products.FindAsync(id) is { } product
            ? Results.Ok(product)
            : Results.NotFound()
);


// ------------------------------------------------------------
// 5.3. REGISTRAR UN NUEVO PRODUCTO
// ------------------------------------------------------------

/*
   POST /products

   Registra un producto nuevo. Solo administradores.

   Validaciones:
   - Nombre obligatorio.
   - Precio no negativo.
   - Stock no negativo.
*/

app.MapPost(
    "/products",
    async (ProductInput input, InventoryDb db) =>
    {
        // Validación de los datos recibidos.
        if (
            string.IsNullOrWhiteSpace(input.Name) ||
            input.Price < 0 ||
            input.Stock < 0
        )
        {
            return Results.BadRequest("Datos inválidos");
        }

        // Construye la entidad del producto.
        var product = new Product
        {
            Name = input.Name,
            Category = input.Category,
            Price = input.Price,
            Stock = input.Stock,
            Image = input.Image,
            Description = input.Description
        };

        // Agrega el producto a la base de datos.
        db.Products.Add(product);

        // Guarda los cambios.
        await db.SaveChangesAsync();

        // Devuelve la ubicación del nuevo recurso.
        return Results.Created(
            $"/products/{product.Id}",
            product
        );
    }
).RequireAuthorization(Policies.Admin);


// ------------------------------------------------------------
// 5.4. ACTUALIZAR UN PRODUCTO EXISTENTE
// ------------------------------------------------------------

/*
   PUT /products/{id}

   Permite modificar la información de un producto:
   nombre, categoría, precio, stock, imagen y descripción.
*/

app.MapPut(
    "/products/{id:int}",
    async (
        int id,
        ProductInput input,
        InventoryDb db
    ) =>
    {
        // Busca el producto.
        var product = await db.Products.FindAsync(id);

        if (product is null)
        {
            return Results.NotFound();
        }

        // Verifica los datos.
        if (
            input.Price < 0 ||
            input.Stock < 0 ||
            string.IsNullOrWhiteSpace(input.Name)
        )
        {
            return Results.BadRequest();
        }

        // Actualiza las propiedades.
        product.Name = input.Name;
        product.Category = input.Category;
        product.Price = input.Price;
        product.Stock = input.Stock;
        product.Image = input.Image;
        product.Description = input.Description;

        // Guarda los cambios.
        await db.SaveChangesAsync();

        return Results.Ok(product);
    }
).RequireAuthorization(Policies.Admin);


// ------------------------------------------------------------
// 5.5. ELIMINAR UN PRODUCTO
// ------------------------------------------------------------

/*
   DELETE /products/{id}

   Elimina un producto de la base de datos.

   Respuestas:
   - 204 No Content: eliminado correctamente.
   - 404 Not Found: producto inexistente.
*/

app.MapDelete(
    "/products/{id:int}",
    async (int id, InventoryDb db) =>
    {
        var product = await db.Products.FindAsync(id);

        if (product is null)
        {
            return Results.NotFound();
        }

        db.Products.Remove(product);

        await db.SaveChangesAsync();

        return Results.NoContent();
    }
).RequireAuthorization(Policies.Admin);


// ============================================================
// 6. MOVIMIENTOS DE INVENTARIO
// ============================================================


// ------------------------------------------------------------
// 6.1. CONSULTAR MOVIMIENTOS
// ------------------------------------------------------------

/*
   GET /movements

   Devuelve los últimos 100 movimientos registrados,
   ordenados desde el más reciente.
*/

app.MapGet("/movements", async (InventoryDb db) =>
    await db.Movements
        .OrderByDescending(m => m.Id)
        .Take(100)
        .ToListAsync()
).RequireAuthorization(Policies.Admin);

// Reservas creadas por Kafka al recibir OrderPlaced.
// reserved: todavía apartada. sold: el pago la convirtió en venta.
// released: el pedido se canceló o el pago se rechazó.
app.MapGet("/reservations", async (InventoryDb db) =>
    await db.Reservations
        .OrderByDescending(reservation => reservation.Id)
        .Take(30)
        .ToListAsync()
).RequireAuthorization(Policies.Admin);


// ------------------------------------------------------------
// 6.2. AJUSTAR EXISTENCIAS
// ------------------------------------------------------------

/*
   POST /products/{id}/adjust

   Permite aumentar o disminuir las existencias.

   Ejemplos:
   Change = 5  -> aumenta 5 unidades.
   Change = -2 -> disminuye 2 unidades.

   Cada ajuste genera un movimiento de inventario.
*/

app.MapPost(
    "/products/{id:int}/adjust",
    async (
        int id,
        AdjustRequest request,
        InventoryDb db
    ) =>
    {
        // Busca el producto.
        var product = await db.Products.FindAsync(id);

        if (product is null)
        {
            return Results.NotFound();
        }

        // Impide que el stock total sea negativo.
        if (product.Stock + request.Change < 0)
        {
            return Results.BadRequest(
                "Stock insuficiente"
            );
        }

        // Actualiza las existencias.
        product.Stock += request.Change;

        // Registra el movimiento realizado.
        db.Movements.Add(new StockMovement
        {
            ProductId = id,
            Change = request.Change,
            Reason = request.Reason ?? "Ajuste manual"
        });

        await db.SaveChangesAsync();

        return Results.Ok(product);
    }
).RequireAuthorization(Policies.Admin);


// ============================================================
// 7. INICIALIZACIÓN DEL SERVIDOR
// ============================================================

// Inicia la aplicación y habilita los endpoints.
app.Run();


// ============================================================
// 8. MODELOS DE ENTRADA (DTO)
// ============================================================

/*
   ProductInput

   Define los datos que recibe la API cuando se
   registra o actualiza un producto.
*/

record ProductInput(
    string Name,
    string Category,
    decimal Price,
    int Stock,
    string Image,
    string Description
);


/*
   AdjustRequest

   Define los datos necesarios para modificar
   las existencias de un producto.
*/

record AdjustRequest(
    int Change,
    string? Reason
);


// ============================================================
// 9. ENTIDADES DE LA BASE DE DATOS
// ============================================================


// ------------------------------------------------------------
// 9.1. ENTIDAD PRODUCTO
// ------------------------------------------------------------

/// <summary>
/// Representa un producto disponible en la tienda.
/// </summary>
public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string Category { get; set; } = "";

    public decimal Price { get; set; }

    // Cantidad total registrada en inventario.
    public int Stock { get; set; }

    // Cantidad reservada por pedidos pendientes.
    public int Reserved { get; set; }

    public string Image { get; set; } = "";

    public string Description { get; set; } = "";
}


// ------------------------------------------------------------
// 9.2. ENTIDAD MOVIMIENTO DE INVENTARIO
// ------------------------------------------------------------

/// <summary>
/// Registra las entradas y salidas de productos.
/// </summary>
public class StockMovement
{
    public int Id { get; set; }

    public int ProductId { get; set; }

    // Positivo: entrada.
    // Negativo: salida.
    public int Change { get; set; }

    public string Reason { get; set; } = "";

    // Fecha y hora del movimiento en UTC.
    public DateTime At { get; set; } = DateTime.UtcNow;
}


// ------------------------------------------------------------
// 9.3. ENTIDAD RESERVA DE INVENTARIO
// ------------------------------------------------------------

/// <summary>
/// Representa productos reservados para un pedido.
/// </summary>
public class Reservation
{
    public int Id { get; set; }

    public Guid OrderId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    // Estados esperados:
    // reserved: reservado
    // sold: vendido
    // released: liberado
    public string Status { get; set; } = "reserved";
}


// ------------------------------------------------------------
// 9.4. ENTIDAD EVENTO PROCESADO
// ------------------------------------------------------------

/// <summary>
/// Almacena identificadores de eventos ya procesados
/// para evitar procesar nuevamente el mismo evento.
/// </summary>
public class ProcessedInventoryEvent
{
    public int Id { get; set; }

    public Guid EventId { get; set; }
}


// ============================================================
// 10. CONTEXTO DE BASE DE DATOS
// ============================================================

/// <summary>
/// Administra las entidades de inventario mediante
/// Entity Framework Core.
/// </summary>
public class InventoryDb(
    DbContextOptions<InventoryDb> options
) : DbContext(options)
{
    // Tabla de productos.
    public DbSet<Product> Products =>
        Set<Product>();

    // Tabla de reservas.
    public DbSet<Reservation> Reservations =>
        Set<Reservation>();

    // Tabla de movimientos.
    public DbSet<StockMovement> Movements =>
        Set<StockMovement>();

    // Tabla de eventos procesados.
    public DbSet<ProcessedInventoryEvent> Processed =>
        Set<ProcessedInventoryEvent>();


    /// <summary>
    /// Configura restricciones e índices únicos.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        if (!Database.IsSqlite())
        {
            builder.HasDefaultSchema("inventory");
        }

        builder.Entity<Product>()
            .Property(p => p.Price)
            .HasPrecision(18, 2);

        // Impide duplicar identificadores de eventos.
        builder.Entity<ProcessedInventoryEvent>()
            .HasIndex(x => x.EventId)
            .IsUnique();

        // Impide duplicar la combinación
        // pedido-producto en las reservas.
        builder.Entity<Reservation>()
            .HasIndex(x => new
            {
                x.OrderId,
                x.ProductId
            })
            .IsUnique();
    }
}


// ============================================================
// 11. SERVICIO DE PROCESAMIENTO DE EVENTOS KAFKA
// ============================================================

/// <summary>
/// Escucha eventos provenientes de otros microservicios
/// y actualiza el inventario según el estado del pedido.
/// </summary>
public class InventoryWorker(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<InventoryWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken
    )
    {
        // Espera inicial antes de conectarse a Kafka.
        await Task.Delay(3000, stoppingToken);

        // Mantiene activo el procesamiento de eventos.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Crea un consumidor de Kafka.
                using var consumer = Events.Consumer(
                    config,
                    "inventory-service"
                );

                while (!stoppingToken.IsCancellationRequested)
                {
                    // Espera el siguiente evento.
                    var result = consumer.Consume(stoppingToken);

                    // Convierte el mensaje recibido.
                    var ev = Events.Parse<EventMessage>(
                        result.Message.Value
                    );

                    // Crea un ámbito de servicios.
                    using var scope = scopes.CreateScope();

                    var db = scope.ServiceProvider
                        .GetRequiredService<InventoryDb>();


                    // ----------------------------------------
                    // 11.1. VERIFICACIÓN DE EVENTOS DUPLICADOS
                    // ----------------------------------------

                    // Comprueba si el evento ya fue procesado.
                    if (await db.Processed.AnyAsync(
                        x => x.EventId == ev.Id,
                        stoppingToken
                    ))
                    {
                        consumer.Commit(result);
                        continue;
                    }


                    // Variables para la respuesta del inventario.
                    string? response = null;
                    object? payload = null;


                    // ----------------------------------------
                    // 11.2. EVENTO: NUEVO PEDIDO
                    // ----------------------------------------

                    if (ev.Type == "OrderPlaced")
                    {
                        // Obtiene los datos del pedido.
                        var order = Events.Parse<OrderPlaced>(
                            ev.Data
                        );

                        // Verifica que el pedido tenga productos.
                        var valid = order.Items.Count > 0;

                        string reason = "Stock insuficiente";


                        // Verifica las existencias disponibles.
                        foreach (var line in order.Items)
                        {
                            var product = await db.Products.FindAsync(
                                new object[] { line.ProductId },
                                stoppingToken
                            );

                            if (
                                product is null ||
                                line.Quantity <= 0 ||
                                product.Stock - product.Reserved
                                    < line.Quantity
                            )
                            {
                                valid = false;
                                break;
                            }
                        }


                        // ------------------------------------
                        // 11.3. RESERVAR PRODUCTOS
                        // ------------------------------------

                        if (valid)
                        {
                            foreach (var line in order.Items)
                            {
                                var product = await db.Products.FindAsync(
                                    new object[] { line.ProductId },
                                    stoppingToken
                                );

                                // Incrementa las unidades reservadas.
                                product!.Reserved += line.Quantity;

                                // Registra la reserva del pedido.
                                db.Reservations.Add(new Reservation
                                {
                                    OrderId = order.OrderId,
                                    ProductId = line.ProductId,
                                    Quantity = line.Quantity
                                });
                            }

                            // Prepara el evento de reserva exitosa.
                            response = "StockReserved";

                            payload = new StockReserved(
                                order.OrderId
                            );
                        }
                        else
                        {
                            // Prepara el evento de rechazo.
                            response = "StockRejected";

                            payload = new StockRejected(
                                order.OrderId,
                                reason
                            );
                        }
                    }


                    // ----------------------------------------
                    // 11.4. EVENTOS DE PAGO Y CANCELACIÓN
                    // ----------------------------------------

                    if (
                        ev.Type is
                            "PaymentConfirmed" or
                            "PaymentFailed" or
                            "OrderCancelled"
                    )
                    {
                        // Obtiene el identificador del pedido.
                        Guid orderId =
                            ev.Type == "PaymentConfirmed"
                                ? Events.Parse<PaymentConfirmed>(
                                    ev.Data
                                ).OrderId

                            : ev.Type == "PaymentFailed"
                                ? Events.Parse<PaymentFailed>(
                                    ev.Data
                                ).OrderId

                            : Events.Parse<OrderCancelled>(
                                ev.Data
                            ).OrderId;


                        // Busca las reservas activas del pedido.
                        var reservations = await db.Reservations
                            .Where(x =>
                                x.OrderId == orderId &&
                                x.Status == "reserved"
                            )
                            .ToListAsync(stoppingToken);


                        // ------------------------------------
                        // 11.5. PROCESAR CADA RESERVA
                        // ------------------------------------

                        foreach (var reservation in reservations)
                        {
                            var product = await db.Products.FindAsync(
                                new object[] {
                                    reservation.ProductId
                                },
                                stoppingToken
                            );

                            if (product is null)
                            {
                                continue;
                            }

                            // Libera las unidades reservadas.
                            product.Reserved -= reservation.Quantity;


                            // --------------------------------
                            // PAGO CONFIRMADO
                            // --------------------------------

                            if (ev.Type == "PaymentConfirmed")
                            {
                                // Descuenta el producto del stock.
                                product.Stock -= reservation.Quantity;

                                // Registra la salida por venta.
                                db.Movements.Add(new StockMovement
                                {
                                    ProductId = product.Id,
                                    Change = -reservation.Quantity,
                                    Reason = $"Venta {orderId}"
                                });
                            }


                            // --------------------------------
                            // ACTUALIZAR ESTADO DE LA RESERVA
                            // --------------------------------

                            reservation.Status =
                                ev.Type == "PaymentConfirmed"
                                    ? "sold"
                                    : "released";
                        }
                    }


                    // ----------------------------------------
                    // 11.5B. EDICIÓN O ELIMINACIÓN DEL PEDIDO
                    // ----------------------------------------

                    if (ev.Type is "OrderRevised" or "OrderRemoved")
                    {
                        if (ev.Type == "OrderRevised")
                        {
                            var revised = Events.Parse<OrderRevised>(ev.Data);
                            await AdjustOrderStock(
                                db,
                                revised.OrderId,
                                revised.PreviousStatus,
                                revised.PreviousItems,
                                revised.Items,
                                logger,
                                stoppingToken
                            );
                        }
                        else
                        {
                            var removed = Events.Parse<OrderRemoved>(ev.Data);
                            await AdjustOrderStock(
                                db,
                                removed.OrderId,
                                removed.PreviousStatus,
                                removed.Items,
                                new List<OrderLine>(),
                                logger,
                                stoppingToken
                            );
                        }
                    }


                    // ----------------------------------------
                    // 11.6. REGISTRAR EVENTO PROCESADO
                    // ----------------------------------------

                    db.Processed.Add(
                        new ProcessedInventoryEvent
                        {
                            EventId = ev.Id
                        }
                    );

                    // Guarda los cambios en Supabase.
                    await db.SaveChangesAsync(stoppingToken);


                    // ----------------------------------------
                    // 11.7. PUBLICAR RESPUESTA EN KAFKA
                    // ----------------------------------------

                    if (response != null)
                    {
                        await Events.Publish(
                            config,
                            response,
                            payload!,
                            stoppingToken
                        );
                    }


                    // ----------------------------------------
                    // 11.8. CONFIRMAR PROCESAMIENTO
                    // ----------------------------------------

                    consumer.Commit(result);
                }
            }

            // Finaliza cuando se cancela el servicio.
            catch (OperationCanceledException)
            {
                break;
            }

            // Registra errores y reintenta la conexión.
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Inventory Kafka worker retrying"
                );

                await Task.Delay(
                    2500,
                    stoppingToken
                );
            }
        }
    }

    /// <summary>
    /// Aplica la diferencia de cantidades de un pedido.
    /// Si estaba reservado, mueve Reserved. Si ya estaba pagado, mueve Stock.
    /// No lanza errores: un fallo aquí detendría el consumidor de Kafka.
    /// </summary>
    static async Task AdjustOrderStock(
        InventoryDb db,
        Guid orderId,
        string previousStatus,
        List<OrderLine> previousItems,
        List<OrderLine> nextItems,
        ILogger logger,
        CancellationToken ct
    )
    {
        var holdsReservation = previousStatus is
            "pending-stock" or "awaiting-payment";
        var sold = previousStatus == "paid";

        if (!holdsReservation && !sold)
        {
            return;
        }

        var before = previousItems
            .GroupBy(item => item.ProductId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.Quantity)
            );
        var after = nextItems
            .GroupBy(item => item.ProductId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.Quantity)
            );

        foreach (var productId in before.Keys.Union(after.Keys))
        {
            var oldQuantity = before.GetValueOrDefault(productId);
            var newQuantity = after.GetValueOrDefault(productId);
            var delta = newQuantity - oldQuantity;

            if (delta == 0)
            {
                continue;
            }

            var product = await db.Products.FindAsync(
                new object[] { productId },
                ct
            );

            if (product is null)
            {
                continue;
            }

            if (holdsReservation)
            {
                var reservation = await db.Reservations
                    .FirstOrDefaultAsync(
                        row =>
                            row.OrderId == orderId &&
                            row.ProductId == productId &&
                            row.Status == "reserved",
                        ct
                    );

                if (reservation is null && oldQuantity > 0)
                {
                    // La reserva original no está activa. No se toca el contador.
                    continue;
                }

                if (delta > 0 && product.Stock - product.Reserved < delta)
                {
                    logger.LogWarning(
                        "Kafka no amplió la reserva del pedido {OrderId}: sin stock de {Product}",
                        orderId,
                        product.Name
                    );
                    continue;
                }

                product.Reserved = Math.Max(0, product.Reserved + delta);

                if (newQuantity <= 0)
                {
                    if (reservation is not null)
                    {
                        reservation.Status = "released";
                    }
                }
                else if (reservation is null)
                {
                    db.Reservations.Add(new Reservation
                    {
                        OrderId = orderId,
                        ProductId = productId,
                        Quantity = newQuantity,
                        Status = "reserved"
                    });
                }
                else
                {
                    reservation.Quantity = newQuantity;
                }
            }
            else
            {
                var reservation = await db.Reservations
                    .Where(row =>
                        row.OrderId == orderId &&
                        row.ProductId == productId &&
                        row.Status == "sold"
                    )
                    .OrderByDescending(row => row.Id)
                    .FirstOrDefaultAsync(ct);

                // Una segunda eliminación no vuelve a sumar stock.
                if (reservation is null && newQuantity == 0)
                {
                    continue;
                }

                if (delta > 0 && product.Stock - product.Reserved < delta)
                {
                    logger.LogWarning(
                        "Kafka no descontó más stock del pedido {OrderId}: sin existencias de {Product}",
                        orderId,
                        product.Name
                    );
                    continue;
                }

                product.Stock = Math.Max(0, product.Stock - delta);

                db.Movements.Add(new StockMovement
                {
                    ProductId = productId,
                    Change = -delta,
                    Reason = delta > 0
                        ? $"Ajuste pedido {orderId}"
                        : $"Devolución pedido {orderId}"
                });

                if (reservation is null)
                {
                    continue;
                }

                if (newQuantity <= 0)
                {
                    reservation.Status = "released";
                }
                else
                {
                    reservation.Quantity = newQuantity;
                }
            }
        }
    }
}
