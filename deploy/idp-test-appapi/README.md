# idp-test-appapi (demo-api) — deploy на srv-05

Тестовое приложение **demo-api** (`examples/demo-api`, task-11): resource-server,
приём JWT Keycloak realm `rulink` (idp.rulink.io) через библиотеку `IdentityService`
(режим `Jwt`), эндпоинты `/public`, `/me`, `/admin`, `/audit`, `/group-data`.

| Параметр | Значение |
|---|---|
| URL | https://idp-test2.rulink.io |
| Порт хоста | `127.0.0.1:11002` → контейнер `8080` |
| Образ registry | `cr.yandex/crp9et2dv05vocv8pagt/idptestappapi:latest` |
| Локальный тег | `idptestappapi:latest` |
| Контейнер | `idp-test-appapi` |

## Сборка образа (из корня репо)

Аутентификация — библиотека `IdentityService` (ProjectReference на каталог вне репозитория),
поэтому её исходники передаются именованным контекстом (BuildKit):

```bash
docker build --build-context identityservice=../IdentityService/IdentityService \
  --provenance=false --sbom=false \
  -t cr.yandex/crp9et2dv05vocv8pagt/idptestappapi:latest examples/demo-api
docker push cr.yandex/crp9et2dv05vocv8pagt/idptestappapi:latest
```

## Обновление (уже задеплоен)

Новых обязательных переменных нет: `Mode=Jwt` и `Audience=demo-api` зашиты в `appsettings.json` образа.
В общем `compose.yaml` стенда ключ `IDENTITY__VALIDATEAUDIENCE` (из первой версии фрагмента) больше
не используется — библиотека его игнорирует (строгая проверка `aud` включается самим `IDENTITY__AUDIENCE`);
`IDENTITY__MODE: Jwt` в compose необязателен (дублирует appsettings).

## Деплой на сервер

```bash
# перенести папку deploy/idp-test-appapi -> /home/dbalandin/idp-test-appapi
# на сервере (из папки сервиса):
cp envexample .env          # секретов нет
chmod +x *.sh
printf '%s\n' '<IAM-token>' | sudo ./11-idp-test-appapi_image.sh
sudo ./12-idp-test-appapi_deploy.sh
sudo /opt/services/z-config/update-nginx.sh      # применить nginx-сайт
sudo /opt/services/z-config/update-compose.sh    # перезапустить compose-стек
```

## Проверка

```bash
curl https://idp-test2.rulink.io/public           # 200, анонимный health/echo
curl https://idp-test2.rulink.io/me               # 401 без токена
```

Полный набор проверок (токен + все эндпоинты + DoD-матрица для 5 пользователей) — Postman-коллекция
`examples/postman/demo-api.postman_collection.json`: переменные `api_url`/`idp_url` по умолчанию указывают на этот стенд.
