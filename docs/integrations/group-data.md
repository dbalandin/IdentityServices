# Групповые данные в сервисах — доступ «всем пользователям группы» (.NET 10)

- **Версия:** 1.0
- **Дата:** 2026-10-28
- **Статус:** проверена на `examples/demo-api` (endpoint `/group-data`, suite `DemoApi.Tests` — task-04/06)
- **Аудитория:** команды-потребители IdentityService (.NET 10 / ASP.NET Core сервисы экосистемы)
- **Эталон клеймов:** `docs/contracts/jwt-claims.md` — интеграция пишется против контракта, а не конкретной инсталляции Keycloak
- **Базовая инструкция:** `docs/integrations/dotnet-oauth.md` (task-05) — настройка JwtBearer/ролей; здесь только групповая авторизация и данные

Результат инструкции: сервис, в котором ресурс (документ, отчёт, запись) виден **всем пользователям
группы-владельца и только им** — на уровне и endpoint-авторизации, и фильтрации запросов к БД.
Живой образец — `examples/demo-api` endpoint `/group-data` (task-04).

---

## 1. Постановка

У пользователя есть группы (клейм `groups`), организация (`organization`) и набор сервисов
(`scope`). Типовые требования к данным сервиса:

| Требование | Чем закрываем |
|---|---|
| «Документ виден всем членам команды X и никому больше» | клейм `groups` + связка `ресурс ↔ группа` в БД сервиса (§3) |
| «Раздел виден всей организации» | клейм `organization` (§4) |
| «Функциональность доступна пользователям сервиса Y» | клейм `scope` (вхождение имени сервиса) — см. `dotnet-oauth.md` §4 |

Правило фазы 1: **группы и организация всегда читаются из токена** (источник истины — Keycloak),
а не из query-параметров/тела запроса. Клиент не сообщает серверу, в какой он группе, — сервер
знает это из подписанного JWT.

## 2. Чтение клеймов

Валидация токена настраивается по `dotnet-oauth.md` (Authority + JWKS, `MapInboundClaims = false`).
Дальше — три клейма контракта (`docs/contracts/jwt-claims.md` §3):

| Клейм | Тип в токене | Как читать в ASP.NET Core |
|---|---|---|
| `organization` | string (alias, у пользователя ровно одна) | `user.FindFirst("organization")?.Value` |
| `groups` | array of string (имена групп **без пути**) | `user.FindAll("groups")` — **FindAll, не FindFirst** (нюанс ниже) |
| `scope` | string, значения через пробел | `user.FindFirst("scope")?.Value.Split(' ')` — проверка доступа — **вхождение**, не равенство |

Нюанс `groups`: обработчик .NET 10 разворачивает JSON-массив примитивов в *несколько* клеймов
`groups` (по одному на группу); в старом представлении — один клейм со значением-JSON-массивом.
Надёжный хелпер — `examples/demo-api/KeycloakClaims.cs` (можно скопировать целиком):

```csharp
// organization — строка
public static string? Organization(this ClaimsPrincipal user)
    => user.FindFirst("organization")?.Value;

// groups — устойчиво к обоим форматам представления
public static IReadOnlyList<string> Groups(this ClaimsPrincipal user)
{
    var claims = user.FindAll("groups").ToList();
    if (claims.Count == 0) return [];
    if (claims.Count == 1 && claims[0].Value.StartsWith("["))
        return JsonSerializer.Deserialize<string[]>(claims[0].Value) ?? [];
    return claims.Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
}

// scope — вхождение имени сервиса, не равенство строки
public static bool HasServiceScope(this ClaimsPrincipal user, string service)
    => user.FindFirst("scope")?.Value.Split(' ').Contains(service) ?? false;
```

`scope` кроме сервисов содержит стандартные OIDC-значения (`openid`, `email`, `profile`) — это
нормально, фильтруйте по конкретному имени сервиса.

## 3. Паттерн «данные доступны всем пользователям группы»

