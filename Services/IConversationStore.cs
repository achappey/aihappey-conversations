namespace AIhappey.Core.Conversations.Services;

using AIhappey.Core.Conversations.Models;

public interface IConversationStore
{
    Task<ConversationDto?> GetAsync(string id, string? userId = null, CancellationToken ct = default);
    Task SaveAsync(ConversationDto conversation, string? userId = null, CancellationToken ct = default);
    Task UpdateAsync(ConversationDto conversation, string? userId = null, CancellationToken ct = default);
    Task<IEnumerable<ConversationDto>> GetAllAsync(string? userId = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, string? userId = null, CancellationToken ct = default);
}
