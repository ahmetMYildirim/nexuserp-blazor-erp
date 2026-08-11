using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using NexusErp.Api.Endpoints;
using NexusErp.Application;
using NexusErp.Infrastructure;
using NexusErp.Infrastructure.Identity;
using Scalar.AspNetCore;

var trCulture = new CultureInfo("tr-TR");
CultureInfo.DefaultThreadCurrentCulture = trCulture;
CultureInfo.DefaultThreadCurrentUICulture = trCulture;

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// ⚠️ MİMARİNİN KARŞILIĞI BURADA: Application katmanına HİÇ DOKUNMADAN ikinci bir
// arayüz ekliyoruz. Aynı servisler (InvoiceService, PaymentService, ...) hem Blazor'dan
// hem REST'ten çağrılıyor — iş kuralları tek yerde (ADR-001, ADR-006).
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddApiIdentityContext();   // tenant/kullanıcı JWT claim'lerinden

var jwt = builder.Configuration.GetSection("Jwt");
var signingKey = new SymmetricSecurityKey(
    Encoding.UTF8.GetBytes(jwt["Key"] ?? throw new InvalidOperationException("Jwt:Key eksik.")));

// Web tarafı cookie kullanıyor, API JWT. İkisi de aynı Identity kullanıcılarını okuyor.
//
// ⚠️ ÜÇ şemayı da AÇIKÇA JWT'ye çevirmek zorundayız. AddInfrastructure içindeki
// AddIdentity, DefaultAuthenticateScheme ve DefaultChallengeScheme'i Identity cookie'sine
// ayarlıyor; sadece AddAuthentication(JwtBearerDefaults...) yazmak YETMEZ.
// Belirtisi: jetonsuz istek 401 yerine 302 döner (API'de giriş sayfasına yönlendirme!).
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(1),
            RoleClaimType = ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// Basit hız sınırlama — kimlik doğrulanmış çağrı başına 100 istek/dakika
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("api", o =>
    {
        o.PermitLimit = 100;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueLimit = 0;
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference(opt => opt
    .WithTitle("NexusERP API")
    .WithTheme(ScalarTheme.BluePlanet));

app.MapAuthEndpoints(signingKey, jwt["Issuer"]!, jwt["Audience"]!);
app.MapPartyEndpoints();
app.MapInvoiceEndpoints();
app.MapSubscriptionEndpoints();

app.MapGet("/", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();

app.Run();
