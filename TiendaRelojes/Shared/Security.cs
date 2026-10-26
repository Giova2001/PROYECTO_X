using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Shared;

/// <summary>
/// Roles de la tienda. Un usuario tiene uno solo.
/// </summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Cliente = "cliente";

    public static readonly string[] All = [Admin, Cliente];
}

/// <summary>
/// Nombres de las políticas que usan los endpoints con RequireAuthorization.
/// </summary>
public static class Policies
{
    // Cualquier usuario con sesión iniciada.
    public const string Cliente = "cliente";

    // Solo administradores: inventario, todos los pedidos, usuarios.
    public const string Admin = "admin";
}

/// <summary>
/// Autenticación JWT compartida por las cuatro APIs.
/// AuthApi firma el token y las demás lo validan con la misma clave,
/// sin tener que consultar a AuthApi en cada petición.
/// </summary>
public static class ShopAuth
{
    const string Issuer = "tienda-relojes";

    // Solo para la demostración local. En compose se reemplaza con JWT_KEY.
    const string DevKey = "clave-local-de-demostracion-cambiar-en-produccion-2026";

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    static SymmetricSecurityKey Key(IConfiguration config)
    {
        var key = config["JWT_KEY"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
        {
            key = DevKey;
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>
    /// Registra la validación del token y las políticas por rol.
    /// </summary>
    public static IServiceCollection AddShopAuth(
        this IServiceCollection services,
        IConfiguration config)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Conserva los nombres cortos "sub", "role" y "name".
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = Issuer,
                    ValidateAudience = true,
                    ValidAudience = Issuer,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = Key(config),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = "name",
                    RoleClaimType = "role"
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Cliente, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(Policies.Admin, policy => policy.RequireRole(Roles.Admin));

        return services;
    }

    /// <summary>
    /// Firma un token con el identificador, nombre, correo y rol del usuario.
    /// </summary>
    public static string CreateToken(
        IConfiguration config,
        int userId,
        string name,
        string email,
        string role)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            Expires = DateTime.UtcNow.Add(Lifetime),
            SigningCredentials = new SigningCredentials(Key(config), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["name"] = name,
                ["email"] = email,
                ["role"] = role
            }
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static int? UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;

    public static string Email(this ClaimsPrincipal user) =>
        user.FindFirstValue("email") ?? "";

    public static bool IsAdmin(this ClaimsPrincipal user) =>
        user.IsInRole(Roles.Admin);
}
