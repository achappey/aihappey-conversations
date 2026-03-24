
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIhappey.Core.Conversations.Models;

public class ConversationDto
{
  [JsonPropertyName("id")]
  public string Id { get; init; } = default!;

  [JsonPropertyName("messages")]
  public List<UIMessage> Messages { get; init; } = [];

  [JsonPropertyName("metadata")]
  public Dictionary<string, object>? Metadata { get; set; }
}