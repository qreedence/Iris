namespace Iris.Api.Authentication
{
    /// <summary>
    /// Frontend origin configuration. Single source of truth for the SPA's base URL —
    /// used for the CORS policy and the post-OAuth-login redirect.
    /// </summary>
    public class FrontendOptions
    {
        public const string SectionName = "Frontend";

        public string BaseUrl { get; set; } = string.Empty;
    }
}
