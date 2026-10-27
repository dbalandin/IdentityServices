# Подключение OIDC-аутентификации (Keycloak) к ASP.NET Core — .NET 10

- **Версия:** 1.0
- **Дата:** 2026-10-27
- **Статус:** проверена на `examples/demo-api` (task-05)
- **Аудитория:** команды-потребители IdentityService (.NET 10 / ASP.NET Core сервисы экосистемы)
- **Эталон клеймов:** `docs/contracts/jwt-claims.md` — интеграция пишется против контракта, а не конкретной инсталляции Keycloak

Результат инструкции: сервис с JWT bearer-валидацией против realm `rulink`, в котором работают
`[Authorize]`, `[Authorize(Roles = "…")]` и чтение клеймов контракта (`organization`, `groups`, `scope`).
Живой образец каждого фрагмента кода — `examples/demo-api` (task-04).

---

## 1. Модель интеграции (что делает ваш сервис)

Ваш API — **resource server**: он только *валидирует* приходящие access-токены, сам он OIDC-потоков
не инициирует. Поэтому:

| Сервис… | Делает | Не делает |
|---|---|---|
| проверяет подпись токена (RS256) по JWKS realm'а | ✔ | |
| проверяет `iss` (issuer) и `aud` (аудиторию, опционально) | ✔ | |
| маппит роли/клеймы в `ClaimsPrincipal` | ✔ | |
| хранит клиентский секрет | | ✘ — секрет нужен только клиентам, которые запрашивают токены (SPA, confidential-клиенты) |
| логинит пользователя (redirect на Keycloak) | | ✘ — это делает клиент-приложение (браузерный код / task-06, demopage) |
| делает logout / refresh | | ✘ — см. §6 |

Адреса dev-инсталляции (prod отличается только hostname — сервис настраивается **одним значением Authority**):

| Что | Значение (dev) |
|---|---|
| Authority / Issuer | `http://127.0.0.1:10020/realms/rulink` |
| Discovery (OIDC) | `…/.well-known/openid-configuration` — .NET забирает сам по Authority |
| JWKS (ключи подписи) | `…/protocol/openid-connect/certs` — тоже сам, включая ротацию по `kid` |
| Token endpoint | `…/protocol/openid-connect/token` |

### Клиент в realm `rulink` — какой и когда

Терминология Keycloak: *клиент* — это тот, кому **выдаются** токены. Чистому web api свой клиент не нужен:
токены выпускаются клиентам-потребителям (SPA, демо-клиент, портал), а api фигурирует в них только как
**аудитория** (`aud`). Клиент в realm нужен вашему сервису если:

1. **Хотите своё имя в `aud` токена** (строгая проверка) — для клиента-потребителя настраивается
   *audience mapper* на ваш сервис (так сделан клиент `demo-api` → `aud: ["account", "demo-api"]`,
   task-02). Ваш код тогда задаёт `Audience` и включает проверку аудитории (§3).
2. **Нужен direct access grants для интеграционных тестов** (password grant, dev/test only —
   realm `rulink` включает его только для этого).

Тип клиента для web api, если всё же создаётся: **OpenID Connect, public access-type, без секретов**
(сервис ничего не подписывает клиентом). Если же сервис должен *сам* вызывать другие сервисы от своего
имени — это отдельный confidential-клиент с service account (см. контракт §6, client_credentials).

> В фазе 1 realm `rulink` сеется скриптом `deploy/init/realm-bootstrap.sh` (task-02): клиентам-потребителям
> нужен новый audience mapper — заводится через владельца realm, а не вручную в консоли.

---

## 2. Пакеты NuGet

Один пакет — валидация JWT по discovery/JWKS realm'а:

```bash
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
```

Версия — линия 10.x (в demo-api — `10.0.1`). Остальное (авторизация, CORS) — в метапакете SDK `Microsoft.NET.Sdk.Web`.

---

## 3. Конфигурация

Секретов нет — только Authority и Audience. Значения — в `appsettings.json` + переопределение окружением
(`Identity__Authority`, `Identity__Audience`), чтобы один и тот же артефакт работал в dev/prod:

