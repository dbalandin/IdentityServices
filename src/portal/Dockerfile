FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Identity.keycloak/src/portal/Portal.csproj", "Identity.keycloak/src/portal/"]
COPY ["OTech.Shared/NlogExtensions/NlogExtensions.csproj", "OTech.Shared/NlogExtensions/"]
COPY ["OTech.Shared/WebAppServicesExtensions/WebAppServicesExtensions.csproj", "OTech.Shared/WebAppServicesExtensions/"]
COPY ["OTech.Shared/IngressGate/IngressGate.csproj", "OTech.Shared/IngressGate/"]
RUN dotnet restore "Identity.keycloak/src/portal/Portal.csproj"

COPY ["OTech.Shared/", "OTech.Shared/"]
COPY ["Identity.keycloak/src/portal", "Identity.keycloak/src/portal"]
WORKDIR "/src/Identity.keycloak/src/portal"
RUN dotnet build "./Portal.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Portal.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Portal.dll"]
