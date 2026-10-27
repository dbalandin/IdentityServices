using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace DemoApi.Tests;

/// <summary>
/// demo-api в TestServer'e. JwtBearer получает ConfigurationManager с HttpClient'ом,
/// перенаправленным на мок OIDC-authority (discovery + JWKS отдаются in-memory,
/// подпись RS256 — валидация честная, включая подбор ключа по kid).
/// </summary>
public sealed class DemoApiFactory : WebApplicationFactory<Program>
{
    public MockOidcAuthority Authority { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Identity:Authority", MockOidcAuthority.AuthorityUrl);
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                // HttpDocumentRetriever с RequireHttps=false — мок-адрес http (аналогично
                // библиотечному JwtBearerPostConfigureOptions: RequireHttps = RequireHttpsMetadata)
                o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    MockOidcAuthority.AuthorityUrl + "/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever(new HttpClient(Authority.CreateHttpHandler()))
                    {
                        RequireHttps = false,
                    });
            });
        });
    }

    /// <summary>Клиент с заголовком Authorization: Bearer …</summary>
    public HttpClient CreateClientWithToken(string accessToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        return client;
    }
}
