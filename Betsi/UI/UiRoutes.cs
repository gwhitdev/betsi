namespace Betsi.UI;

/// <summary>
/// The web interface's paths, in one place because authentication, routing and the components
/// all have to agree on them.
/// </summary>
public static class UiRoutes
{
    public const string Dashboard = "/";
    public const string EscalationBoard = "/escalations";
    public const string WaitingBoard = "/waiting";
    public const string RegisterPatient = "/patients/register";
    public const string Episodes = "/episodes";
    public const string Policy = "/policy";

    public static string Episode(Guid id) => $"{Episodes}/{id}";

    public const string SignIn = "/sign-in";
    public const string SignInCallback = "/sign-in/callback";
    public const string SignOut = "/sign-out";
    public const string SignOutCallback = "/sign-out/callback";
    public const string SignedOut = "/signed-out";
    public const string Forbidden = "/forbidden";
    public const string Language = "/language";

    /// <summary>The live-update hub the boards subscribe to.</summary>
    public const string Hub = "/hub/boards";

    /// <summary>
    /// Whether a path belongs to the machine API rather than the web interface.
    /// </summary>
    /// <remarks>
    /// Drives which credential a request is judged on: the API expects a bearer token and
    /// answers with RFC 9457 problem details, while the interface expects a cookie and redirects
    /// a signed-out user to sign in. Answering an API call with a redirect to a login page is
    /// one of the more confusing things an integration can be given.
    /// </remarks>
    public static bool IsApiPath(PathString path) =>
        path.StartsWithSegments("/api") ||
        path.StartsWithSegments("/openapi") ||
        path.StartsWithSegments("/swagger") ||
        path.StartsWithSegments("/health");
}
