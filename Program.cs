using EPApi.DataAccess;
using EPApi.Models;
using EPApi.Services;
using EPApi.Services.Billing;
using EPApi.Services.Storage;
using EPApi.Services.Archive;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
const string CorsPolicy = "VitePolicy";

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "EPApi", Version = "v1" });
    var scheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter 'Bearer {token}'"
    };
    c.AddSecurityDefinition("Bearer", scheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { { scheme, new List<string>() } });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy(name: CorsPolicy, policy =>
    {
        policy
            .WithOrigins("http://localhost:5173") // tu frontend
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials(); // solo si usas cookies/Auth; si usas Bearer, puedes omitirlo
    });
});

var jwtKey = builder.Configuration["Jwt:Key"] ?? "dev-secret-key-change-me-please-0123456789";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "EPApi";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "EPApiAudience";
var jwtExpireMinutes = int.TryParse(builder.Configuration["Jwt:ExpireMinutes"], out var exp) ? exp : 60;

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromSeconds(10),
        NameClaimType = JwtRegisteredClaimNames.Sub,
        RoleClaimType = ClaimTypes.Role    
    };
});

builder.Services.AddAuthorization((options) =>
{
    // Política para CRUD de taxonomías (disciplinas/categorías/subcategorías)
    options.AddPolicy("ManageTaxonomy", p => p.RequireRole("admin"));

    // Lectura (si quieres permitir a todos los autenticados leer, usa RequireAuthenticatedUser)
    options.AddPolicy("ReadTaxonomy", p => p.RequireAuthenticatedUser());
    options.AddPolicy("ManageTests", p => p.RequireRole("admin"));

    // Clínica (profesionales + admin)
    options.AddPolicy("ReadClinic", p => p.RequireRole("editor", "admin"));
    options.AddPolicy("ManageClinic", p => p.RequireRole("editor", "admin"));
});

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<IJwtTokenService>(sp => new JwtTokenService(jwtKey, jwtIssuer, jwtAudience, TimeSpan.FromMinutes(jwtExpireMinutes)));
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IDisciplineRepository, DisciplineRepository>();
builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
builder.Services.AddScoped<ISubcategoryRepository, SubcategoryRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ITestRepository, TestRepository>();
builder.Services.AddScoped<IAgeGroupRepository, AgeGroupRepository>();
builder.Services.AddScoped<IUserDisciplineRepository, UserDisciplineRepository>();
builder.Services.AddScoped<IPatientRepository, PatientRepository>();
builder.Services.AddScoped<IClinicianReviewRepository, ClinicianReviewRepository>();
builder.Services.AddScoped<IInterviewsRepository, InterviewsRepository>();
builder.Services.AddScoped<ITranscriptionService, WhisperTranscriptionService>();
builder.Services.AddScoped<IInterviewDraftService, InterviewDraftService>();
builder.Services.AddSingleton<IAiAssistantService, OpenAiAssistantService>();
builder.Services.AddSingleton<BillingRepository>();
builder.Services.AddSingleton<IUsageService, SqlUsageService>();
builder.Services.AddSingleton<IBillingGateway, FakeGateway>();
builder.Services.AddSingleton<BillingOrchestrator>();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<StorageArchiveOptions>(builder.Configuration.GetSection("Storage:Archive"));

builder.Services.AddScoped<IStorageService, LocalStorageService>();
builder.Services.AddScoped<IArchiveService, ArchiveService>();
builder.Services.AddHostedService<ArchiveHostedService>();



builder.Services.AddMemoryCache();

builder.Services
  .AddControllers()
  .AddJsonOptions(o =>
  {
      o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
      o.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
      o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
      o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
  });


builder.Services.AddHttpClient();
//builder.Services.AddScoped<ITranscriptionService>(sp =>
//    new CompositeTranscriptionService(
//        sp.GetRequiredService<WhisperTranscriptionService>(),
//        sp.GetRequiredService<DummyTranscriptionService>()));
builder.Services.AddScoped<ITranscriptionService, WhisperTranscriptionService>();
//builder.Services.AddScoped<DummyTranscriptionService>();

builder.Services.AddControllers(options =>
{
    options.Filters.Add<EPApi.Infrastructure.SqlExceptionFilter>();
});

var app = builder.Build();

await DatabaseInitializer.EnsureCreatedAndSeedAsync(app.Configuration.GetConnectionString("Default"));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.UseHttpsRedirection();
app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();