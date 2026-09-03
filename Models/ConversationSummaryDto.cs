using System.Text.Json.Serialization;

namespace AIhappey.Core.Conversations.Models;

public sealed class ConversationSummaryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("name")]
    public string Name { get; init; } = default!;

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; init; }

    [JsonPropertyName("activityAt")]
    public DateTimeOffset ActivityAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}
