# Blazor 관리자 UI를 SDK 단계에서 게시합니다.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/CoopGameServer.Admin/CoopGameServer.Admin.csproj
RUN dotnet publish src/CoopGameServer.Admin/CoopGameServer.Admin.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CoopGameServer.Admin.dll"]
