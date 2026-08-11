using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using NexusErp.Infrastructure.Identity;

namespace NexusErp.Api.Endpoints;

public sealed record TokenRequest(string Email, string Password);
public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, string TokenType);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(
        this WebApplication app, SecurityKey key, string issuer, string audience)
    {
        var group = app.MapGroup("/api/auth").WithTags("Kimlik");

        group.MapPost("/token", async (
            TokenRequest request,
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager) =>
        {
            var user = await userManager.FindByEmailAsync(request.Email);
            if (user is null || !user.IsActive)
                return Results.Problem("E-posta veya parola hatalı.",
                                       statusCode: StatusCodes.Status401Unauthorized);

            var check = await signInManager.CheckPasswordSignInAsync(
                user, request.Password, lockoutOnFailure: true);

            if (check.IsLockedOut)
                return Results.Problem("Hesap geçici olarak kilitlendi.",
                                       statusCode: StatusCodes.Status423Locked);

            if (!check.Succeeded)
                return Results.Problem("E-posta veya parola hatalı.",
                                       statusCode: StatusCodes.Status401Unauthorized);

            var roles = await userManager.GetRolesAsync(user);
            var expires = DateTimeOffset.UtcNow.AddHours(8);

            // ⚠️ tenant_id claim'i ŞART: API üzerinden gelen istekler de aynı
            // global query filter'a takılıyor. Claim yoksa veri sızabilir.
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
                new(ClaimTypes.Name, user.UserName!),
                new(AppClaims.TenantId, user.TenantId.ToString()),
                new(AppClaims.FullName, user.FullName)
            };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: expires.UtcDateTime,
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

            return Results.Ok(new TokenResponse(
                new JwtSecurityTokenHandler().WriteToken(token), expires, "Bearer"));
        })
        .WithSummary("JWT erişim jetonu alır")
        .WithDescription("Web arayüzüyle AYNI kullanıcı ve rolleri kullanır. " +
                         "Dönen jetonu Authorization: Bearer <token> başlığında gönderin.")
        .AllowAnonymous();
    }
}
