using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public static class ConversationTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Description("List the authenticated user's conversations as lightweight summaries, ordered by most recent activity.")]
    [McpServerTool(
        Title = "List conversations",
        Name = "conversations_list_all",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static async Task<CallToolResult> ListAll(
        IServiceProvider services,
        CancellationToken ct = default)
    {
        var (store, userId) = ResolveUserStore(services);
        var summaries = await store.GetSummariesAsync(userId, ct);

        return Structured(new { conversations = summaries });
    }

    [Description("Get one of the authenticated user's conversations by id. Inline attachment bytes are omitted from file parts.")]
    [McpServerTool(
        Title = "Get conversation by id",
        Name = "conversations_get_conversation",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static async Task<CallToolResult> GetConversation(
        IServiceProvider services,
        [Description("Id of the conversation.")] string conversationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new McpException("A non-empty conversationId is required.");

        var (store, userId) = ResolveUserStore(services);
        var conversation = await store.GetWithoutAttachmentDataAsync(conversationId, userId, ct)
            ?? throw new McpException("Conversation not found.");

        return new CallToolResult
        {
            StructuredContent = SanitizeConversation(conversation)
        };
    }

    [Description("Plain-text search across the authenticated user's conversations. Multi-word queries require every word in the same text part, regardless of order or distance.")]
    [McpServerTool(
        Title = "Search conversations (text only)",
        Name = "conversations_search_text",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static async Task<CallToolResult> SearchText(
        IServiceProvider services,
        [Description("Search query. Single words use substring matching; multi-word queries require every word in the same text part.")]
        string query,
        [Description("Maximum number of results. Defaults to 20 and is clamped to the range 1-50.")]
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new McpException("A non-empty query is required.");

        var (store, userId) = ResolveUserStore(services);
        var result = await store.SearchAsync(query, Math.Clamp(limit, 1, 50), userId, ct);

        return Structured(result);
    }

    internal static JsonElement SanitizeConversation(ConversationDto conversation)
    {
        var root = JsonSerializer.SerializeToNode(conversation, JsonOptions)
            ?? throw new InvalidOperationException("Could not serialize the conversation.");

        if (root["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonObject>())
            {
                if (message["parts"] is not JsonArray parts) continue;

                foreach (var part in parts.OfType<JsonObject>())
                {
                    if (string.Equals(part["type"]?.GetValue<string>(), "file", StringComparison.Ordinal))
                        part.Remove("url");
                }
            }
        }

        return JsonSerializer.SerializeToElement(root, JsonOptions);
    }

    private static (IConversationStore Store, string UserId) ResolveUserStore(IServiceProvider services)
    {
        var context = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        var userId = context?.GetUserOid();

        if (string.IsNullOrWhiteSpace(userId))
            throw new McpException("Authenticated user identity is unavailable.");

        return (services.GetRequiredService<IConversationStore>(), userId);
    }

    private static CallToolResult Structured<T>(T value) => new()
    {
        StructuredContent = JsonSerializer.SerializeToElement(value, JsonOptions)
    };
}
