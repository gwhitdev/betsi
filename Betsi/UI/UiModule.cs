namespace Betsi.UI;

using Betsi.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

public sealed class UiOptions
{
    public const string SectionName = "UI";

    /// <summary>
    /// Serve the web interface. Off by default: an instance that exists only to receive HL7
    /// messages or to run operator commands has no reason to expose a sign-in page.
    /// </summary>
    public bool Enabled { get; set; }
}

public static class UiModule
{
    /// <summary>Languages the interface is offered in. English first as the fallback.</summary>
    /// <remarks>
    /// Welsh is not an optional extra here. The Welsh Language (Wales) Measure 2011 requires
    /// public services in Wales to treat Welsh no less favourably than English, so it is built
    /// in from the first screen rather than added as a translation pass.
    /// </remarks>
    public static readonly string[] SupportedCultures = ["en-GB", "cy-GB"];

    public static IServiceCollection AddBetsiUi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new UiOptions();
        configuration.GetSection(UiOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        if (!options.Enabled)
            return services;

        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();
        services.AddSignalR();
        services.AddSingleton<IBoardNotifier, BoardNotifier>();

        // Scoped is per circuit in Blazor Server, which is what these want: one signed-in user,
        // for the life of one browser connection.
        services.AddScoped<UiSession>();
        services.AddScoped<UiScopeRunner>();
        services.AddSingleton<UiIdentityResolver>();

        services.AddLocalization();
        services.Configure<RequestLocalizationOptions>(localization =>
        {
            var cultures = SupportedCultures.Select(c => new CultureInfo(c)).ToList();
            localization.DefaultRequestCulture = new RequestCulture(cultures[0]);
            localization.SupportedCultures = cultures;
            localization.SupportedUICultures = cultures;
        });

        return services;
    }

    public static WebApplication MapBetsiUi(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<UiOptions>().Enabled)
            return app;

        app.UseRequestLocalization();

        // Anonymous: the browser must be able to fetch the framework's own files before anyone
        // has signed in, or the sign-in page cannot render to ask them to.
        app.MapStaticAssets().AllowAnonymous();

        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode()
            // Each page declares its own [Authorize]; the router renders a sign-in prompt for
            // the rest. A blanket policy here would fight the framework's negotiation requests.
            .AllowAnonymous()
            .Add(endpoint =>
            {
                // .NET 10 exposes the generated circuit transport options as metadata.
                foreach (var transport in endpoint.Metadata.OfType<HttpConnectionDispatcherOptions>())
                    transport.CloseOnAuthenticationExpiration = true;
            });

        app.MapHub<BoardHub>(UiRoutes.Hub, options => options.CloseOnAuthenticationExpiration = true);

        MapSignIn(app);
        return app;
    }

    private static void MapSignIn(WebApplication app)
    {
        // Minimal endpoints rather than a controller: each is one redirect, and keeping them
        // beside the routes they use makes the flow readable in one place.
        //
        // None of them appears in the OpenAPI document. That document is the contract for
        // machine callers; a browser sign-in redirect is not something an integration can use,
        // and publishing it would invite someone to try.
        app.MapGet(UiRoutes.SignIn, (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) },
                [BetsiAuthenticationSchemes.Oidc]))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapPost(UiRoutes.SignOut, () =>
            Results.SignOut(
                new AuthenticationProperties { RedirectUri = UiRoutes.SignedOut },
                [BetsiAuthenticationSchemes.Cookie, BetsiAuthenticationSchemes.Oidc]))
            .AllowAnonymous()
            .ExcludeFromDescription();

        // The language a person reads in is a preference, not identity, so it is a plain cookie
        // and survives sign-out. POST because it changes state, and same-site so another site
        // cannot set it.
        app.MapPost(UiRoutes.Language, (HttpContext context, [FromForm] string culture, [FromForm] string? returnUrl) =>
        {
            if (!UiModule.SupportedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest("Unsupported language.");

            context.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                new CookieOptions
                {
                    HttpOnly = false,
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddYears(1)
                });

            return Results.LocalRedirect(SafeReturnUrl(returnUrl));
        }).AllowAnonymous().ExcludeFromDescription();
    }

    /// <summary>
    /// A return path this application will actually redirect to.
    /// </summary>
    /// <remarks>
    /// Local paths only. An absolute URL here is an open redirect: a link that signs a clinician
    /// in and then lands them on a page someone else controls, still carrying the trust of
    /// having just authenticated.
    /// </remarks>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : UiRoutes.Dashboard;
}