```json
// appsettings.json
{
  "Identity": {
    "Authority": "http://127.0.0.1:10020/realms/rulink",
    "Audience": "demo-api"
  }
}
```

### Program.cs — полностью

Этот листинг — работающий `examples/demo-api/Program.cs` (минус demo-endpoints):

```csharp
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

var identity = builder.Configuration.GetSection("Identity");
var authority = identity["Authority"]
    ?? throw new InvalidOperationException("Identity:Authority не задан (appsettings/env Identity__Authority)");
var audience = identity["Audience"] ?? "demo-api";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = authority;   // discovery и JWKS .NET заберёт сам (issuer, ключи, ротация kid)
        o.Audience = audience;

        // dev: Keycloak на http://127.0.0.1 без TLS → метаданные разрешаем по http.
        // prod (https-issuer): флаг остаётся true (default) — не отключать.
        var isHttp = Uri.TryCreate(authority, UriKind.Absolute, out var u) && u.Scheme != "https";
        o.RequireHttpsMetadata = !isHttp;

        o.MapInboundClaims = false;                // ← критично, см. пояснение
        o.TokenValidationParameters = new()
        {
            NameClaimType = "preferred_username",  // User.Identity.Name = email (username = email)
            ValidAudience = audience,
        };
    });

builder.Services.AddAuthorization();

// роли realm'а → ClaimTypes.Role (см. ниже раздел «Роли»)
builder.Services.AddSingleton<IClaimsTransformation, RealmRolesTransformer>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
// … endpoints
app.Run();
```

*(вычисление `RequireHttpsMetadata` — как в demo-api: http-authority → `false`, иначе `true`;
явное значение можно задать конфигом `Identity:RequireHttpsMetadata`.)*

### Зачем каждый параметр

| Параметр | Зачем |
|---|---|
| `Authority` | По нему JwtBearer скачивает discovery-документ: issuer (`iss`), JWKS-URI. Смена окружения (prod hostname) — одно значение конфига |
| `RequireHttpsMetadata = false` | Только dev: Keycloak опубликован на `http://127.0.0.1:10020`. С https-issuer не задавайте — метаданные обязаны идти по TLS |
| `MapInboundClaims = false` | .NET по умолчанию *переименовывает* входящие клеймы в legacy-имена (`ClaimTypes.*`): `email` → `…emailaddress`, `sub` → `…nameidentifier` и т.д. Отключаем: клеймы контракта (`organization`, `groups`, `scope`, `realm_access`) читаются по их настоящим именам, как в токене |
| `NameClaimType = "preferred_username"` | `MapInboundClaims=false` убирает дефолтный маппинг имени — без этой строки `User.Identity.Name` будет пустым. По контракту `preferred_username` = email пользователя |
| `Audience` / `ValidAudience` | Проверка `aud`-клейма: токен выдан в том числе вашему сервису. Требует audience mapper у клиента-эмитента (§1). Допустимо и слабее: проверять только `iss`+подпись (`ValidateAudience = false`, контракт §3) — но строгий вариант лучше |

Проверки, которые JwtBearer делает без дополнительной настройки: подпись RS256 по JWKS (с перечитыванием
ключей при неизвестном `kid`), `iss`, `exp`/`nbf` (допуск перекоса часов 5 мин — достаточно, контракт §5).

### Роли `realm_access.roles` и `RoleClaimType`

Роли realm'а Keycloak кладёт **вложенным JSON-объектом**:

```json
"realm_access": { "roles": ["user", "admin", "default-roles-rulink", …] }
```

Интуитивное `TokenValidationParameters.RoleClaimType = "realm_access.roles"` **не работает**: роль-клейм
должен быть отдельной строкой на каждый элемент, а здесь один клейм-строка со всем JSON. Правильное
решение — `IClaimsTransformation`, разворачивающая массив в набор role-клеймов (`RealmRolesTransformer.cs`
из demo-api):

