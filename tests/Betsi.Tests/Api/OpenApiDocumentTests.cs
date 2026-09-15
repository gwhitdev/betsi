namespace Betsi.Tests.Api;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The published OpenAPI document is the v1 contract (MVP-060, MVP-070). It is checked in at
/// <c>docs/openapi/v1.json</c>, so every contract change shows up in review as a diff.
/// </summary>
/// <remarks>
/// To accept an intended change, run the tests with <c>BETSI_UPDATE_OPENAPI=1</c> and commit the
/// regenerated file. Review the diff against docs/API-VERSIONING.md: removals and renames are
/// breaking and do not belong in v1.
/// </remarks>
[Collection(ApiCollection.Name)]
public class OpenApiDocumentTests
{
    private readonly BetsiApiFactory _factory;

    public OpenApiDocumentTests(BetsiApiFixture fixture)
    {
        _factory = fixture.Factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<JsonNode> PublishedAsync()
    {
        var response = await _factory.CreateClient().GetAsync("/openapi/v1.json", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    [Fact]
    public async Task The_published_document_matches_the_checked_in_contract()
    {
        var published = (await PublishedAsync()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var path = Path.Combine(RepositoryRoot(), "docs", "openapi", "v1.json");

        if (Environment.GetEnvironmentVariable("BETSI_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, published, Ct);
            return;
        }

        File.Exists(path).ShouldBeTrue($"{path} is missing. Run the tests with BETSI_UPDATE_OPENAPI=1 to create it.");
        (await File.ReadAllTextAsync(path, Ct)).ReplaceLineEndings("\n")
            .ShouldBe(published, "The API contract changed. If intended, regenerate with BETSI_UPDATE_OPENAPI=1 and review the diff.");
    }

    [Fact]
    public async Task Every_path_is_versioned_and_every_operation_declares_authentication()
    {
        var document = await PublishedAsync();

        foreach (var (path, _) in document["paths"]!.AsObject())
            path.ShouldStartWith("/api/v1/");

        document["security"].ShouldNotBeNull();
        document["components"]!["securitySchemes"]!["Bearer"].ShouldNotBeNull();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Betsi.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root (Betsi.slnx).");
    }
}
