# demopage — .NET 10 Razor demo-сайт (task-04, задание 2)

Минимальное ASP.NET Core **.NET 10** Razor Pages приложение: OIDC-логин через Keycloak
(realm `rulink`), демонстрация страниц `[Authorize]` / `[AllowAnonymous]` и вызов `demo-api`
с access token пользователя.

Аутентификация — **библиотека `IdentityService`** (`AddRulinkIdentityService`, режим `Cookie`;
выход — `MapRulinkAccount`). Собственной реализации в проекте нет: настройка cookie/OIDC-схем
(authorization code + PKCE), диспетчер «[Authorize] без сессии → редирект на логин», эндпоинт выхода
и хелперы клеймов — в библиотеке (её README — `IdentityService/README.md`).

- `/Index` — публичная страница (`[AllowAnonymous]`);
- `/Private` — защищённая (`[Authorize]`): без сессии — **редирект на Keycloak**,
  после входа — клеймы пользователя (sub, email, organization, groups, roles, services)
  и вызов `GET demo-api /me` с access token (`SaveTokens=true`);
- `/Account/Logout` (`GET`/`POST` — эндпоинт библиотеки): удаление cookie + завершение сессии
  в Keycloak (`end_session_endpoint`), возврат на `/`. Кнопка «Выйти» в шапке — POST-форма на этот путь.

## Запуск (dev)

Предусловия: Keycloak realm `rulink` на `http://127.0.0.1:10020` (`deploy/`), .NET 10 SDK,
каталог библиотеки — рядом с репозиторием (`../IdentityService/IdentityService/IdentityService.csproj`;
тот же относительный путь в `examples/example.slnx`).

```bash
cd examples/demopage
dotnet run            # http://127.0.0.1:10040; профиль Development (launchSettings.json)
```

Открыть http://127.0.0.1:10040 → «Защищённая» → редирект на Keycloak → вход
seeded-пользователем (напр. `bob@orga.example.com`, пароль `INIT_DEMO_PASSWORD` из env)
→ возврат на /Private с клеймами. Для блока «вызов demo-api» поднять `examples/demo-api`.

## Конфигурация

Секция `Identity` — модель библиотеки `IdentityConfiguration` (в окружении — `IDENTITY__…`).

| Что | Prod (appsettings.json) | Dev (appsettings.Development.json) | env |
|---|---|---|---|
| Authority | `https://id.rulink.io/realms/rulink` | `http://127.0.0.1:10020/realms/rulink` | `IDENTITY__AUTHORITY` |
| Mode | `Cookie` | тот же | `IDENTITY__MODE` |
| ClientId | `demo-api` (public, PKCE) | тот же | `IDENTITY__CLIENTID` |
| RequireHttpsMetadata | true | false | `IDENTITY__REQUIREHTTPSMETADATA` |
| demo-api | `Identity:DemoApi` = `http://127.0.0.1:10010` | тот же | `IDENTITY__DEMOAPI` |

- `Mode = Cookie` — сайт с чистым UI: JWT не принимается; запрос без сессии на любом маршруте
  (даже с заголовком `Authorization: Bearer`) уводит на логин Keycloak.
- `Identity:DemoApi` — настройка самого демо (адрес demo-api для блока «вызов demo-api»);
  лежит в той же секции, библиотекией игнорируется.
- Секрета нет: клиент `demo-api` — public (authorization code + PKCE). Для confidential-клиента
  библиотека принимает `IDENTITY__CLIENTSECRET` (только из окружения).
- Прочие ключи (`CallbackPath`, `CookieName`, `CookieLifetime`, `SaveTokens`, `LogoutPath`…) —
  в README библиотеки. Ошибки конфигурации (нет `Authority`/`ClientId`…) падают при старте.

