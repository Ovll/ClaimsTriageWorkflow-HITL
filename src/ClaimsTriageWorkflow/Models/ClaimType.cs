using System.Text.Json.Serialization;

namespace ClaimsTriageWorkflow.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClaimType
{
    Unknown = 0, // sentinel: model omitted or returned an unrecognised value
    Vehicle,
    Property,
    Health,
    Liability,
    Other,
}
