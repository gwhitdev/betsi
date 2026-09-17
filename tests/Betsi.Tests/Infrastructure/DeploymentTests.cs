namespace Betsi.Tests.Infrastructure;

using Betsi.ControlPlane;
using Betsi.Infrastructure.Configuration;
using Betsi.Infrastructure.DataProtection;
using Betsi.Infrastructure.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// The Phase I pieces a deployment depends on: secret resolution, the shared key ring's
/// configuration, the correlation id and the operator command line's flag parsing.
/// </summary>
public class SecretReferenceTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"betsi-secrets-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ConfigurationManager ConfigurationWith(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(
            settings.ToDictionary(s => s.Key, s => (string?)s.Value)
                .Append(new KeyValuePair<string, string?>(SecretReferences.DirectoryKey, _directory))
                .ToDictionary(pair => pair.Key, pair => pair.Value));

        return configuration;
    }

    private string WriteSecret(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_bare_reference_reads_the_file_of_that_name_in_the_secrets_directory()
    {
        // How Docker, Kubernetes and Azure Container Apps all present a mounted secret.
        WriteSecret("control-plane", "Server=db;Database=betsi_control\n");

        var configuration = ConfigurationWith(("ConnectionStrings:ControlPlane", "secret:control-plane"));
        configuration.ResolveSecretReferences();

        // The trailing newline `echo` leaves behind is not part of the secret.
        configuration["ConnectionStrings:ControlPlane"].ShouldBe("Server=db;Database=betsi_control");
    }

    [Fact]
    public void A_file_reference_reads_an_explicit_path()
    {
        var path = WriteSecret("elsewhere", "a-secret");

        var configuration = ConfigurationWith(("Tenancy:DatabaseServers:default", $"secret:file:{path}"));
        configuration.ResolveSecretReferences();

        configuration["Tenancy:DatabaseServers:default"].ShouldBe("a-secret");
    }

    [Fact]
    public void An_env_reference_reads_an_environment_variable()
    {
        var name = $"BETSI_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "from-the-environment");

        try
        {
            var configuration = ConfigurationWith(("DataProtection:CertificatePassword", $"secret:env:{name}"));
            configuration.ResolveSecretReferences();

            configuration["DataProtection:CertificatePassword"].ShouldBe("from-the-environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Values_that_are_not_references_are_left_alone()
    {
        var configuration = ConfigurationWith(
            ("Observability:ServiceName", "betsi"),
            ("Authentication:Jwt:Authority", "https://login.example.test"));

        configuration.ResolveSecretReferences();

        configuration["Observability:ServiceName"].ShouldBe("betsi");
        configuration["Authentication:Jwt:Authority"].ShouldBe("https://login.example.test");
    }

    [Fact]
    public void An_unresolvable_reference_stops_the_service_starting()
    {
        // Fail closed. An unresolved connection string would otherwise become an empty one, and
        // the symptom of that is a service that starts and then refuses every request.
        var configuration = ConfigurationWith(("ConnectionStrings:ControlPlane", "secret:not-mounted"));

        var failure = Should.Throw<InvalidOperationException>(() => configuration.ResolveSecretReferences());

        failure.Message.ShouldContain("ConnectionStrings:ControlPlane");
        failure.Message.ShouldContain("could not be resolved");
        // The reason names the path, never a value.
        failure.Message.ShouldContain("not-mounted");
    }

    [Fact]
    public void A_missing_environment_variable_is_refused_too()
    {
        var configuration = ConfigurationWith(("ConnectionStrings:ControlPlane", "secret:env:BETSI_NEVER_SET_ANYWHERE"));

        Should.Throw<InvalidOperationException>(() => configuration.ResolveSecretReferences())
            .Message.ShouldContain("BETSI_NEVER_SET_ANYWHERE");
    }
}

public class DataProtectionConfigurationTests
{
    private static IServiceCollection Services(IHostEnvironment environment, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddBetsiDataProtection(configuration, environment);
        return services;
    }

    private static IHostEnvironment Environment(string name) =>
        new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = name, ApplicationName = "Betsi" };

    [Fact]
    public void An_ephemeral_key_ring_outside_development_is_refused_at_startup()
    {
        // Every webhook and inbound integration secret is encrypted with the key ring. An
        // ephemeral one loses them all on restart, which would look like every subscriber
        // silently going quiet.
        var failure = Should.Throw<InvalidOperationException>(() =>
            Services(Environment("Production"), ("DataProtection:Store", "Ephemeral")));

        failure.Message.ShouldContain("Ephemeral outside Development");
    }

    [Fact]
    public void An_ephemeral_key_ring_is_allowed_in_development()
    {
        Should.NotThrow(() => Services(Environment("Development"), ("DataProtection:Store", "Ephemeral")));
    }

    [Fact]
    public void A_keys_directory_selects_the_file_system_store_rather_than_being_ignored()
    {
        // Configuring a directory says what was meant. Silently keeping the control-plane
        // default would put the keys somewhere other than where the operator was told.
        var directory = Path.Combine(Path.GetTempPath(), $"betsi-keys-{Guid.NewGuid():N}");

        try
        {
            var provider = Services(Environment("Production"), ("DataProtection:KeysDirectory", directory))
                .BuildServiceProvider();

            provider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
                .CreateProtector("test").Protect(System.Text.Encoding.UTF8.GetBytes("payload"));

            Directory.Exists(directory).ShouldBeTrue();
            Directory.GetFiles(directory, "key-*.xml").ShouldNotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_control_plane_store_is_the_default()
    {
        // The registration, not a resolved instance: the repository needs the control-plane
        // context factory, which belongs to AddInfrastructure and is not part of this module.
        Services(Environment("Production"))
            .ShouldContain(descriptor => descriptor.ServiceType == typeof(ControlPlaneXmlRepository));
    }
}

public class CorrelationIdTests
{
    [Theory]
    [InlineData("epr-request-4821", "epr-request-4821")]
    [InlineData("  padded  ", "padded")]
    public void A_usable_caller_supplied_id_is_honoured(string supplied, string expected) =>
        CorrelationIdMiddleware.Sanitise(supplied).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has\nnewline")]
    [InlineData("has\0null")]
    [InlineData("nonprintableÿ")]
    public void Anything_that_would_forge_a_log_line_is_discarded(string supplied)
    {
        // The id reaches an operator's console and the log store's query language, and it is
        // written by whoever called the API.
        CorrelationIdMiddleware.Sanitise(supplied).ShouldBeNull();
    }

    [Fact]
    public void A_very_long_id_is_capped_to_what_the_idempotency_record_can_store()
    {
        // The command envelope persists its correlation id in an nvarchar(100) column. An id
        // this accepted but the database could not store would turn a caller's long header
        // into a 500 at the point the command is recorded.
        var sanitised = CorrelationIdMiddleware.Sanitise(new string('a', 500));

        sanitised.ShouldNotBeNull();
        sanitised.Length.ShouldBe(CorrelationIdMiddleware.MaxLength);
    }
}

public class OperatorCommandLineTests
{
    [Fact]
    public void Options_take_the_value_that_follows_them() =>
        OperatorCli.ParseOptions(["--id", "abc", "--reason", "a reason"])
            .ShouldBe(new Dictionary<string, string> { ["id"] = "abc", ["reason"] = "a reason" });

    [Fact]
    public void A_switch_with_no_value_is_a_flag()
    {
        // `--repoint` says to do something, not what to do it with.
        var options = OperatorCli.ParseOptions(["--id", "abc", "--replace", "--repoint"]);

        options["replace"].ShouldBe("true");
        options["repoint"].ShouldBe("true");
        options["id"].ShouldBe("abc");
    }

    [Fact]
    public void A_value_without_an_option_is_a_usage_error() =>
        Should.Throw<FormatException>(() => OperatorCli.ParseOptions(["id", "abc"]));
}
