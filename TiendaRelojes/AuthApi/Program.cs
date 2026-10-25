
/* ============================================================
   MICROSERVICIO DE USUARIOS - AUTH SERVICE

   Tecnologías:
   - ASP.NET Core
   - Entity Framework Core
   - PostgreSQL (Supabase)
   - JWT (validado por las demás APIs con Shared.ShopAuth)

   FUNCIONALIDADES:
   1. Configuración del servidor
   2. Inicialización de la base de datos y administrador inicial
   3. Registro e inicio de sesión
   4. Perfil del usuario
   5. Gestión de roles (solo administradores)
   6. Modelos de entrada (DTO)
   7. Entidades y contexto de base de datos
   8. Hash de contraseñas
============================================================ */

using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Shared;


// ============================================================
// 1. CONFIGURACIÓN DEL SERVIDOR
// ============================================================

var builder = WebApplication.CreateBuilder(args);

// SQLite para la demostración local; Postgres cuando la cadena es de Supabase.
var shopDb = builder.Configuration.GetConnectionString("Db");
if (string.IsNullOrWhiteSpace(shopDb))
{
    throw new InvalidOperationException(
        "Falta ConnectionStrings:Db."
    );
}

var authUsesSqlite = ShopDatabase.IsSqlite(shopDb);

builder.Services.AddDbContext<AuthDb>(options =>
{
    if (authUsesSqlite)
    {
        options.UseSqlite(shopDb);
    }
    else
    {
        options.UseNpgsql(shopDb, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity")
        );
    }
});

// Misma validación de token que inventario, pedidos y pagos.
builder.Services.AddShopAuth(builder.Configuration);

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
    )
);

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();


// ============================================================
// 2. INICIALIZACIÓN DE LA BASE DE DATOS
// ============================================================

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<AuthDb>();

    if (authUsesSqlite)
    {
        db.Database.EnsureCreated();
    }
    else
    {
        // El historial de migraciones vive en este esquema, así que debe existir antes.
        db.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS identity;");
        db.Database.Migrate();
    }

    // Crea el primer administrador si todavía no hay ninguno.
    // Las credenciales se cambian con ADMIN_EMAIL y ADMIN_PASSWORD.
    if (!db.Users.Any(user => user.Role == Roles.Admin))
    {
        var email = (app.Configuration["ADMIN_EMAIL"] ?? "admin@tiempoyestilo.com")
            .Trim()
            .ToLowerInvariant();
        var password = app.Configuration["ADMIN_PASSWORD"] ?? "Admin123!";

        var existing = db.Users.FirstOrDefault(user => user.Email == email);
        if (existing is null)
        {
            db.Users.Add(new AppUser
            {
                Name = "Administrador",
                Email = email,
                PasswordHash = Passwords.Hash(password),
                Role = Roles.Admin
            });
        }
        else
        {
            existing.Role = Roles.Admin;
        }

        db.SaveChanges();
    }
}


// ============================================================
// 3. REGISTRO E INICIO DE SESIÓN
// ============================================================

app.MapGet("/health", () =>
    Results.Ok(new
    {
        status = "ok"
    })
);

/*
   POST /register

   Crea una cuenta de cliente y devuelve el token.
   El rol siempre es "cliente": solo un administrador puede ascender a otro.
*/

