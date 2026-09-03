using AIhappey.Core.Conversations.Models;

namespace AIhappey.Core.Conversations.Services;

public interface IUserMemoryStore
{
    Task<IReadOnlyList<UserMemoryDto>> ListAsync(string userId, CancellationToken ct = default);
    Task<UserMemoryDto?> GetAsync(string userId, string memoryId, CancellationToken ct = default);
    Task<UserMemoryDto> CreateAsync(
        string userId,
        UserMemoryContentDto content,
        CancellationToken ct = default);
    Task<UserMemoryDto?> UpdateAsync(
        string userId,
        string memoryId,
        UserMemoryContentDto content,
        CancellationToken ct = default);
    Task<bool> DeleteAsync(string userId, string memoryId, CancellationToken ct = default);
}
