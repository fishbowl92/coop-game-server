# 빌드 도구가 포함된 SDK 단계에서 복원과 게시를 수행합니다.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/CoopGameServer.Api/CoopGameServer.Api.csproj
RUN dotnet publish src/CoopGameServer.Api/CoopGameServer.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# 최종 이미지는 컴파일러가 없는 ASP.NET Core 실행 환경만 포함합니다.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
# Npgsql이 PostgreSQL의 선택적 GSSAPI 인증 기능을 탐색할 때 라이브러리 누락 경고를 남기지 않게 합니다.
RUN apk add --no-cache krb5-libs
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CoopGameServer.Api.dll"]
