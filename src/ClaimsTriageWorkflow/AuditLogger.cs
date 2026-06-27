using System.Text.Json;
using ClaimsTriageWorkflow.Models;

namespace ClaimsTriageWorkflow;

/// <summary>Appends one JSON line per claim decision to an append-only audit log file.</summary>
public static class AuditLogger
{
    public static async Task AppendAsync(AuditRecord record, string path)
    {
        var line = JsonSerializer.Serialize(record) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line);
    }
}
