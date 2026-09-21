namespace Betsi.Tests.UI;

using Betsi.Tests.Api;
using System.Net;

[Collection(ApiCollection.Name)]
public sealed class SignInTests(BetsiApiFixture fixture)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/waiting")]
    [InlineData("/escalations")]
    public async Task Signed_out_visitors_can_reach_sign_in(string path)
    {
        using var client = fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain($"href=\"/sign-in?returnUrl={Uri.EscapeDataString(path)}\"");
        html.ShouldContain("You need to sign in to see this page.");
        html.ShouldNotContain("You are not signed in.");
    }
}
