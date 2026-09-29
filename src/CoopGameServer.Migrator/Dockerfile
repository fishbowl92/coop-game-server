# EF Core 마이그레이션 실행 파일을 별도의 SDK 단계에서 게시합니다.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/CoopGameServer.Migrator/CoopGameServer.Migrator.csproj
RUN dotnet publish src/CoopGameServer.Migrator/CoopGameServer.Migrator.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# Persistence가 공유 관측성 라이브러리를 참조하므로 ASP.NET Core 공유 프레임워크를 포함합니다.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
# 마이그레이션 연결 시 Npgsql이 탐색하는 GSSAPI 런타임 라이브러리를 제공합니다.
RUN apk add --no-cache krb5-libs
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "CoopGameServer.Migrator.dll"]
