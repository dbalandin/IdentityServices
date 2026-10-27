# demopage — .NET 10 Razor demo-сайт (task-04, задание 2)

Минимальное ASP.NET Core **.NET 10** Razor Pages приложение: OIDC-логин через
IdentityService (Keycloak realm `rulink`), демонстрация страниц
`[Authorize]` / `[AllowAnonymous]` и вызов `demo-api` с access token пользователя.

- `/Index` — публичная страница (`[AllowAnonymous]`);
- `/Private` — защищённая (`[Authorize]`): без сессии — **редирект на IdentityService**,
  после входа — клеймы пользователя (sub, email, organization, groups, roles, scope)
  и вызов `GET demo-api /me` с access token (`SaveTokens=true`);
- `/Logout` — logout: локальная cookie-сессия + Single Logout на IdP (`end_session_endpoint`).

## Запуск (dev)

Предусловия: Keycloak realm `rulink` на `http://127.0.0.1:10020` (deploy/), .NET 10 SDK.

```bash
cd examples/demopage
dotnet run            # http://127.0.0.1:10040; профиль Development (launchSettings.json)
```

Открыть http://127.0.0.1:10040 → «Защищённая» → редирект на Keycloak → вход
seeded-пользователем (напр. `bob@orga.example.com`, пароль `INIT_DEMO_PASSWORD` из env)
→ возврат на /Private с клеймами. Для блока «вызов demo-api» поднять `examples/demo-api`.

## Конфигурация

| Что | Prod (appsettings.json) | Dev (appsettings.Development.json) |
|---|---|---|
| Authority | `https://id.rulink.io/realms/rulink` | `http://127.0.0.1:10020/realms/rulink` |
| ClientId | `demo-api` (public, PKCE) | тот же |
| RequireHttpsMetadata | true | false |
| demo-api | `Identity:DemoApi` = `http://127.0.0.1:10010` | тот же |

Env-переопределения: `Identity__Authority`, `Identity__ClientId`, `Identity__DemoApi`.
Production: `ASPNETCORE_ENVIRONMENT=Production dotnet run --no-launch-profile`
(для nginx настроить forwarded headers, чтобы redirect_uri формировался https).

## Артефакты, нужные чтобы токен работал с сайтом (задание 2.4)

1. **Клиент Keycloak `demo-api`** (создаётся `deploy/init/realm-bootstrap.sh`):
   - `publicClient=true`, `standardFlowEnabled=true` (authorization code flow);
   - **redirect URI сайта** `<site>/signin-oidc` должен входить в `redirectUris` клиента:
     dev `http://127.0.0.1:10040/signin-oidc` покрыт wildcard `http://127.0.0.1:*/*`;
     для прод-URL добавить в `.env`: `DEMO_API_REDIRECT_URIS=http://127.0.0.1:*/*,http://localhost:*/*,https://<site>/*`
     (композиция передаёт в контейнер, bootstrap идемпотентно обновит клиента)
     и перезапустить `deploy/init-realm.sh`;
   - web origins: `+` (все redirect-URIs), прод — `DEMO_API_WEB_ORIGINS`;
   - аудитория: audience mapper `demo-api` → access token `aud=[account, demo-api]`
     (именно поэтому токен сайта проходит `ValidateAudience` в demo-api без доп. настроек).
2. **Scopes**: `openid profile email`; группы/организация/services — дефолтные
   client scope realm'а (task-02), отдельно запрашивать не нужно.
3. **Приложение** (Program.cs): Authority, ClientId, `ResponseType=code`, `UsePkce=true`
   (public-клиент — секрета нет), `SaveTokens=true`, `MapInboundClaims=false`,
   `NameClaimType=preferred_username`.
4. **Сайт доступен по URL, совпадающему с redirect URI** (dev: 127.0.0.1:10040),
   иначе Keycloak отклонит колбэк (`Invalid parameter: redirect_uri`).

Проверка редиректа без браузера:

```bash
curl -s -o /dev/null -w "%{redirect_url}\n" http://127.0.0.1:10040/Private
# → http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/auth?client_id=demo-api
#   &redirect_uri=http%3A%2F%2F127.0.0.1%3A10040%2Fsignin-oidc&response_type=code
#   &scope=openid%20profile%20email&code_challenge=…&code_challenge_method=S256…
```
