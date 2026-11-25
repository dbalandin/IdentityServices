# Task 11: Deploy test app to server

## Цель

Сделай деплой тестовых приложений

## Что сделать

1. Для тестирования keycloak созданы 2 тестовых приложения. Они в папке `examples`
2. Для каждого приложения собери образ. 
3. Параметры запуска **examples\demopage** 
	- server: srv-05
	- imagename = idptestappweb
	- containername = idp-test-appweb
	- publish on nginx. url = idp-test.rulink.io
	- docker port mapping на порт 11001
4. Параметры запуска **examples\demo-api**
	- server: srv-05
	- imagename  = idptestappapi
	- containername = idp-test-appapi
	- public on nginx. url = idp-test2.rulink.io
	- docker port mapping на порт 11002
5. адаптируй шаблонные файлы для deploy. укажи в них актуальные переменные
6. Сделай деплой каждого приложения. Использyй /skill:rulink-service-deploy
7. сообщи мне о выполнении задания
