namespace ZenLead.Application.Csv;

/// <summary>Pure header-to-field guessing. F16 ports the same synonym table client-side.</summary>
public static class ColumnGuesser
{
    // Synonyms are normalised (lower-case, letters and digits only) and listed in priority order.
    private static readonly string[] Email = ["email", "emailaddress", "workemail"];
    private static readonly string[] FirstName = ["firstname", "givenname"];
    private static readonly string[] LastName = ["lastname", "surname", "familyname"];
    private static readonly string[] Name = ["name", "fullname"];
    private static readonly string[] Title = ["title", "jobtitle", "position"];
    private static readonly string[] Company = ["company", "companyname", "organization", "organisation"];
    private static readonly string[] Domain = ["companydomain", "domain", "website", "url"];
    private static readonly string[] Industry = ["industry"];
    private static readonly string[] Country = ["country"];
    private static readonly string[] Size = ["companysize", "size", "employees"];

    public static ColumnMappingGuess Guess(IReadOnlyList<string> headers)
    {
        var used = new HashSet<string>();
        string? Find(string[] synonyms)
        {
            foreach (var s in synonyms)
            {
                var hit = headers.FirstOrDefault(h => !used.Contains(h) && Normalize(h) == s);
                if (hit is not null) { used.Add(hit); return hit; }
            }
            return null;
        }

        var email = Find(Email);
        var first = Find(FirstName);
        var last = Find(LastName);
        var name = first is null && last is null ? Find(Name) : null;   // Name and First/Last are mutually exclusive in a mapping

        return new ColumnMappingGuess(name, first, last, email, Find(Title), Find(Company), Find(Domain), Find(Industry), Find(Country), Find(Size));
    }

    private static string Normalize(string header) => new(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
