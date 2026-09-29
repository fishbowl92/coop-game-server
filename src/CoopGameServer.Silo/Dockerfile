# Orleans Silo와 참조 프로젝트를 SDK 단계에서 게시합니다.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/CoopGameServer.Silo/CoopGameServer.Silo.csproj
RUN dotnet publish src/CoopGameServer.Silo/CoopGameServer.Silo.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# Silo는 상태 확인 HTTP 엔드포인트도 제공하므로 ASP.NET Core 실행 이미지를 사용합니다.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
# Npgsql의 선택적 GSSAPI 인증 라이브러리를 명시해 컨테이너 시작 로그의 누락 경고를 제거합니다.
RUN apk add --no-cache krb5-libs
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080 11111 30000
ENTRYPOINT ["dotnet", "CoopGameServer.Silo.dll"]
