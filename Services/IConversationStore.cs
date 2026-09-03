namespace AIhappey.Core.Conversations.Services;

using AIhappey.Core.Conversations.Models;
using AIHappey.Vercel.Models;

public enum ConversationMutationResult
{
    Success,
    NoChange,
    ConversationNotFound,
    MessageNotFound
}

public interface IConversationStore
{
    Task<ConversationDto?> GetAsync(string id, string? userId = null, CancellationToken ct = default);
    Task<ConversationDto?> GetWithoutAttachmentDataAsync(string id, string? userId = null, CancellationToken ct = default);
    Task SaveAsync(ConversationDto conversation, string? userId = null, CancellationToken ct = default);
    Task UpdateAsync(ConversationDto conversation, string? userId = null, CancellationToken ct = default);
    Task<ConversationMutationResult> AddMessageAsync(
        string conversationId,
        UIMessage message,
        string? userId = null,
        CancellationToken ct = default);
    Task<ConversationMutationResult> UpdateMessageAsync(
        string conversationId,
        string messageId,
        ConversationMessagePatchDto patch,
        string? userId = null,
        CancellationToken ct = default);
    Task<ConversationMutationResult> DeleteMessageAsync(
        string conversationId,
        string messageId,
        string? userId = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<ConversationSummaryDto>> GetSummariesAsync(string? userId = null, CancellationToken ct = default);
    Task<ConversationSearchResultDto> SearchAsync(string query, int limit = 20, string? userId = null, CancellationToken ct = default);

    // Compatibility API for clients deployed before lightweight summaries.
    Task<IEnumerable<ConversationDto>> GetAllAsync(string? userId = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, string? userId = null, CancellationToken ct = default);
}
