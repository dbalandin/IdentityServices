# syntax=docker/dockerfile:1
# Портал администратора IdentityService (task-07).
# Сборка: docker build -t identityservices-portal src/portal
# Запуск (dev-фаза): dotnet run --urls http://127.0.0.1:10030 — см. README.md портала.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Portal.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Portal.dll"]
