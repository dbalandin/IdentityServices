# tests/ — интеграционные тесты живого окружения (task-08)

Проверка ключевых инвариантов фазы 1 против **живого** Keycloak (+ портал):

1. **Контракт JWT** (`JwtContractTests`) — password grant seeded-пользователей, сверка с эталоном
   `docs/contracts/jwt-claims.md` (§8: состав клеймов, значения §4, подпись RS256 по JWKS realm'а).
2. **Орг-изоляция портала** (`PortalIsolationTests`) — admin org A видит только свою организацию;
   операции с пользователями org B → 403; создание пользователя своей организации → 201.
3. **Безопасность** (`SecurityTests`) — 5 неверных паролей → временная блокировка;
   неверный логин и несуществующий логин неотличимы (ошибка + тайминг);
   forgot-password не раскрывает существование username.

Тесты **сами пропускаются** (xUnit Skip), если окружение не поднято или нет `INIT_DEMO_PASSWORD` —
suite зелёный в CI/среде без docker; полный прогон — на машине с живым окружением.

## Запуск из чистого состояния (DoD task-08)

```bash
# 1) окружение
cd deploy
cp .envexample ../.env                # заполнить (один раз)
docker compose --env-file ../.env up -d
./init-realm.sh                       # realm rulink + seeded-пользователи (task-02)

# 2) портал (второй терминал)
cd src/portal
set -a; . ../../.env; set +a          # PORTAL_CLIENT_SECRET / INIT_DEMO_PASSWORD
dotnet run --urls http://127.0.0.1:10030

# 3) тесты (третий терминал, из корня репо)
set -a; . ./.env; set +a              # INIT_DEMO_PASSWORD
dotnet test tests/Identity.IntegrationTests
```

Переменные (переопределение env):

| Переменная | По умолчанию | Что |
|---|---|---|
| `IDENTITY_BASE_URL` | `http://127.0.0.1:10020` | Keycloak |
| `IDENTITY_REALM` | `rulink` | realm |
| `PORTAL_BASE_URL` | `http://127.0.0.1:10030` | портал task-07 |
| `INIT_DEMO_PASSWORD` | — (без него тесты токенов skip) | демо-пароль seeded-пользователей из `.env` |

Замечания:

- `SecurityTests.Five_Bad_Passwords…` создаёт временного пользователя через портал и блокирует его —
  seeded-пользователи не страдают; временные пользователи (`it-*`, `lock-*`) удаляются/остаются
  заблокированными — безвредно для повторных прогонов (email с таймстампом).
- Юнит/интеграционные тесты без живого окружения: `examples/demo-api/tests/DemoApi.Tests` (28) и
  `src/portal/tests/Portal.Tests` (20) — мок-authority/мок Admin REST.