Идея: у ресурса сервиса есть владелец-группа (или несколько). Пользователь видит ресурс ⇔
пересечение его `groups` с группами-владельцами ресурса непусто. Проверка выполняется **дважды**:
на endpoint (быстрый отказ) и в запросе к БД (истина последней инстанции — endpoint-проверка
не заменяет фильтр).

### 3.1. Схема БД сервиса

**Вариант A — одна группа-владелец у ресурса** (проще, покрывает большинство сценариев):

```sql
-- PostgreSQL
CREATE TABLE documents (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title        TEXT NOT NULL,
    owner_group  TEXT NOT NULL,          -- имя группы из клейма groups (без пути)
    -- … поля сервиса
    CONSTRAINT uq_documents_owner CHECK (owner_group <> '')
);
CREATE INDEX ix_documents_owner_group ON documents (owner_group);
```

**Вариант B — много групп на ресурс** (таблица связки `resource ↔ group`):

```sql
CREATE TABLE documents (
    id    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title TEXT NOT NULL
    -- …
);

CREATE TABLE document_groups (
    document_id BIGINT NOT NULL REFERENCES documents (id) ON DELETE CASCADE,
    group_name  TEXT   NOT NULL,
    PRIMARY KEY (document_id, group_name)
);
CREATE INDEX ix_document_groups_group ON document_groups (group_name);
```

Хранится **имя группы** — именно оно приходит в клейме `groups` (плоские имена без пути,
mapper `full.path=false`, task-02). Внешний ключ на Keycloak невозможен (группы живут в IdP),
поэтому целостность имён поддерживается миграцией при переименовании (§5.2).

### 3.2. Фильтр запроса (SQL)

Группы пользователя — **параметр** запроса (никогда не конкатенация строк):

```sql
-- вариант A (колонка)
SELECT id, title
FROM documents
WHERE owner_group = ANY(@userGroups);          -- @userGroups — text[] из токена

-- вариант B (таблица связки)
SELECT d.id, d.title
FROM documents d
JOIN document_groups dg ON dg.document_id = d.id
WHERE dg.group_name = ANY(@userGroups);
```

`ANY(@array)` — postgres-идиома для «значение ∈ массив»; в SQL Server эквивалент —
`owner_group IN (@g1, @g2, …)` (список параметризуется драйвером/Npgsql автоматически).

Пустой `groups` (пользователь без групп) → пустой массив → запрос вернёт 0 строк; отдельной
ветки не нужно.

### 3.3. Фильтр запроса (EF Core)

```csharp
// Сущности (вариант B)
public class Document
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public List<DocumentGroup> AllowedGroups { get; set; } = [];
}

public class DocumentGroup
{
    public long DocumentId { get; set; }
    public string GroupName { get; set; } = "";
}

// Запрос: группы — из токена, переводится в owner_group = ANY(...) / IN (…)
IReadOnlyList<string> userGroups = user.Groups();

var visible = await db.Documents
    .Where(d => d.AllowedGroups.Any(g => userGroups.Contains(g.GroupName)))
    .OrderByDescending(d => d.Id)
    .Take(50)
    .ToListAsync(ct);

// Вариант A (колонка):
var visibleA = await db.Documents
    .Where(d => userGroups.Contains(d.OwnerGroup))
    .ToListAsync(ct);
```

`Contains` по `List<>` параметру EF Core параметризует и переводит в `ANY`/`IN` — SQL-инъекций
нет, план использует индекс из §3.1.

### 3.4. Авторизация endpoint по группе

Endpoint должен вернуть **403** аутентифицированному не-члену и **401** без токена. Два способа:

**а) явная проверка в handler** — так сделано в `examples/demo-api` `/group-data` (живой образец):

