
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

// Usuario autenticado (claims del token JWT).
using System.Security.Claims;

// Expresiones regulares para validar datos del cliente.
using System.Text.RegularExpressions;

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

// SQLite conserva los pedidos de la demostración local.
// Postgres se usa cuando la cadena es de Supabase.
var shopDb = builder.Configuration.GetConnectionString("Db");
if (string.IsNullOrWhiteSpace(shopDb))
{
    throw new InvalidOperationException(
        "Falta ConnectionStrings:Db."
    );
}

var ordersUseSqlite = ShopDatabase.IsSqlite(shopDb);

builder.Services.AddDbContext<OrdersDb>(options =>
{
    if (ordersUseSqlite)
    {
        options.UseSqlite(shopDb);
    }
    else
    {
        options.UseNpgsql(shopDb, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders")
        );
    }
});


// ------------------------------------------------------------
// 2.2. REGISTRO DE SERVICIOS
// ------------------------------------------------------------

// Registra el servicio que escucha eventos Kafka.
builder.Services.AddHostedService<OrderWorker>();

// Habilita HttpClient para comunicarse con el
// microservicio de inventario.
builder.Services.AddHttpClient();

// Valida el token emitido por AuthApi.
// Clientes: crean y consultan sus pedidos. Admin: todos los pedidos.
builder.Services.AddShopAuth(builder.Configuration);


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
app.UseAuthentication();
app.UseAuthorization();


// ============================================================
// 3. INICIALIZACIÓN DE LA BASE DE DATOS
// ============================================================

// Crea un ámbito de servicios para obtener OrdersDb.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<OrdersDb>();

    if (ordersUseSqlite)
    {
        db.Database.EnsureCreated();

        // EnsureCreated no modifica una base ya creada:
        // agrega las columnas de usuario y pago si faltan.
        var columns = db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Orders')")
            .ToList();

        if (!columns.Contains("UserId"))
            db.Database.ExecuteSqlRaw("ALTER TABLE \"Orders\" ADD COLUMN \"UserId\" INTEGER NULL;");
        if (!columns.Contains("Email"))
            db.Database.ExecuteSqlRaw("ALTER TABLE \"Orders\" ADD COLUMN \"Email\" TEXT NOT NULL DEFAULT '';");
        if (!columns.Contains("PaymentDetail"))
            db.Database.ExecuteSqlRaw("ALTER TABLE \"Orders\" ADD COLUMN \"PaymentDetail\" TEXT NOT NULL DEFAULT '';");
    }
    else
    {
        // El historial de migraciones vive en este esquema, así que debe existir antes.
        db.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS orders;");
        db.Database.Migrate();
    }
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
   Solo administradores.
*/

app.MapGet("/orders", async (OrdersDb db) =>
    await db.Orders
        .Include(order => order.Items)
        .OrderByDescending(order => order.CreatedAt)
        .ToListAsync()
).RequireAuthorization(Policies.Admin);


// ------------------------------------------------------------
// 5.1B. PEDIDOS DEL USUARIO CON SESIÓN
// ------------------------------------------------------------

/*
   GET /orders/mine

   Devuelve solo los pedidos creados por el usuario del token.
*/

app.MapGet("/orders/mine", async (ClaimsPrincipal user, OrdersDb db) =>
{
    var userId = user.UserId();

    return await db.Orders
        .Include(order => order.Items)
        .Where(order => order.UserId == userId)
        .OrderByDescending(order => order.CreatedAt)
        .ToListAsync();
}).RequireAuthorization(Policies.Cliente);


// ------------------------------------------------------------
// 5.2. OBTENER PEDIDO POR IDENTIFICADOR
// ------------------------------------------------------------

/*
   GET /orders/{id}

   Busca un pedido mediante su identificador GUID.

   Respuestas:
   - 200 OK: pedido encontrado.
   - 403 Forbidden: el pedido es de otro usuario.
   - 404 Not Found: pedido inexistente.
*/

app.MapGet(
    "/orders/{id:guid}",
    async (Guid id, ClaimsPrincipal user, OrdersDb db) =>
    {
        var order = await db.Orders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id);

        if (order is null)
        {
            return Results.NotFound();
        }

        return CanSee(user, order)
            ? Results.Ok(order)
            : Results.Forbid();
    }
).RequireAuthorization(Policies.Cliente);


// ============================================================
// 6. REGISTRO DE NUEVOS PEDIDOS
// ============================================================