```csharp
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

public sealed class RealmRolesTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is ClaimsIdentity id
            && !id.HasClaim(c => c.Type == ClaimTypes.Role)          // идемпотентность
            && principal.FindFirst("realm_access")?.Value is { } json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("roles", out var roles))
                foreach (var role in roles.EnumerateArray())
                    if (role.GetString() is { Length: > 0 } r)
                        id.AddClaim(new Claim(ClaimTypes.Role, r));
        }
        return Task.FromResult(principal);
    }
}

// Program.cs:
builder.Services.AddSingleton<IClaimsTransformation, RealmRolesTransformer>();
```

После этого работают `[Authorize(Roles = "…")]`, `User.IsInRole("admin")`, `RequireRole(…)`.

> Технические роли Keycloak (`default-roles-rulink`, `offline_access`, `uma_authorization`) тоже попадут
> в role-клеймы — это нормально, прикладная авторизация опирается только на `user`/`admin`/`auditor`
> (контракт §3: проверка — вхождение, технические игнорировать).

### CORS (если API зовут из браузера)

CORS для вашего api настраивается **на стороне api** (web-origins клиента в Keycloak — про другое:
это CORS прямых запросов браузера к самому Keycloak):

```csharp
builder.Services.AddCors(o => o.AddPolicy("spa", p => p
    .WithOrigins("http://localhost:5173")   // origin вашего SPA
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseCors("spa");        // до UseAuthentication/UseAuthorization
// …
```

В dev для SPA достаточно `WithOrigins` без credentials-токенов: SPA кладёт JWT в `Authorization`-заголовок,
cookie не нужны.

---

## 4. Использование

### Защита endpoints

```csharp
// minimal api
app.MapGet("/public", () => "ok");                                    // анонимный — не трогаем
app.MapGet("/me", (ClaimsPrincipal user) => …).RequireAuthorization(); // [Authorize]
app.MapGet("/admin", () => "admin ok")
   .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });  // [Authorize(Roles = "admin")]

// контроллеры — то же самое атрибутами
[Authorize]                          public IActionResult Me() => …;
[Authorize(Roles = "admin")]         public IActionResult Admin() => …;
[Authorize(Roles = "admin,auditor")] public IActionResult Both() => …;   // ИЛИ-семантика
[AllowAnonymous]                     public IActionResult Public() => …; // дырка в [Authorize]-контроллере
```

Поведение: нет/невалидный токен → **401**; токен валиден, но роли/клеймы не проходят → **403**.

### Чтение клеймов контракта

```csharp
app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
{
    sub             = user.FindFirst("sub")?.Value,
    name            = user.Identity?.Name,                  // preferred_username (§3, NameClaimType)
    email           = user.FindFirst("email")?.Value,
    organization    = user.FindFirst("organization")?.Value,
    groups          = user.FindAll("groups").Select(c => c.Value), // см. нюанс ниже
    services        = user.FindFirst("scope")?.Value.Split(' '),   // scope — строка через пробел
    isInAdminRole   = user.IsInRole("admin"),               // роли из RealmRolesTransformer (§3)
}))
.RequireAuthorization();
```

- `organization` — строка, у пользователя ровно одна (`docs/contracts/jwt-claims.md` §3).
- `groups` — **нюанс формата**: обработчик .NET 10 разворачивает JSON-массив в *несколько* клеймов
  `groups` (по одному на группу) — поэтому `FindAll`, а не `FindFirst`. Хелпер, устойчивый к обоим
  вариантам представления, — `examples/demo-api/KeycloakClaims.cs` (`Organization()`, `Groups()`,
  `IsInGroup()`, `Services()`, `HasServiceScope()`) — можно скопировать целиком.
- `scope` — строка через пробел; доступ к сервису — *вхождение* имени (`HasServiceScope("cryptoservice")`),
  не равенство строки. Групповые данные (паттерн «ресурс виден членам группы», фильтры в БД) — отдельная
  инструкция task-06 (`docs/integrations/group-data.md`, готовится) и `/group-data` в demo-api.

### Политики вместо ролей (когда условие сложнее)

```csharp
builder.Services.AddAuthorization(o => o.AddPolicy("OrgaOnly",
    p => p.RequireClaim("organization", "orga")));
// …
app.MapGet("/orga", () => "ok").RequireAuthorization("OrgaOnly");
```

