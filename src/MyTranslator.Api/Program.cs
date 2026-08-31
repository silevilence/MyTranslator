using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using MyTranslator.Api;
using MyTranslator.Api.Authentication;
using MyTranslator.Api.Data;
using MyTranslator.Api.FileTasks;
using MyTranslator.Api.Tokens;
using MyTranslator.Api.Translation;
using MyTranslator.Api.TaskOperations;
using MyTranslator.Api.TaskLists;
using MyTranslator.Api.Rules;
using MyTranslator.Api.AiConfiguration;
using MyTranslator.Api.Terms;
using MyTranslator.Api.Review;
using MyTranslator.Api.TranslationMemory;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var connectionString = ResolveSqliteConnectionString(
    builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=data/mytranslator.db",
    builder.Environment.ContentRootPath);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<FileTaskService>();
builder.Services.AddScoped<TaskListService>();
builder.Services.AddScoped<AiConfigurationService>();
builder.Services.AddScoped<TermService>();
builder.Services.AddScoped<TermAlignmentService>();
builder.Services.AddScoped<TranslationMemoryService>();
builder.Services.AddSingleton<TaskOperationLock>();
builder.Services.Configure<TranslationOptions>(builder.Configuration.GetSection("Translation"));
builder.Services.Configure<ReviewOptions>(builder.Configuration.GetSection("Review"));
builder.Services.AddSingleton<TranslationRunQueue>();
builder.Services.AddScoped<TranslationRunService>();
builder.Services.AddScoped<TranslationRunProcessor>();
builder.Services.AddSingleton<ReviewRunQueue>();
builder.Services.AddScoped<ReviewRunService>();
builder.Services.AddScoped<ReviewRunProcessor>();
builder.Services.AddSingleton<IAiChatClientFactory, AiChatClientFactory>();
builder.Services.AddSingleton<ITranslationRule, PlaceholderIntegrityRule>();
builder.Services.AddHttpClient("AiChatClient");
builder.Services.AddSingleton<ExtractionPreviewStore>();
builder.Services.AddHttpClient<UrlImportClient>(client =>
    client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(PublicAddressHttpHandler.Create);
builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<TranslationRunWorker>();
builder.Services.AddHostedService<ReviewRunWorker>();
builder.Services
    .AddAuthentication(TokenAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(
        TokenAuthenticationDefaults.Scheme,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.AllowAnyOrigin();
        }
        else
        {
            var allowedOrigins = builder.Configuration
                .GetSection("Cors:AllowedOrigins")
                .GetChildren()
                .Select(origin => origin.Value)
                .Where(origin => !string.IsNullOrWhiteSpace(origin))
                .Cast<string>()
                .ToArray();
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins);
            }
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "MyTranslator API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "MyTranslator API Token, for example: Bearer sk-...",
        In = ParameterLocation.Header,
        Name = "Authorization",
        Scheme = "bearer",
        Type = SecuritySchemeType.Http
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();
app.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;
    var title = response.StatusCode switch
    {
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        _ => "Request failed"
    };

    var problemDetailsService = statusCodeContext.HttpContext.RequestServices
        .GetRequiredService<IProblemDetailsService>();
    await problemDetailsService.WriteAsync(new ProblemDetailsContext
    {
        HttpContext = statusCodeContext.HttpContext,
        ProblemDetails = new ProblemDetails
        {
            Status = response.StatusCode,
            Title = title,
            Type = $"https://httpstatuses.com/{response.StatusCode}",
            Instance = statusCodeContext.HttpContext.Request.Path
        }
    });
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("GetHealth")
    .WithTags("System");
app.MapTokenEndpoints();
app.MapAiConfigurationEndpoints();
app.MapTaskListEndpoints();
app.MapFileTaskEndpoints();
app.MapTranslationEndpoints();
app.MapReviewEndpoints();
app.MapTermEndpoints();
app.MapTranslationMemoryEndpoints();

app.Run();

static string ResolveSqliteConnectionString(string connectionString, string contentRootPath)
{
    var sqlite = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(sqlite.DataSource) || sqlite.DataSource == ":memory:")
    {
        return connectionString;
    }

    var databasePath = Path.IsPathRooted(sqlite.DataSource)
        ? sqlite.DataSource
        : Path.Combine(contentRootPath, sqlite.DataSource);
    var directory = Path.GetDirectoryName(databasePath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    sqlite.DataSource = databasePath;
    return sqlite.ToString();
}

public partial class Program;