```csharp
// группы-владельцы ресурса — конфиг сервиса (env: GROUPDATA__ALLOWEDGROUPS)
var allowedGroups = (builder.Configuration["GroupData:AllowedGroups"] ?? "orga-team-a")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

app.MapGet("/group-data", (ClaimsPrincipal user) =>
{
    var userGroups = user.Groups();                       // ← из токена (KeycloakClaims.cs)
    var visible = allowedGroups.Intersect(userGroups, StringComparer.Ordinal).ToList();
    return visible.Count > 0
        ? Results.Ok(new { resourceGroups = visible, data = LoadByGroups(visible) })
        : Results.Forbid();                               // аутентифицирован, но не член → 403
})
.RequireAuthorization();                                  // без токена → 401
```

**б) политика авторизации** (когда группа-владелец известна статически, без БД):

```csharp
builder.Services.AddAuthorization(o => o.AddPolicy("TeamAOnly", p =>
    p.RequireAssertion(ctx => ctx.User.Groups()
        .Contains("orga-team-a", StringComparer.Ordinal))));

app.MapGet("/team-a/report", () => "…").RequireAuthorization("TeamAOnly");
```

**в) ресурс-специфичная проверка** (группа-владелец живёт в БД): сначала прочитать запись,
сравнить `owner_group` с `user.Groups()`, при несовпадении — `Results.Forbid()`. Один и тот же
инвариант «пересечение групп» повторяется и здесь, и в фильтре списка (§3.2/3.3) — это осознанно:
фильтр нельзя «оптимизировать» endpoint-проверкой, endpoint не видит записей, которых нет в выдаче.

### 3.5. Чего не делать

- ❌ принимать группы из query/body («покажи как admin группы X») — только из токена;
- ❌ сравнивать весь массив `groups` на равенство — только вхождение/пересечение;
- ❌ `like`-матчинг имён групп — точное равенство строк (`ordinal`);
- ❌ полагаться на роль `admin` как на «видит всё» — админ другой организации не член группы
  (межорганизационный доступ — отдельное решение, фазой 1 не предусмотрен).

## 4. Вариант с организацией: когда фильтровать по `organization`

Клейм `organization` — строка, у пользователя ровно одна организация. Фильтруйте по ней, когда
владелец данных — **вся организация**, а не отдельная группа:

- общие справочники/шаблоны организации: `WHERE organization = @userOrganization`;
- «данные организации + рабочая группа внутри»: фильтр по организации **и** группам одновременно
  (группы фазы 1 именуются с префиксом `<org>-*`, но изоляция — не строковой проверкой префикса
  в сервисе-потребителе, а парой клеймов `organization` + `groups` из одного токена);

```csharp
var org = user.Organization();                  // "orga"
var groups = user.Groups();                     // ["orga-team-a"]

// видны: записи этой организации И входящие в группы пользователя
var visible = await db.Documents
    .Where(d => d.Organization == org
             && d.AllowedGroups.Any(g => groups.Contains(g.GroupName)))
    .ToListAsync(ct);
```

- `scope` (`HasServiceScope("cadastralservice")`) отвечает на «какой сервис разрешён», а не «чьи
  данные» — для фильтрации записей его не использовать.

Не смешивать: если ресурс объявлен групповым — организация сама по себе права не даёт (пользователь
той же организации, но другой группы, — 403; см. сценарий `alice` в §6).

## 5. Ограничения

### 5.1. Уникальность имён групп

`groups` — плоские имена в масштабе **всего realm** (не в рамках организации). В фазе 1 группы
создаются с префиксом организации (`orga-team-a`, `orgb-team-b` — соглашение task-02, его же
поддерживает админ-портал task-07), поэтому:

- коллизий «одинаковое имя в двух организациях» нет, пока префикс соблюдается;
- сервис-потребитель может полагаться на имя как на ключ, но **не должен** разбирать имя
  (выделять «организацию» из префикса) — для организации есть отдельный клейм;
- состав групп пользователя виден в токене целиком: членство в нескольких группах даёт доступ
  ко всем ресурсам каждой из них (объединение, не пересечение).

### 5.2. Переименование группы

Имя группы — значение клейма и ключ в БД сервиса. При переименовании группы в Keycloak старые
токены (до `exp`, ≤ 5 мин, контракт §5) продолжают нести старое имя — короткое окно
расхождения. Поэтому:

