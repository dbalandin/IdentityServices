# demopage — .NET 10 Razor demo-сайт (task-11)
# Сборка (из корня репозитория IdentityPortal). Аутентификация — библиотека IdentityService
# (ProjectReference на соседний каталог, вне репозитория), поэтому кроме контекста demopage нужен
# именованный контекст с её исходниками (BuildKit):
#   docker build --build-context identityservice=../IdentityService/IdentityService \
#     -t cr.yandex/crp9et2dv05vocv8pagt/idptestappweb:latest examples/demopage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# структура каталогов повторяет относительный ProjectReference (..\..\..\IdentityService\IdentityService)
WORKDIR /src/IdentityPortal/examples/demopage

# restore по csproj (библиотека — по её csproj): слой кэшируется при правках кода
COPY Demopage.csproj .
COPY --from=identityservice IdentityService.csproj /src/IdentityService/IdentityService/
RUN dotnet restore

COPY . .
# только исходники библиотеки — без bin/obj хоста (в каталоге библиотеки нет своего .dockerignore)
COPY --from=identityservice *.cs README.md /src/IdentityService/IdentityService/
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
USER app
# контейнер слушает 8080 (порт хоста задаётся в docker-compose: 127.0.0.1:11001:8080);
# --urls — старший приоритет конфигурации, перекрывает "Urls" из appsettings.json
ENTRYPOINT ["dotnet", "Demopage.dll", "--urls", "http://+:8080"]
