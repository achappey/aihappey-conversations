using AIhappey.Core.Conversations.Extensions;
using AIhappey.Core.Conversations.Models;
using AIhappey.Core.Conversations.Services;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddAuthorization();

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(230);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(230);
    o.Limits.MaxRequestBodySize = null;
});

builder.Services.AddSingleton(_ =>
{
    var conn = builder.Configuration["Storage:ConnectionString"];
    var client = new BlobServiceClient(conn);
    var container = client.GetBlobContainerClient("conversations");
    container.CreateIfNotExists();
    return container;
});

builder.Services.AddSingleton<IConversationStore, BlobConversationStore>();

// CORS for SPA (adjust origin as needed)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
              .AllowAnyHeader()
              .AllowAnyOrigin()
              .AllowAnyMethod()
              .WithExposedHeaders("WWW-Authenticate");
    });
});

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/conversations", async (
    IConversationStore store,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var tenant = ctx.GetUserOid();
    var convos = await store.GetAllAsync(tenant, ct);
    return Results.Ok(convos);
}).RequireAuthorization();

app.MapGet("/conversations/summaries", async (
    IConversationStore store,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var tenant = ctx.GetUserOid();
    var summaries = await store.GetSummariesAsync(tenant, ct);
    return Results.Ok(summaries);
}).RequireAuthorization();

app.MapGet("/conversations/search", async (
    string? query,
    int? limit,
    IConversationStore store,
    HttpContext ctx,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "A non-empty query is required." });

    var tenant = ctx.GetUserOid();
    var result = await store.SearchAsync(query, limit ?? 20, tenant, ct);
    return Results.Ok(result);
}).RequireAuthorization();

app.MapGet("/conversations/{id}", async (
    string id,
    IConversationStore store,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var tenant = ctx.GetUserOid();
    var convo = await store.GetAsync(id, tenant, ct);
    return convo is not null ? Results.Ok(convo) : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/conversations", async (
    ConversationDto dto,
    IConversationStore store,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var tenant = ctx.GetUserOid();
    await store.SaveAsync(dto, tenant, ct);
    return Results.Created($"/conversations/{dto.Id}", dto);
}).RequireAuthorization();

app.MapPut("/conversations/{id}", async (
    string id,
    ConversationDto dto,
    IConversationStore store,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var tenant = ctx.GetUserOid();
    if (dto.Id != id) return Results.BadRequest();
    await store.UpdateAsync(dto, tenant, ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/conversations/{id}", async (
    string id,
    IConversationStore store,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var tenant = ctx.GetUserOid();
    var deleted = await store.DeleteAsync(id, tenant, ct);
    return deleted ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization();

app.Run();