1. переименование — редкая операция: создать новую группу, перевести членов, затем удалить старую;
2. в момент перевода — миграция данных сервиса: `UPDATE document_groups SET group_name = @new WHERE group_name = @old` (+ колонку `owner_group` в варианте A);
3. пока живы старые токены, сервис видит «пустую» новую группу — на практике окно закрывается
   временем жизни access-токена; критичные сервисы могут дополнительно сверяться с Admin REST
   (фаза 1 не требует).

### 5.3. Прочее

- `groups` может отсутствовать/быть пустым (пользователь без групп) — код обязан это переживать
  (хелпер `Groups()` возвращает `[]`);
- имена групп в токене — без пути (`/` в значениях не встречается, контракт §8); если ваш сервис
  строит иерархию — делайте это в своей модели, а не из клейма.

## 6. Проверка на demo-api (P3-сценарий)

Развёртывание — `dotnet-oauth.md` §7 (compose → init → demo-api). Рабочая пара — `bob`+`carol`
(одна группа `orga-team-a`, контракт §4); негатив — `alice` (другая группа той же организации),
`dave`/`erin` (другая организация).

```bash
set -a; . ./.env; set +a
API=http://127.0.0.1:10010

# 401 без токена
curl -s -o /dev/null -w '%{http_code}\n' $API/group-data                       # → 401

# 200: члены orga-team-a
for u in bob@orga.example.com carol@orga.example.com; do
  TOKEN=$(curl -s http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/token \
    -d grant_type=password -d client_id=demo-api \
    -d username=$u -d password="$INIT_DEMO_PASSWORD" | jq -r .access_token)
  curl -s -o /dev/null -w "%{http_code} $u\n" $API/group-data -H "Authorization: Bearer $TOKEN"
done                                                                            # → 200 bob, 200 carol

# 403: alice (orga, группа orga-managers), dave и erin (orgb)
for u in alice@orga.example.com dave@orgb.example.com erin@orgb.example.com; do
  TOKEN=$(curl -s http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/token \
    -d grant_type=password -d client_id=demo-api \
    -d username=$u -d password="$INIT_DEMO_PASSWORD" | jq -r .access_token)
  curl -s -o /dev/null -w "%{http_code} $u\n" $API/group-data -H "Authorization: Bearer $TOKEN"
done                                                                            # → 403 ×3
```

Тот же сценарий зашит в автотесты `examples/demo-api/tests/DemoApi.Tests`
(`GroupData_Accessible_To_Members_Of_Same_Group`, `GroupData_Forbidden_For_Non_Members`):

```bash
dotnet test examples/demo-api/tests/DemoApi.Tests   # 28/28 PASS (2026-10-28, task-06)
```

Живой прогон против Keycloak (compose + init) — как в task-01…05: остаточная проверка владельца
(docker-сокет в среде агента недоступен); матрица ожиданий идентична suite.

## 7. Чек-лист внедрения в свой сервис

- [ ] JwtBearer + `MapInboundClaims=false` по `dotnet-oauth.md` §3
- [ ] скопирован `KeycloakClaims.cs` (`Organization/Groups/IsInGroup/Services/HasServiceScope`)
- [ ] схема БД: колонка `owner_group` или таблица связки `*_groups` + индекс по имени группы (§3.1)
- [ ] фильтр чтения — `WHERE … = ANY(@userGroups)` / EF Core `Contains` (§3.2–3.3)
- [ ] endpoint: `RequireAuthorization` + проверка пересечения групп, не-члену — 403 (§3.4)
- [ ] группы только из токена; сравнение — точное, ordinal (§3.5)
- [ ] решено, где нужен фильтр по `organization` (§4) — и он не подменяет групповой
- [ ] процесс переименования групп описан (§5.2)
- [ ] интеграционные тесты: член группы 200, той же организации другой группы — 403, другой
      организации — 403, без токена — 401 (по образцу `DemoApi.Tests`)
