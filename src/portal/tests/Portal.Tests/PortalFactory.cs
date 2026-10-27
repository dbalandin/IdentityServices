using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Portal.Tests;

/// <summary>
/// Портал в TestServer'e:
///   • JwtBearer получает discovery/JWKS от MockOidcAuthority (подпись RS256 честная);
///   • HttpClient("keycloak-admin") ходит в FakeKeycloakAdmin (in-memory Admin REST);
///   • клиент portal авторизуется клиент-секретом "test-secret" (фейк проверяет его).
/// </summary>
public sealed class PortalFactory : WebApplicationFactory<Program>
{
    public MockOidcAuthority Authority { get; } = new();
    public FakeKeycloakAdmin FakeAdmin { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Identity:Authority", MockOidcAuthority.AuthorityUrl);
        builder.UseSetting("Portal:KeycloakBaseUrl", "http://kc");
        builder.UseSetting("Portal:ClientSecret", "test-secret");
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    MockOidcAuthority.AuthorityUrl + "/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever(new HttpClient(Authority.CreateHttpHandler()))
                    {
                        RequireHttps = false,
                    });
            });
            // OIDC-логин (challenge страниц) тоже получает метаданные от мока
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    MockOidcAuthority.AuthorityUrl + "/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever(new HttpClient(Authority.CreateHttpHandler()))
                    {
                        RequireHttps = false,
                    });
            });
            // все HttpClient'ы (в т.ч. "keycloak-admin" и token endpoint) → фейк
            services.ConfigureAll<HttpClientFactoryOptions>(o =>
                o.HttpMessageHandlerBuilderActions.Add(b =>
                    b.PrimaryHandler = FakeAdmin.CreateHandler()));
        });
    }

    public HttpClient CreateClientWithToken(string accessToken)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        return client;
    }

    /// <summary>Токен пользователя realm (контракт §4): organization/roles/groups.</summary>
    public string TokenFor(string email, string? organization, string[] roles, string[] groups,
        string services = "")
        => Authority.IssueToken(TestTokens.Build(email, organization, roles, groups, services));
}
