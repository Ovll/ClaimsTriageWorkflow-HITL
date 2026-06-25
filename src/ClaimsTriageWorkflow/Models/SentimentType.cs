using System.Text.Json.Serialization;

namespace ClaimsTriageWorkflow.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SentimentType
{
    Unknown = 0, // sentinel: model omitted or returned an unrecognised value
    Positive,
    Neutral,
    Frustrated,
    Distressed,
}