/*
   POST /orders

   Registra un nuevo pedido en la tienda.

   Requiere sesión: el pedido queda asociado al usuario del token.

   PROCESO:

   1. Validar datos del cliente y del método de pago.
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
        ClaimsPrincipal user,
        OrdersDb db,
        IHttpClientFactory http,
        IConfiguration config
    ) =>
    {

        // ----------------------------------------------------
        // 6.1. VALIDACIÓN DE DATOS DEL CLIENTE
        // ----------------------------------------------------

        if (
            request.Items is null ||
            request.Items.Count == 0 ||
            request.Items.Count > 30
        )
        {
            return Results.BadRequest(
                "Datos de pedido incompletos"
            );
        }

        if (OrderRules.CustomerProblem(request) is { } customerProblem)
        {
            return Results.BadRequest(customerProblem);
        }

        if (!OrderRules.PaymentMethods.Contains(request.PaymentMethod))
        {
            return Results.BadRequest(
                "Elige tarjeta, transferencia o contra entrega."
            );
        }

        // Con tarjeta solo se guarda la marca y los últimos 4 dígitos.
        var paymentDetail = "";
        if (request.PaymentMethod == "tarjeta")
        {
            if (OrderRules.CardProblem(request.Card, out paymentDetail) is { } cardProblem)
            {
                return Results.BadRequest(cardProblem);
            }
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

            // Usuario dueño del pedido.
            UserId = user.UserId(),
            Email = user.Email(),

            // Información del cliente.
            Customer = request.Customer.Trim(),
            Phone = OrderRules.FormatPhone(request.Phone),
            Address = request.Address.Trim(),

            // Método de pago ya validado.
            PaymentMethod = request.PaymentMethod,
            PaymentDetail = paymentDetail,

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
).RequireAuthorization(Policies.Cliente);


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
   Solo el dueño del pedido o un administrador.
*/

app.MapPost(
    "/orders/{id:guid}/cancel",
    async (
        Guid id,
        ClaimsPrincipal user,
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

        if (!CanSee(user, order))
        {
            return Results.Forbid();
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
).RequireAuthorization(Policies.Cliente);


// ============================================================
// 7B. EDICIÓN Y ELIMINACIÓN DE PEDIDOS
// ============================================================

/*
   PUT /orders/{id}

   Cambia los datos del cliente y las cantidades. Solo administradores.
   Si las cantidades cambian, publica OrderRevised para que
   inventario ajuste la reserva o el stock por Kafka.
*/

app.MapPut(
    "/orders/{id:guid}",
    async (
        Guid id,
        CreateOrder request,
        OrdersDb db,
        IHttpClientFactory http,
        IConfiguration config
    ) =>
    {
        var order = await db.Orders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id);

        if (order is null)
        {
            return Results.NotFound();
        }

        if (
            order.Status is not (
                "awaiting-payment" or "paid"
            )
        )
        {
            return Results.BadRequest(
                "Solo se editan pedidos reservados o ya pagados. " +
                "Si Kafka todavía está verificando el stock, espera un momento."
            );
        }

        if (
            request.Items is null ||
            request.Items.Count == 0 ||
            request.Items.Count > 30
        )
        {
            return Results.BadRequest(
                "Datos de pedido incompletos"
            );
        }

        if (OrderRules.CustomerProblem(request) is { } customerProblem)
        {
            return Results.BadRequest(customerProblem);
        }

        if (!OrderRules.PaymentMethods.Contains(request.PaymentMethod))
        {
            return Results.BadRequest(
                "Elige tarjeta, transferencia o contra entrega."
            );
        }

        // Los datos de la tarjeta no se guardan, así que solo
        // el cliente puede elegir tarjeta desde la cesta.
        if (
            request.PaymentMethod == "tarjeta" &&
            order.PaymentMethod != "tarjeta"
        )
        {
            return Results.BadRequest(
                "El pago con tarjeta solo puede elegirlo el cliente desde la cesta."
            );
        }

        var client = http.CreateClient();
        var baseUrl = config["INVENTORY_URL"]
            ?? "http://localhost:5101";
        var products = await client.GetFromJsonAsync<
            List<CatalogProduct>
        >(baseUrl + "/products") ?? new();

        var lines = new List<OrderItem>();
        var groupedItems = request.Items
            .Where(item => item.Quantity > 0)
            .GroupBy(item => item.ProductId)
            .Select(group => new OrderInput(
                group.Key,
                group.Sum(item => item.Quantity)
            ))
            .ToList();

        if (groupedItems.Count == 0)
        {
            return Results.BadRequest(
                "El pedido necesita al menos un reloj"
            );
        }

        foreach (var input in groupedItems)
        {
            var product = products.FirstOrDefault(
                item => item.Id == input.ProductId
            );

            if (
                product is null ||
                input.Quantity > 100
            )
            {
                return Results.BadRequest(
                    "Producto o cantidad inválida"
                );
            }

            var alreadyHeld = order.Status == "awaiting-payment"
                ? order.Items
                    .Where(item => item.ProductId == product.Id)
                    .Sum(item => item.Quantity)
                : 0;

            var available = product.Stock - product.Reserved + alreadyHeld;

            if (input.Quantity > available)
            {
                return Results.BadRequest(
                    $"No hay existencias suficientes de {product.Name}"
                );
            }

            lines.Add(new OrderItem
            {
                ProductId = product.Id,
                Name = product.Name,
                Quantity = input.Quantity,
                UnitPrice = product.Price
            });
        }

        var previousItems = order.Items
            .Select(item => new OrderLine(
                item.ProductId,
                item.Quantity,
                item.UnitPrice
            ))
            .ToList();
        var previousStatus = order.Status;

        order.Customer = request.Customer.Trim();
        order.Phone = OrderRules.FormatPhone(request.Phone);
        order.Address = request.Address.Trim();

        if (request.PaymentMethod != "tarjeta")
        {
            order.PaymentDetail = "";
        }
        order.PaymentMethod = request.PaymentMethod;

        db.RemoveRange(order.Items.ToList());
        order.Items.Clear();

        foreach (var line in lines)
        {
            order.Items.Add(line);
        }

        order.Total = lines.Sum(
            item => item.UnitPrice * item.Quantity
        );

        await db.SaveChangesAsync();

        var nextItems = lines
            .Select(item => new OrderLine(
                item.ProductId,
                item.Quantity,
                item.UnitPrice
            ))
            .ToList();

        if (!SameQuantities(previousItems, nextItems))
        {
            await Events.Publish(
                config,
                "OrderRevised",
                new OrderRevised(
                    order.Id,
                    previousStatus,
                    previousItems,
                    nextItems
                )
            );
        }

        return Results.Ok(order);
    }
).RequireAuthorization(Policies.Admin);

/*
   DELETE /orders/{id}

   Elimina el pedido y publica OrderRemoved. Solo administradores.
   Inventario libera la reserva o devuelve las unidades vendidas.
*/

app.MapDelete(
    "/orders/{id:guid}",
    async (
        Guid id,
        OrdersDb db,
        IConfiguration config
    ) =>
    {
        var order = await db.Orders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id);

        if (order is null)
        {
            return Results.NotFound();
        }

        var previousStatus = order.Status;
        var items = order.Items
            .Select(item => new OrderLine(
                item.ProductId,
                item.Quantity,
                item.UnitPrice
            ))
            .ToList();

        if (
            previousStatus is
                "pending-stock" or
                "awaiting-payment" or
                "paid"
        )
        {
            await Events.Publish(
                config,
                "OrderRemoved",
                new OrderRemoved(
                    order.Id,
                    previousStatus,
                    items
                )
            );
        }

        db.Orders.Remove(order);
        await db.SaveChangesAsync();

        return Results.NoContent();
    }
).RequireAuthorization(Policies.Admin);

