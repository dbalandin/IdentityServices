using System.Net;
using System.Web;
using Xunit;

namespace Demopage.Tests;

/// <summary>
/// Стенд (deploy/idp-test-appweb) запускает demopage с ASPNETCORE_ENVIRONMENT=Production — там
/// включён app.UseExceptionHandler("/Error"), а appsettings.Development.json не читается.
/// Проверяем, что и в Production аутентификация из библиотеки работает (исключение ушло бы
/// в страницу ошибки: для POST это пустой 400).
/// </summary>
public sealed class ProductionEnvironmentTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")] // форма «Выйти» в _Layout делает POST
    public async Task Logout_In_Production_Redirects_To_Idp(string method)
    {
        using var factory = DemopageFactory.CreateProduction();

        using var resp = await factory.CreateClientNoRedirect()
            .SendAsync(new HttpRequestMessage(new HttpMethod(method), "/Account/Logout"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith(MockOidcAuthority.EndSessionEndpoint, resp.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Private_Without_Session_In_Production_Redirects_To_Idp()
    {
        using var factory = DemopageFactory.CreateProduction();

        using var resp = await factory.CreateClientNoRedirect().GetAsync("/Private");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var query = HttpUtility.ParseQueryString(resp.Headers.Location!.Query);
        Assert.Equal("demo-api", query["client_id"]);
        Assert.Equal("http://localhost/signin-oidc", query["redirect_uri"]);
    }
}
