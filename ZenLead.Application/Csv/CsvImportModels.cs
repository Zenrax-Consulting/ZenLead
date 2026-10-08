using System.Text.Json;
using System.Text.Json.Serialization;
using ZenLead.Application.Leads;

namespace ZenLead.Application.Csv;

/// <summary>Maps lead fields to CSV header names (the file's own spelling).</summary>
public record ColumnMapping(string? Name, string? FirstName, string? LastName, string Email,
    string? Title, string? CompanyName, string? CompanyDomain, string? Industry, string? Country, string? CompanySize);

/// <summary>Server-side starting point for the mapping UI; <see cref="Email"/> is null when no header looked like one.</summary>
public record ColumnMappingGuess(string? Name, string? FirstName, string? LastName, string? Email,
    string? Title, string? CompanyName, string? CompanyDomain, string? Industry, string? Country, string? CompanySize);

public record CsvRowIssue(int Row, IngestionOutcome Outcome, string Reason, string? Email, string? Name);

public static class CsvJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
