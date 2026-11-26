namespace ZenLead.Application;

/// <summary>Bound from the "App" config section. Required outside Development (see StartupConfiguration).</summary>
public class AppOptions
{
    /// <summary>Public origin of the SPA, used to build links in emails (register, approval, password reset).</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;
}
