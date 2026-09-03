namespace AIhappey.Core.Conversations.Models;

public sealed class UserMemoryContentDto
{
    public required string Subject { get; init; }
    public required string Memory { get; init; }
}

public sealed class UserMemoryDto
{
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public required string Memory { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