---

## 5. Токен для тестов (dev)

В realm `rulink` включён **direct access grants** (password grant) — только для dev/тестов; браузерные
приложения используют authorization code flow (см. task-06/demopage). Seeded-пользователи — контракт §4,
пароль у всех один — из `INIT_DEMO_PASSWORD` в `.env` (значение не печатать).

| Пользователь | Организация | Роли | Группы |
|---|---|---|---|
| `alice@orga.example.com` | orga | user, **admin** | orga-managers |
| `bob@orga.example.com` | orga | user | orga-team-a |
| `carol@orga.example.com` | orga | user, **auditor** | orga-team-a |
| `dave@orgb.example.com` | orgb | user, admin | orgb-managers |
| `erin@orgb.example.com` | orgb | user | orgb-team-b |

```bash
# пароль берём из .env, токен в консоль не печатаем
set -a; . ./.env; set +a

TOKEN=$(curl -s http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/token \
  -d grant_type=password \
  -d client_id=demo-api \
  -d username=alice@orga.example.com \
  -d password="$INIT_DEMO_PASSWORD" \
  | jq -r .access_token)

curl -s http://127.0.0.1:10010/me -H "Authorization: Bearer $TOKEN" | jq .
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:10010/admin -H "Authorization: Bearer $TOKEN"  # 200
# тем же способом возьмите токен bob@… — /admin ответит 403 (нет роли admin)
```

Сверить состав клеймов живого токена с контрактом можно готовым скриптом: `deploy/verify.sh [username]`.

---

## 6. Logout и refresh token

Access-токен — **stateless JWT**: сервис проверяет подпись и `exp`, состояние сессии не спрашивает.

**Сервис не делает ничего**: нет ни logout-endpoint'а, ни инвалидации — токен действителен до `exp`
(5 минут, контракт §5) независимо от logout'а пользователя. Поэтому lifetime и короткий.

**Всё делает клиент** (SPA/mobile/bff):

- **Refresh** — по истечении access-токена (api ответил 401) повторить `POST …/token`:

  ```bash
  curl -s http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/token \
    -d grant_type=refresh_token \
    -d client_id=demo-api \
    -d refresh_token="$REFRESH_TOKEN"   # из ответа password-grant/предыдущего refresh
  ```

  Refresh-токен живёт в рамках сессии: idle 30 минут, абсолютный максимум 10 часов (контракт §5);
  многоразовый (Revoke Refresh Token в realm выключен) — но клиент обязан хранить его безопасно
  (не в localStorage без необходимости) и слать только на token endpoint по TLS.
- **Logout** — redirect на `…/protocol/openid-connect/logout?post_logout_redirect_uri=…&id_token_hint=…`:
  Keycloak убивает сессию и refresh-токены. Уже выданный access-токен продолжит работать до `exp` —
  это ожидаемо; при необходимости немедленного отзыва клиент шлёт refresh-токен на
  `…/protocol/openid-connect/revoke` (остальные access-токены всё равно доживут ≤5 минут).

Если сервису нужна сессионная семантика «токен отозван мгновенно» — это introspection/Blacklist, в фазе 1
не используется (обсуждение — `docs/QUESTIONS.md`).

---

## 7. Проверка инструкции: demo-api из чистого клона

Каждый шаг воспроизводим без недокументированных действий. Нужны: docker, .NET 10 SDK, curl, jq.

```bash
# 1. Окружение (один раз): скопировать и заполнить .env
cp .envexample .env && $EDITOR .env        # KC_BOOTSTRAP_ADMIN_*, KC_DB_*, INIT_DEMO_PASSWORD, …

# 2. Keycloak + PostgreSQL (внешняя БД из .env), ждать статуса healthy
cd deploy && docker compose --env-file ../.env up -d && cd ..

# 3. Realm rulink: клиенты, роли, организации, группы, 5 seeded-пользователей
deploy/init-realm.sh

# (опционально) сверка живого токена с контрактом JWT
deploy/verify.sh alice@orga.example.com

# 4. Демо-сервис (порт 10010; в csproj задан ApplicationUrl, но у dotnet run без
#    launchSettings.json свой дефолт — URL указываем явно)
cd examples/demo-api && dotnet run --urls http://127.0.0.1:10010
```

