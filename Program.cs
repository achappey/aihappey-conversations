using AIhappey.Core.Conversations.Extensions;
using AIhappey.Core.Conversations.MCP;
using AIhappey.Core.Conversations.Models;
using AIhappey.Core.Conversations.Services;
using AIHappey.Common.MCP;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

var mcpServers = ConversationMcpDefinitions.GetDefinitions().ToList();
builder.Services.AddMcpServers(mcpServers);

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
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (error is not InvalidConversationAttachmentException attachmentError)
        return;

    context.Response.StatusCode = StatusCodes.Status400BadRequest;
    await context.Response.WriteAsJsonAsync(new { error = attachmentError.Message });
}));
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapMcpEndpoints(mcpServers, requireAuth: true);
app.MapMcpRegistry(mcpServers);

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

// Allows a newer client to distinguish an older deployment (safe legacy PUT
// fallback) from a missing conversation returned by a message mutation.
app.MapGet("/conversations/capabilities", () => Results.Ok(new
{
    granularMessageMutations = true
})).RequireAuthorization();

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

app.MapPost("/conversations/{id}/messages", async (
    string id,
    AIHappey.Vercel.Models.UIMessage message,
    IConversationStore store,
    HttpContext ctx,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(message.Id))
        return Results.BadRequest(new { error = "Conversation and message ids are required." });

    var result = await store.AddMessageAsync(id, message, ctx.GetUserOid(), ct);
    return result switch
    {
        ConversationMutationResult.Success => Results.Created($"/conversations/{id}/messages/{message.Id}", null),
        ConversationMutationResult.NoChange => Results.NoContent(),
        ConversationMutationResult.ConversationNotFound => Results.NotFound(),
        _ => Results.Conflict()
    };
}).RequireAuthorization();

app.MapPatch("/conversations/{id}/messages/{messageId}", async (
    string id,
    string messageId,
    ConversationMessagePatchDto patch,
    IConversationStore store,
    HttpContext ctx,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(messageId))
        return Results.BadRequest(new { error = "Conversation and message ids are required." });

    var result = await store.UpdateMessageAsync(id, messageId, patch, ctx.GetUserOid(), ct);
    return result switch
    {
        ConversationMutationResult.Success or ConversationMutationResult.NoChange => Results.NoContent(),
        ConversationMutationResult.ConversationNotFound or ConversationMutationResult.MessageNotFound => Results.NotFound(),
        _ => Results.Conflict()
    };
}).RequireAuthorization();

app.MapDelete("/conversations/{id}/messages/{messageId}", async (
    string id,
    string messageId,
    IConversationStore store,
    HttpContext ctx,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(messageId))
        return Results.BadRequest(new { error = "Conversation and message ids are required." });

    var result = await store.DeleteMessageAsync(id, messageId, ctx.GetUserOid(), ct);
    return result switch
    {
        ConversationMutationResult.Success => Results.NoContent(),
        ConversationMutationResult.ConversationNotFound or ConversationMutationResult.MessageNotFound => Results.NotFound(),
        _ => Results.Conflict()
    };
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
