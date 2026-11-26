namespace ZenLead.Domain.Scheduling;

public static class TimeZones
{
    /// <summary>Resolves an IANA time-zone id (e.g. "Asia/Dubai"). The one validator both the API and campaign scheduling use.</summary>
    public static bool TryFind(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }   // IANA ids work on Windows (ICU) and Linux in .NET 6+
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
