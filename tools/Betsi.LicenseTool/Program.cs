namespace Betsi.LicenseTool;

using Betsi.Licensing;

// An explicit entry class rather than top-level statements: the test project references both
// this tool and the service, and two top-level programs would both be named 'Program'.
public static class LicenseToolCli
{
    public static async Task<int> Main(string[] args)
    {
        const string Usage = """
            Betsi licence tool — signs licence keys. Keep private keys off hospital servers.

            Usage:
              keygen --key-id <id> --out <directory>
                  Writes <id>.private.pem and <id>.public.pem. Key ids starting 'dev-' are refused
                  by the product outside Development.

              issue --private-key <file> --key-id <id> --tenant <guid> --features <a,b>
                    (--expires <yyyy-MM-dd> | --days <n>) [--grace-days <n>] [--issuer <name>] [--out <file>]
                  Prints the licence key, or writes it to --out.

              inspect --file <file>
                  Prints the payload of a licence WITHOUT verifying it. Use 'license status' on the
                  service to see whether it is valid.
            """;

        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i + 1 < args.Length; i += 2)
            options[args[i].TrimStart('-')] = args[i + 1];

        string Require(string name) =>
            options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required.");

        try
        {
            switch (args[0])
            {
                case "keygen":
                {
                    var keyId = Require("key-id");
                    var directory = Require("out");
                    Directory.CreateDirectory(directory);

                    var (privatePem, publicPem) = LicenseIssuer.GenerateKeyPair();
                    var privatePath = Path.Combine(directory, $"{keyId}.private.pem");
                    var publicPath = Path.Combine(directory, $"{keyId}.public.pem");

                    if (File.Exists(privatePath))
                        throw new ArgumentException($"{privatePath} already exists; refusing to overwrite a signing key.");

                    await File.WriteAllTextAsync(privatePath, privatePem);
                    await File.WriteAllTextAsync(publicPath, publicPem);

                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                    Console.WriteLine($"Private key: {privatePath}  (never commit, never deploy)");
                    Console.WriteLine($"Public key:  {publicPath}  (add to Licensing:TrustedKeys as '{keyId}')");
                    return 0;
                }

                case "issue":
                {
                    var issuer = LicenseIssuer.FromPem(await File.ReadAllTextAsync(Require("private-key")), Require("key-id"));

                    var expires = options.TryGetValue("expires", out var date)
                        ? new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(date).Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc))
                        : DateTimeOffset.UtcNow.AddDays(int.Parse(Require("days")));

                    var key = issuer.Issue(
                        Guid.Parse(Require("tenant")),
                        Require("features").Split(',', StringSplitOptions.RemoveEmptyEntries),
                        expires,
                        options.TryGetValue("grace-days", out var grace) ? int.Parse(grace) : 30,
                        options.GetValueOrDefault("issuer", "Betsi"));

                    if (options.TryGetValue("out", out var outFile))
                    {
                        await File.WriteAllTextAsync(outFile, key + Environment.NewLine);
                        Console.WriteLine($"Licence written to {outFile}, expires {expires:yyyy-MM-dd}.");
                    }
                    else
                    {
                        Console.WriteLine(key);
                    }

                    return 0;
                }

                case "inspect":
                {
                    var key = await File.ReadAllTextAsync(Require("file"));
                    if (!LicenseFormat.TryParse(key, out _, out var payload, out _))
                        throw new ArgumentException("Not a Betsi licence key.");

                    Console.WriteLine("UNVERIFIED payload:");
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }

                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }
}
