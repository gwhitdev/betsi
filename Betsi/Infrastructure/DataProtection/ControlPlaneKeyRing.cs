namespace Betsi.Infrastructure.DataProtection;

using Betsi.ControlPlane;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

/// <summary>Where the Data Protection key ring is kept.</summary>
public enum KeyRingStore
{
    /// <summary>The control-plane database, shared by every instance. The default.</summary>
    ControlPlane = 0,

    /// <summary>A directory, which must be shared storage in a multi-instance deployment.</summary>
    FileSystem = 1,

    /// <summary>
    /// In memory, discarded on restart. Every webhook and integration secret encrypted with it
    /// becomes unreadable, so it is refused outside Development.
    /// </summary>
    Ephemeral = 2
}

public sealed class DataProtectionOptions
{
    public const string SectionName = "DataProtection";

    public KeyRingStore Store { get; set; } = KeyRingStore.ControlPlane;

    /// <summary>Set (with <see cref="KeyRingStore.FileSystem"/>) to keep keys on shared storage.</summary>
    public string? KeysDirectory { get; set; }

    /// <summary>
    /// A PKCS#12 certificate used to encrypt the key ring at rest. Without it the keys are
    /// stored unencrypted on Linux, where there is no DPAPI, and anything that can read the
    /// control-plane database can decrypt every integration secret.
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>Password for <see cref="CertificatePath"/>. A <c>secret:</c> reference in a deployment.</summary>
    public string? CertificatePassword { get; set; }
}

/// <summary>One Data Protection key, as the key ring stores it.</summary>
/// <remarks>
/// The XML holds the key material, encrypted at rest only if a certificate is configured, so
/// this table is as sensitive as the integration secrets it protects. It lives in the control
/// plane rather than in a tenant database because the key ring is per-deployment, not per-tenant,
/// and because a tenant database restored from backup must not carry an old key ring with it.
/// </remarks>
public sealed class DataProtectionKeyRecord
{
    public long Id { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string Xml { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Stores the Data Protection key ring in the control-plane database.</summary>
/// <remarks>
/// The interface is synchronous by design in Data Protection, and is called rarely — once at
/// startup and when a key is created or rolled — so a synchronous context is used rather than
/// blocking on an asynchronous one.
/// </remarks>
public sealed class ControlPlaneXmlRepository : IXmlRepository
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _contextFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<ControlPlaneXmlRepository> _logger;

    public ControlPlaneXmlRepository(
        IDbContextFactory<ControlPlaneDbContext> contextFactory,
        TimeProvider time,
        ILogger<ControlPlaneXmlRepository> logger)
    {
        _contextFactory = contextFactory;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var context = _contextFactory.CreateDbContext();

        return context.DataProtectionKeys
            .AsNoTracking()
            .OrderBy(key => key.Id)
            .Select(key => key.Xml)
            .ToList()
            .Select(XElement.Parse)
            .ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var context = _contextFactory.CreateDbContext();

        context.DataProtectionKeys.Add(new DataProtectionKeyRecord
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
            CreatedAt = _time.GetUtcNow().UtcDateTime
        });

        context.SaveChanges();
        _logger.LogInformation("Stored data protection key {FriendlyName} in the control plane", friendlyName);
    }
}

public static class DataProtectionModule
{
    /// <summary>
    /// Configures the key ring that protects webhook and inbound integration secrets.
    /// </summary>
    /// <remarks>
    /// Every instance must read the same key ring: a secret encrypted by one instance is read
    /// by whichever instance next delivers a webhook. The default store is the control-plane
    /// database, which every instance already has a connection to, so a second instance needs
    /// no shared filesystem and no extra configuration to be correct.
    /// </remarks>
    public static IServiceCollection AddBetsiDataProtection(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = new DataProtectionOptions();
        configuration.GetSection(DataProtectionOptions.SectionName).Bind(options);

        // A directory on its own says what was meant, so it selects the store rather than
        // being silently ignored against the default.
        if (options.Store == KeyRingStore.ControlPlane && !string.IsNullOrWhiteSpace(options.KeysDirectory))
            options.Store = KeyRingStore.FileSystem;

        if (options.Store == KeyRingStore.Ephemeral && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "DataProtection:Store is Ephemeral outside Development. The key ring would be lost on " +
                "restart and every webhook and inbound integration secret with it. Use ControlPlane, " +
                "or FileSystem with shared storage.");
        }

        var builder = services.AddDataProtection().SetApplicationName("Betsi");

        switch (options.Store)
        {
            case KeyRingStore.FileSystem:
                builder.PersistKeysToFileSystem(new DirectoryInfo(
                    options.KeysDirectory ?? throw new InvalidOperationException(
                        "DataProtection:Store is FileSystem but DataProtection:KeysDirectory is not set.")));
                break;

            case KeyRingStore.Ephemeral:
                builder.UseEphemeralDataProtectionProvider();
                break;

            case KeyRingStore.ControlPlane:
            default:
                services.AddSingleton<ControlPlaneXmlRepository>();

                // Configured through options rather than PersistKeysTo…: the repository needs
                // services, and the key ring is not built until first use, by which time the
                // provider exists.
                services.AddOptions<KeyManagementOptions>().Configure<IServiceProvider>(
                    (keyManagement, provider) =>
                        keyManagement.XmlRepository = provider.GetRequiredService<ControlPlaneXmlRepository>());
                break;
        }

        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            // X509CertificateLoader, not the obsolete constructor: loading is explicit about
            // the file being PKCS#12 rather than guessing from its content.
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                options.CertificatePath, options.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);

            builder.ProtectKeysWithCertificate(certificate);
        }
        else if (!environment.IsDevelopment() && options.Store != KeyRingStore.FileSystem)
        {
            // Not fatal: a key ring in the control plane is no more exposed than the tenant
            // credentials alongside it, and refusing to start would block a pilot on a
            // certificate. It is a DSPT finding, so it is logged at warning on every start.
            Log.Logger.Warning(
                "The data protection key ring is stored without encryption at rest. Configure " +
                "DataProtection:CertificatePath before holding real patient data (DSPT).");
        }

        return services;
    }
}
