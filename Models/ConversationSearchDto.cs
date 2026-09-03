using System.Text.Json.Serialization;

namespace AIhappey.Core.Conversations.Models;

public sealed class ConversationSearchHitDto
{
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; init; } = default!;

    [JsonPropertyName("messageId")]
    public string? MessageId { get; init; }

    [JsonPropertyName("messageIndex")]
    public int MessageIndex { get; init; }

    [JsonPropertyName("role")]
    public string Role { get; init; } = default!;

    [JsonPropertyName("partIndex")]
    public int PartIndex { get; init; }

    [JsonPropertyName("matchIndex")]
    public int MatchIndex { get; init; }

    [JsonPropertyName("snippet")]
    public string Snippet { get; init; } = default!;
}

public sealed class ConversationSearchResultDto
{
    [JsonPropertyName("query")]
    public string Query { get; init; } = default!;

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<ConversationSearchHitDto> Results { get; init; } = [];
}
