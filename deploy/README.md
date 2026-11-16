# Keycloak-rulink Service

Deploy scripts for Keycloak-rulink

11-keycloak-rulink_image.sh - скачать образ. Работаем с версией не ниже 26.1 (нам нужны org)
12-keycloak-rulink_deploy.sh - копируем все каталоги

## При первом запуске
Заполняем `envaroments` - заполнить поля 
```
KC_BOOTSTRAP_ADMIN_USERNAME=SET_YOUR_LOGIN
KC_BOOTSTRAP_ADMIN_PASSWORD=SET_YOUR_PASS
```

> **В ПЕРЕМЕННЫХ, В ТОМ ЧИСЛЕ В ПАРОЛЯХ, НЕЛЬЗЯ ИСПОЛЬЗОВАТЬ СИМВОЛ $ - ЭТО ВОСПРИНИМАЕТСЯ КАК ПЕРЕМЕННАЯ**

В образе bin файлы keycloak лежат в `/opt/keycloak` поэтому нельзя монтировать сюда mount volume на `/opt`

```
KC_HOSTNAME = idp.rulink.io #Должен совпадать с именем сервера в nginx
```


Файлы **nginx** адаптированы - можно поднимать сервис

После первого входа 
1. создать учетную запись, добавить группу admins
2. Заблокировать учетку `KC_BOOTSTRAP_ADMIN_USERNAME=SET_YOUR_LOGIN`
3. Удалить строки из .env

В `update-keycloak-rulink.sh` UID пользователя указать, как 1000

