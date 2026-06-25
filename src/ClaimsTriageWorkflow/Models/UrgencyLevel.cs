using System.Text.Json.Serialization;

namespace ClaimsTriageWorkflow.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UrgencyLevel
{
    Unknown = 0, // sentinel: model omitted or returned an unrecognised value
    High,
    Medium,
    Low,
}
