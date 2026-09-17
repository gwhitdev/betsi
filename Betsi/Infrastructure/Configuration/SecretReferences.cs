namespace Betsi.Infrastructure.Configuration;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Resolves <c>secret:</c> references in configuration, so that credentials reach the process
/// from a secret store rather than from a settings file or a container's environment listing.
/// </summary>
/// <remarks>
/// <para>
/// Phase D deliberately kept credentials out of the tenant registry: a tenant record names a
/// server profile, and the profile's connection string lives in configuration. That left the
/// connection string itself in <c>appsettings</c>. This closes it. Any configuration value may
/// be written as a reference and is replaced, before anything binds options, by the secret it
/// names:
/// </para>
/// <list type="bullet">
///   <item><c>secret:control-plane</c> — the file <c>control-plane</c> in the secrets
///     directory (<c>Secrets:Directory</c>, default <c>/run/secrets</c>). This is how Docker
///     Compose, Kubernetes and Azure Container Apps all present a mounted secret.</item>
///   <item><c>secret:file:/var/run/betsi/control-plane</c> — an explicit path.</item>
///   <item><c>secret:env:BETSI_CONTROL_PLANE</c> — an environment variable, for hosts that
///     inject secrets only that way.</item>
/// </list>
/// <para>
/// A reference that cannot be resolved throws at startup, naming the configuration key but
/// never the value. Failing closed matters more than starting: an unresolved connection string
/// would otherwise become an empty one, and the clearest symptom of that is a service that
/// starts and then refuses every request.
/// </para>
/// <para>
/// Resolution is eager and one-pass over the configuration that exists when it is called, which
/// is every source the host has added. Resolved values are held in memory for the life of the
/// process: rotating a secret takes a restart, which is how the deployment runbook describes it.
/// </para>
/// </remarks>
public static class SecretReferences
{
    public const string Prefix = "secret:";
    public const string DirectoryKey = "Secrets:Directory";
    public const string DefaultDirectory = "/run/secrets";

    /// <summary>Replaces every <c>secret:</c> reference in the configuration with its value.</summary>
    /// <exception cref="InvalidOperationException">A reference names a secret that is not there.</exception>
    public static void ResolveSecretReferences(this IConfigurationManager configuration)
    {
        var directory = configuration[DirectoryKey] is { Length: > 0 } configured ? configured : DefaultDirectory;

        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in configuration.AsEnumerable())
        {
            if (value is null || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            resolved[key] = Resolve(key, value[Prefix.Length..], directory);
        }

        if (resolved.Count == 0)
            return;

        // Added last so it wins over the source the reference came from. Every other source
        // keeps its precedence: only the referencing keys are overridden.
        configuration.AddInMemoryCollection(resolved);
    }

    private static string Resolve(string key, string reference, string directory)
    {
        if (reference.Length == 0)
            throw Missing(key, "the reference names nothing");

        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var name = reference[4..];
            return Environment.GetEnvironmentVariable(name)
                ?? throw Missing(key, $"environment variable '{name}' is not set");
        }

        var path = reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? reference[5..]
            : Path.Combine(directory, reference);

        try
        {
            // TrimEnd, not Trim: a file written by `echo` ends with a newline that is not part
            // of the secret, but leading whitespace could be.
            return File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Missing(key, $"'{path}' could not be read: {exception.Message}");
        }
    }

    private static InvalidOperationException Missing(string key, string reason) =>
        new($"Configuration key '{key}' is a secret reference that could not be resolved: {reason}. " +
            "The service will not start with an unresolved secret. See docs/runbooks/deployment.md.");
}
