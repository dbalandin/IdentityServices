FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["portal/Portal.csproj", "portal/"]
COPY ["../../OTech.Shared/NlogExtensions/NlogExtensions.csproj", "../../OTech.Shared/NlogExtensions/"]
COPY ["../../OTech.Shared/WebAppServicesExtensions/WebAppServicesExtensions.csproj", "../../OTech.Shared/WebAppServicesExtensions/"]
COPY ["../../OTech.Shared/IngressGate/IngressGate.csproj", "../../OTech.Shared/IngressGate/"]
RUN dotnet restore "portal/Portal.csproj"
COPY . .
WORKDIR "/src/portal"
RUN dotnet build "./Portal.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Portal.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Portal.dll"]
