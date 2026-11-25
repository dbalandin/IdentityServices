# idp-test-appweb (demopage) — deploy на srv-05

Тестовое приложение **demopage** (`examples/demopage`, task-11): Razor-сайт,
OIDC-логин через Keycloak realm `rulink` (idp.rulink.io) библиотекой `IdentityService`
(режим `Cookie`), вызов demo-api.

| Параметр | Значение |
|---|---|
| URL | https://idp-test.rulink.io |
| Порт хоста | `127.0.0.1:11001` → контейнер `8080` |
| Образ registry | `cr.yandex/crp9et2dv05vocv8pagt/idptestappweb:latest` |
| Локальный тег | `idptestappweb:latest` |
| Контейнер | `idp-test-appweb` |

## Сборка образа (из корня репо)

Аутентификация — библиотека `IdentityService` (ProjectReference на каталог вне репозитория),
поэтому её исходники передаются именованным контекстом (BuildKit):

```bash
docker build --build-context identityservice=../IdentityService/IdentityService \
  --provenance=false --sbom=false \
  -t cr.yandex/crp9et2dv05vocv8pagt/idptestappweb:latest examples/demopage
docker push cr.yandex/crp9et2dv05vocv8pagt/idptestappweb:latest
```

## Обновление (уже задеплоен)

Новых обязательных переменных нет: `Mode=Cookie` зашит в `appsettings.json` образа
(`IDENTITY__MODE` в compose — дубликат). Выход — `POST /Account/Logout`; после выхода из Keycloak
сайт возвращает на `/` (redirect URIs клиента `demo-api` покрывают его: пустой список
«Valid post logout redirect URIs» = использовать «Valid Redirect URIs»).

## Деплой на сервер

```bash
# перенести папку deploy/idp-test-appweb -> /home/dbalandin/idp-test-appweb
# на сервере (из папки сервиса):
cp envexample .env          # секретов нет
chmod +x *.sh
printf '%s\n' '<IAM-token>' | sudo ./11-idp-test-appweb_image.sh
sudo ./12-idp-test-appweb_deploy.sh
sudo /opt/services/z-config/update-nginx.sh      # применить nginx-сайт
sudo /opt/services/z-config/update-compose.sh    # перезапустить compose-стек
```

## Зависимости

- redirect URI клиента `demo-api` в realm rulink должен содержать
  `https://idp-test.rulink.io/*` — переменная `DEMO_API_REDIRECT_URIS`
  keycloak-rulink + повторный запуск `init/realm-bootstrap.sh` (идемпотентен).
- Сайт вызывает demo-api по внутреннему адресу compose-сети
  `http://idp-test-appapi:8080` (контейнер `idp-test-appapi`, task-11).