Production: `ASPNETCORE_ENVIRONMENT=Production dotnet run --no-launch-profile`
(за nginx — forwarded headers, чтобы `redirect_uri` формировался https; на стенде —
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`).

## Артефакты, нужные чтобы токен работал с сайтом (задание 2.4)

1. **Клиент Keycloak `demo-api`** (создаётся `deploy/keycloak-rulink/init/realm-bootstrap.sh`):
   - `publicClient=true`, `standardFlowEnabled=true` (authorization code flow);
   - **redirect URI сайта** `<site>/signin-oidc` должен входить в `redirectUris` клиента:
     dev `http://127.0.0.1:10040/signin-oidc` входит в дефолт `http://127.0.0.1/*,http://localhost/*`
     (в шаблоне нет порта: Keycloak сверяет scheme, host и путь; шаблон с wildcard-портом `http://127.0.0.1:*/*`
     в Keycloak 26 не совпадает ни с одним URI — `Invalid parameter: redirect_uri`);
     для прод-URL добавить в `.env`: `DEMO_API_REDIRECT_URIS=http://127.0.0.1/*,http://localhost/*,https://<site>/*`
     (композиция передаёт в контейнер, bootstrap идемпотентно обновит клиента)
     и перезапустить init-realm.sh;
   - **post logout redirect URI**: после выхода библиотека возвращает на `/` — URL сайта
     (например `https://<site>/*`) должен входить в redirect URIs клиента (Keycloak 26: при пустом
     «Valid post logout redirect URIs» используются они);
   - web origins: `+` (все redirect-URIs), прод — `DEMO_API_WEB_ORIGINS`;
   - аудитория: audience mapper `demo-api` → access token `aud=[account, demo-api]`
     (именно поэтому токен сайта проходит проверку `Audience` в demo-api без доп. настроек).
2. **Scopes**: `openid profile email`; группы/организация/services/realm-роли — дефолтные
   client scope realm'а (task-02), отдельно запрашивать не нужно. Клеймы сессии: обработчик OIDC строит
   principal из **ID-токена**, а недостающие клеймы контракта (`organization`, `services`, `realm_access`…)
   библиотека довносит из access-токена (контракт §5.1) — таблица на `/Private` полная на любом realm.
   Bootstrap realm дополнительно включает `id.token.claim=true` у мапперов `realm roles`, `organization`,
   `services` (клеймы есть и в самом ID-токене); после изменения мапперов нужно перелогиниться — состав
   клеймов фиксируется в cookie при входе.
3. **Приложение** — `AddRulinkIdentityService` в Program.cs: authorization code + PKCE,
   `SaveTokens=true`, `MapInboundClaims=false`, `NameClaimType=preferred_username` — настройки
   библиотеки.
4. **Сайт доступен по URL, совпадающему с redirect URI** (dev: 127.0.0.1:10040),
   иначе Keycloak отклонит колбэк (`Invalid parameter: redirect_uri`).

Проверка редиректа без браузера:

```bash
curl -s -o /dev/null -w "%{redirect_url}\n" http://127.0.0.1:10040/Private
# → http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/auth?client_id=demo-api
#   &redirect_uri=http%3A%2F%2F127.0.0.1%3A10040%2Fsignin-oidc&response_type=code
#   &scope=openid%20profile%20email&code_challenge=…&code_challenge_method=S256…
curl -s -o /dev/null -w "%{redirect_url}\n" -X POST http://127.0.0.1:10040/Account/Logout
# → http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/logout?post_logout_redirect_uri=…
```

## Тесты (без Keycloak и demo-api)

`tests/Demopage.Tests` — TestServer с приложением как есть и моками: discovery, JWKS и token endpoint —
`MockOidcAuthority` (backchannel обработчика OIDC; подписывает RS256-токены), `/me` — `MockDemoApi`;
cookie-сессия тестового пользователя создаётся тестовым входом `/test/signin` (IStartupFilter, код demopage не меняет).

```bash
dotnet test examples/demopage/tests/Demopage.Tests   # 19 тестов
```

Проверяют: `/Index` анонимно; `/Private` без сессии → 302 на Keycloak (`client_id=demo-api`,
`redirect_uri=…/signin-oidc`, PKCE, scopes) и игнорирует `Bearer` (режим Cookie); с сессией → клеймы
и вызов demo-api с `access_token` из сессии (без токена — сообщение об этом); `/Account/Logout` →
302 на `end_session_endpoint` + удаление cookie, GET и POST; форма выхода в layout; старая страница
`/Logout` удалена. Те же проверки выхода/логина — в окружении Production (как на стенде).

`OidcCallbackTests` — настоящий OIDC-вход без браузера: challenge → колбэк `/signin-oidc` → обмен кода
в token endpoint мока (PKCE, `client_id=demo-api`, без секрета). ID-токен мока — как у Keycloak без
`id.token.claim` у мапперов (профиль + `groups`), access-токен — весь контракт: `/Private` показывает
и `organization`, и роли, и services (довнесены из access-токена), demo-api вызывается с полученным
access-токеном; то же — в окружении Production.

## Образ (task-11)

Библиотека подключена `ProjectReference` на каталог вне репозитория, поэтому при сборке образа
он передаётся именованным контекстом (BuildKit; команда — из корня репозитория):

```bash
docker build --build-context identityservice=../IdentityService/IdentityService \
  --provenance=false --sbom=false \
  -t cr.yandex/crp9et2dv05vocv8pagt/idptestappweb:latest examples/demopage
```

Деплой — `deploy/idp-test-appweb/` (https://idp-test.rulink.io).
