using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIhappey.Core.Conversations.Extensions;
using AIhappey.Core.Conversations.Models;
using AIhappey.Core.Conversations.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIhappey.Core.Conversations.MCP;

[McpServerToolType]
public static class UserMemoryTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Description("List all memories belonging to the authenticated user, newest updated first. Memory text is limited to its first 100 characters.")]
    [McpServerTool(
        Title = "List user memories",
        Name = "user_memories_list",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false)]
    public static async Task<CallToolResult> List(
        IServiceProvider services,
        CancellationToken ct = default)
    {
        var (store, userId) = ResolveUserStore(services);
        return Structured(new { memories = await store.ListAsync(userId, ct) });
    }

    [Description("Get one complete memory belonging to the authenticated user by memory id.")]
    [McpServerTool(
        Title = "Get user memory",
        Name = "user_memories_get",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(UserMemoryDto))]
    public static async Task<CallToolResult> Get(
        IServiceProvider services,
        [Description("Server-generated id of the user memory.")] string memoryId,
        CancellationToken ct = default)
    {
        var (store, userId) = ResolveUserStore(services);
        var memory = await GetOrThrow(store, userId, memoryId, ct);
        return Structured(memory);
    }

    [Description("Create a memory for the authenticated user. The server generates its id.")]
    [McpServerTool(
        Title = "Create user memory",
        Name = "user_memories_create",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(UserMemoryDto))]
    public static async Task<CallToolResult> Create(
        IServiceProvider services,
        [Description("Short subject describing what the memory is about.")] string subject,
        [Description("Complete, potentially long memory text to store.")] string memory,
        CancellationToken ct = default)
    {
        ValidateContent(subject, memory);
        var (store, userId) = ResolveUserStore(services);
        var created = await store.CreateAsync(userId, new UserMemoryContentDto
        {
            Subject = subject,
            Memory = memory
        }, ct);
        return Structured(created);
    }

    [Description("Replace the subject and complete text of an existing memory belonging to the authenticated user.")]
    [McpServerTool(
        Title = "Update user memory",
        Name = "user_memories_update",
        ReadOnly = false,
        Idempotent = true,
        Destructive = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(UserMemoryDto))]
    public static async Task<CallToolResult> Update(
        IServiceProvider services,
        [Description("Server-generated id of the user memory.")] string memoryId,
        [Description("Replacement subject describing what the memory is about.")] string subject,
        [Description("Replacement complete, potentially long memory text.")] string memory,
        CancellationToken ct = default)
    {
        ValidateContent(subject, memory);
        var (store, userId) = ResolveUserStore(services);
        var updated = await store.UpdateAsync(userId, memoryId, new UserMemoryContentDto
        {
            Subject = subject,
            Memory = memory
        }, ct) ?? throw new McpException("User memory not found.");
        return Structured(updated);
    }

    [Description("Permanently delete a memory belonging to the authenticated user.")]
    [McpServerTool(
        Title = "Delete user memory",
        Name = "user_memories_delete",
        ReadOnly = false,
        Idempotent = true,
        Destructive = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static async Task<CallToolResult> Delete(
        IServiceProvider services,
        [Description("Server-generated id of the user memory.")] string memoryId,
        CancellationToken ct = default)
    {
        var (store, userId) = ResolveUserStore(services);
        var deleted = await store.DeleteAsync(userId, memoryId, ct);
        return Structured(new { id = memoryId, deleted });
    }

    private static async Task<UserMemoryDto> GetOrThrow(
        IUserMemoryStore store,
        string userId,
        string memoryId,
        CancellationToken ct)
        => await store.GetAsync(userId, memoryId, ct)
            ?? throw new McpException("User memory not found.");

    private static (IUserMemoryStore Store, string UserId) ResolveUserStore(IServiceProvider services)
    {
        var context = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        var userId = context?.GetUserOid();
        if (string.IsNullOrWhiteSpace(userId))
            throw new McpException("Authenticated user identity is unavailable.");

        return (services.GetRequiredService<IUserMemoryStore>(), userId);
    }

    private static void ValidateContent(string subject, string memory)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new McpException("A non-empty subject is required.");
        if (string.IsNullOrWhiteSpace(memory))
            throw new McpException("A non-empty memory is required.");
    }

    private static CallToolResult Structured<T>(T value) => new()
    {
        StructuredContent = JsonSerializer.SerializeToElement(value, JsonOptions)
    };
}
