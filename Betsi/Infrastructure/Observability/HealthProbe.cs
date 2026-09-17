namespace Betsi.Infrastructure.Observability;

/// <summary>
/// <c>--health-probe</c>: asks the instance in this container whether it is ready, and exits 0
/// or 1. The container image's HEALTHCHECK runs it.
/// </summary>
/// <remarks>
/// The probe lives in the application binary because the runtime image has no curl and no wget,
/// and adding either to a clinical system's image to answer one question is a worse trade than
/// fifteen lines here. It calls <c>/health/ready</c> over the loopback interface, so it asks the
/// same question a load balancer does and never leaves the container.
/// </remarks>
public static class HealthProbe
{
    public const string Argument = "--health-probe";

    public static bool IsProbe(string[] args) => args.Contains(Argument, StringComparer.Ordinal);

    public static async Task<int> RunAsync(IConfiguration configuration)
    {
        // The same variable the host binds to, so the probe follows a changed port without
        // being told about it separately.
        var port = configuration["ASPNETCORE_HTTP_PORTS"]?.Split(';').FirstOrDefault() ?? "8080";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/health/ready");

            if (response.IsSuccessStatusCode)
                return 0;

            await Console.Error.WriteLineAsync($"Not ready: HTTP {(int)response.StatusCode}.");
            return 1;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Not ready: {exception.Message}");
            return 1;
        }
    }
}