Ожидаемые результаты (в отдельном терминале, токен — по §5):

| Проверка | Ожидание |
|---|---|
| `curl http://127.0.0.1:10010/public` | **200**, json `{"status":"ok","service":"demo-api",…}` |
| `curl http://127.0.0.1:10010/me` (без токена) | **401** |
| `curl …/me -H "Authorization: Bearer $TOKEN"` (alice или bob) | **200** + клеймы контракта (organization, groups, roles, scope) |
| `…/admin` с токеном **alice** (admin) | **200** |
| `…/admin` с токеном **bob** (user) | **403** |
| `…/audit` с токеном **carol** (auditor) | **200**; с alice — **403** |
| `…/group-data` с **bob** и **carol** (одна группа) | **200**; с **alice**/**erin** — **403** |

Быстрая проверка без Keycloak (мок OIDC-authority вместо реалма — CI-friendly):

```bash
dotnet test examples/demo-api/tests/DemoApi.Tests   # сценарии таблицы выше, RS256 + клеймы контракта
```

---

## 8. Типовые проблемы

| Симптом | Причина | Решение |
|---|---|---|
| 401 на всё, в логах `IDX20803` (не получил метаданные) | Keycloak не поднят / Authority неверен | проверьте `curl $AUTHORITY/.well-known/openid-configuration`; `Identity__Authority` целиком с `/realms/rulink` |
| 401, `IDX10205` issuer mismatch | issuer не совпадает с Authority (порт!) | в compose задан `KC_HOSTNAME_PORT=10020`, issuer = `http://127.0.0.1:10020/realms/rulink`; Authority сервиса должен совпадать до символа |
| 401, `IDX10223` audience invalid | у клиента-эмитента нет audience mapper на ваш сервис | §1: audience mapper на api-клиент, либо (осознанно) `ValidateAudience = false` |
| 403 там, где роли должны пропускать | нет `RealmRolesTransformer` / не зарегистрирован | §3 (раздел «Роли»); проверить `builder.Services.AddSingleton<IClaimsTransformation, …>()` |
| `User.Identity.Name == null` | не задан `NameClaimType` при `MapInboundClaims=false` | `NameClaimType = "preferred_username"` |
| Клейм `organization`/`groups` не находится | включён маппинг клеймов | `o.MapInboundClaims = false` |
| `groups` вернул только одну группу | `FindFirst` вместо `FindAll` | §4: JSON-массив разворачивается в N клеймов |
| 401 сразу после успешного логина на клиенте | access-токен истёк (5 мин), клиент не сделал refresh | §6: refresh на 401 |
| Разработка без сети до Keycloak | backchannel не может скачать JWKS | метаданные кэшируются; для unit-тестов — мок-authority (`examples/demo-api/tests`, `MockOidcAuthority`) |

## 9. Отличия production

- Authority — публичный https-hostname (`https://id.rulink.io/realms/rulink`); `RequireHttpsMetadata`
  остаётся default(true) — не отключать.
- Конфигурация — только через env (`Identity__Authority` / `Identity__Audience`), секретов у
  resource-server нет по определению.
- Строгая проверка `aud` обязательна (каждый сервис — своя аудитория + audience mapper).
- Логи: не логировать токены, e-mail, телефоны (правило AGENTS.md).

---

## Ссылки

- Контракт JWT (эталон клеймов, lifetime, client_credentials): `docs/contracts/jwt-claims.md`
- Рабочий образец: `examples/demo-api` (`Program.cs`, `RealmRolesTransformer.cs`, `KeycloakClaims.cs`)
- Запуск окружения: `deploy/` (compose, `init-realm.sh`, `verify.sh`) — task-01/02
- Групповые данные (P3): `docs/tasks/task-06.md` → `docs/integrations/group-data.md`
- Задача инструкции: `docs/tasks/task-05.md`; план §5.5, PRD FR 4