// El dueño ve y cancela su pedido; el administrador, cualquiera.
// Los pedidos anteriores al login no tienen dueño: solo los ve el admin.
static bool CanSee(ClaimsPrincipal user, ShopOrder order) =>
    user.IsAdmin() ||
    (order.UserId is not null && order.UserId == user.UserId());

static bool SameQuantities(
    List<OrderLine> previous,
    List<OrderLine> next
)
{
    var before = previous
        .GroupBy(item => item.ProductId)
        .ToDictionary(
            group => group.Key,
            group => group.Sum(item => item.Quantity)
        );
    var after = next
        .GroupBy(item => item.ProductId)
        .ToDictionary(
            group => group.Key,
            group => group.Sum(item => item.Quantity)
        );

    return before.Count == after.Count &&
        before.All(pair =>
            after.TryGetValue(pair.Key, out var quantity) &&
            quantity == pair.Value
        );
}


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
    List<OrderInput> Items,
    CardInput? Card = null
);


// ------------------------------------------------------------
// 9.1B. DATOS DE TARJETA (SIMULADA)
// ------------------------------------------------------------

/// <summary>
/// Solo se usa para validar. Nunca se guarda el número ni el CVV.
/// </summary>
record CardInput(
    string? Holder,
    string? Number,
    string? Expiry,
    string? Cvv
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


// ------------------------------------------------------------
// 9.4. REGLAS DE VALIDACIÓN DEL PEDIDO
// ------------------------------------------------------------

/// <summary>
/// Validaciones compartidas por crear y editar pedidos.
/// El frontend aplica las mismas reglas, pero el servidor decide.
/// </summary>
static partial class OrderRules
{
    public static readonly string[] PaymentMethods =
        ["tarjeta", "transferencia", "contra-entrega"];

    [GeneratedRegex(@"^\p{L}[\p{L} .'-]{2,99}$")]
    private static partial Regex NamePattern();

    // Teléfono de El Salvador: 8 dígitos que empiezan por 2, 6 o 7.
    [GeneratedRegex(@"^[267]\d{7}$")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"^(0[1-9]|1[0-2])/(\d{2})$")]
    private static partial Regex ExpiryPattern();

    public static string? CustomerProblem(CreateOrder request)
    {
        if (!NamePattern().IsMatch(request.Customer?.Trim() ?? ""))
        {
            return "El nombre debe tener entre 3 y 100 letras.";
        }

        if (!PhonePattern().IsMatch(PhoneDigits(request.Phone)))
        {
            return "El teléfono debe tener 8 dígitos y empezar por 2, 6 o 7.";
        }

        var address = request.Address?.Trim() ?? "";
        if (address.Length is < 10 or > 300)
        {
            return "La dirección debe tener entre 10 y 300 caracteres.";
        }

        return null;
    }

    // Quita espacios, guiones y el prefijo +503.
    static string PhoneDigits(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length == 11 && digits.StartsWith("503")
            ? digits[3..]
            : digits;
    }

    public static string FormatPhone(string phone)
    {
        var digits = PhoneDigits(phone);
        return $"{digits[..4]}-{digits[4..]}";
    }

    /// <summary>
    /// Valida titular, número (Luhn), vencimiento y CVV.
    /// En detail devuelve lo único que se guarda: marca y últimos 4 dígitos.
    /// </summary>
    public static string? CardProblem(CardInput? card, out string detail)
    {
        detail = "";

        if (card is null)
        {
            return "Faltan los datos de la tarjeta.";
        }

        if (!NamePattern().IsMatch(card.Holder?.Trim() ?? ""))
        {
            return "Escribe el nombre del titular como aparece en la tarjeta.";
        }

        var number = new string((card.Number ?? "")
            .Where(c => c is not (' ' or '-'))
            .ToArray());

        if (number.Length is < 13 or > 19 || !number.All(char.IsDigit) || !Luhn(number))
        {
            return "El número de tarjeta no es válido.";
        }

        var expiry = ExpiryPattern().Match(card.Expiry?.Trim() ?? "");
        if (!expiry.Success)
        {
            return "El vencimiento debe tener el formato MM/AA.";
        }

        var month = int.Parse(expiry.Groups[1].Value);
        var year = 2000 + int.Parse(expiry.Groups[2].Value);
        var lastDay = new DateTime(year, month, 1).AddMonths(1);
        if (lastDay <= DateTime.UtcNow.Date || year > DateTime.UtcNow.Year + 15)
        {
            return "La tarjeta está vencida o la fecha no es válida.";
        }

        var brand = CardBrand(number);
        var cvvLength = brand == "Amex" ? 4 : 3;
        var cvv = card.Cvv?.Trim() ?? "";
        if (cvv.Length != cvvLength || !cvv.All(char.IsDigit))
        {
            return $"El CVV debe tener {cvvLength} dígitos.";
        }

        detail = $"{brand} •••• {number[^4..]}";
        return null;
    }

    static string CardBrand(string number) =>
        number[0] == '4' ? "Visa"
        : number.StartsWith("34") || number.StartsWith("37") ? "Amex"
        : int.Parse(number[..2]) is >= 51 and <= 55 ||
          int.Parse(number[..4]) is >= 2221 and <= 2720 ? "Mastercard"
        : "Tarjeta";

    // Algoritmo de Luhn: detecta errores al teclear el número.
    static bool Luhn(string number)
    {
        var sum = 0;
        var doubleIt = false;

        for (var i = number.Length - 1; i >= 0; i--)
        {
            var digit = number[i] - '0';
            if (doubleIt)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }

            sum += digit;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }
}


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

    // Usuario de AuthApi que creó el pedido.
    // Nulo en pedidos creados antes de existir el login.
    public int? UserId { get; set; }

    public string Email { get; set; } = "";

    // Información del cliente.
    public string Customer { get; set; } = "";

    public string Phone { get; set; } = "";

    public string Address { get; set; } = "";

    // Método de pago seleccionado:
    // tarjeta, transferencia o contra-entrega.
    public string PaymentMethod { get; set; } = "";

    // Con tarjeta: marca y últimos 4 dígitos, p. ej. "Visa •••• 4242".
    public string PaymentDetail { get; set; } = "";

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
        if (!Database.IsSqlite())
        {
            builder.HasDefaultSchema("orders");
        }

        builder.Entity<ShopOrder>()
            .Property(o => o.Total)
            .HasPrecision(18, 2);

        // Acelera la consulta de "mis pedidos".
        builder.Entity<ShopOrder>()
            .HasIndex(o => o.UserId);

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