app.MapPost(
    "/register",
    async (
        RegisterRequest request,
        AuthDb db,
        IConfiguration config
    ) =>
    {
        var name = request.Name?.Trim() ?? "";
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var password = request.Password ?? "";

        if (name.Length is < 3 or > 100)
        {
            return Results.BadRequest("El nombre debe tener entre 3 y 100 caracteres.");
        }

        if (!AuthRules.ValidEmail(email))
        {
            return Results.BadRequest("Escribe un correo electrónico válido.");
        }

        if (AuthRules.PasswordProblem(password) is { } problem)
        {
            return Results.BadRequest(problem);
        }

        if (await db.Users.AnyAsync(user => user.Email == email))
        {
            return Results.Conflict("Ya existe una cuenta con ese correo.");
        }

        var user = new AppUser
        {
            Name = name,
            Email = email,
            PasswordHash = Passwords.Hash(password),
            Role = Roles.Cliente
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return Results.Ok(Session(user, config));
    }
);

/*
   POST /login

   Comprueba correo y contraseña y devuelve el token.
   El mensaje de error no dice cuál de los dos falló.
*/

app.MapPost(
    "/login",
    async (
        LoginRequest request,
        AuthDb db,
        IConfiguration config
    ) =>
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var user = await db.Users.FirstOrDefaultAsync(item => item.Email == email);

        if (user is null || !Passwords.Verify(request.Password ?? "", user.PasswordHash))
        {
            return Results.Json(
                "Correo o contraseña incorrectos.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        return Results.Ok(Session(user, config));
    }
);


// ============================================================
// 4. PERFIL DEL USUARIO
// ============================================================

/*
   GET /me

   Devuelve el usuario dueño del token. Sirve al frontend para
   comprobar si la sesión guardada sigue siendo válida.
*/

app.MapGet(
    "/me",
    async (ClaimsPrincipal principal, AuthDb db) =>
        principal.UserId() is { } id &&
        await db.Users.FindAsync(id) is { } user
            ? Results.Ok(UserView.From(user))
            : Results.Unauthorized()
).RequireAuthorization(Policies.Cliente);


// ============================================================
// 5. GESTIÓN DE ROLES (SOLO ADMINISTRADORES)
// ============================================================

app.MapGet(
    "/users",
    async (AuthDb db) =>
        (await db.Users
            .OrderBy(user => user.Id)
            .ToListAsync())
        .Select(UserView.From)
).RequireAuthorization(Policies.Admin);

/*
   PUT /users/{id}/role

   Cambia el rol de un usuario. Impide dejar la tienda sin administradores.
   El cambio se aplica cuando el usuario vuelve a iniciar sesión.
*/

app.MapPut(
    "/users/{id:int}/role",
    async (
        int id,
        RoleRequest request,
        AuthDb db
    ) =>
    {
        if (!Roles.All.Contains(request.Role))
        {
            return Results.BadRequest("Rol no válido.");
        }

        var user = await db.Users.FindAsync(id);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (
            user.Role == Roles.Admin &&
            request.Role != Roles.Admin &&
            await db.Users.CountAsync(item => item.Role == Roles.Admin) == 1
        )
        {
            return Results.BadRequest("La tienda necesita al menos un administrador.");
        }

        user.Role = request.Role;
        await db.SaveChangesAsync();

        return Results.Ok(UserView.From(user));
    }
).RequireAuthorization(Policies.Admin);

static SessionView Session(AppUser user, IConfiguration config) =>
    new(
        ShopAuth.CreateToken(config, user.Id, user.Name, user.Email, user.Role),
        UserView.From(user)
    );

app.Run();


// ============================================================
// 6. MODELOS DE ENTRADA Y SALIDA (DTO)
// ============================================================

record RegisterRequest(string? Name, string? Email, string? Password);

record LoginRequest(string? Email, string? Password);

record RoleRequest(string Role);

// Nunca se devuelve el hash de la contraseña.
record UserView(int Id, string Name, string Email, string Role)
{
    public static UserView From(AppUser user) =>
        new(user.Id, user.Name, user.Email, user.Role);
}

record SessionView(string Token, UserView User);


// ============================================================
// 7. ENTIDADES Y CONTEXTO DE BASE DE DATOS
// ============================================================

/// <summary>
/// Cuenta de la tienda. Role es "admin" o "cliente".
/// </summary>
public class AppUser
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    // Se guarda en minúsculas y es único.
    public string Email { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public string Role { get; set; } = Roles.Cliente;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AuthDb(
    DbContextOptions<AuthDb> options
) : DbContext(options)
{
    public DbSet<AppUser> Users =>
        Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        if (!Database.IsSqlite())
        {
            builder.HasDefaultSchema("identity");
        }

        builder.Entity<AppUser>()
            .HasIndex(user => user.Email)
            .IsUnique();
    }
}


// ============================================================
// 8. VALIDACIONES Y HASH DE CONTRASEÑAS
// ============================================================

static class AuthRules
{
    public static bool ValidEmail(string email) =>
        email.Length <= 150 &&
        MailAddress.TryCreate(email, out var parsed) &&
        parsed.Address == email &&
        email.Contains('.', StringComparison.Ordinal);

    // Mínimo 8 caracteres, con al menos una letra y un número.
    public static string? PasswordProblem(string password)
    {
        if (password.Length is < 8 or > 100)
        {
            return "La contraseña debe tener entre 8 y 100 caracteres.";
        }

        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
        {
            return "La contraseña debe combinar letras y números.";
        }

        return null;
    }
}

/// <summary>
/// PBKDF2-SHA256 con sal aleatoria. Formato: pbkdf2$iteraciones$sal$hash.
/// </summary>
static class Passwords
{
    const int Iterations = 100_000;
    const int SaltSize = 16;
    const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
