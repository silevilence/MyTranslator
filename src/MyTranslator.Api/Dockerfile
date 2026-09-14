# Build from the repository root: docker build -f src/MyTranslator.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source

COPY src/MyTranslator.Api/MyTranslator.Api.csproj src/MyTranslator.Api/
RUN dotnet restore src/MyTranslator.Api/MyTranslator.Api.csproj

COPY src/MyTranslator.Api/ src/MyTranslator.Api/
RUN dotnet publish src/MyTranslator.Api/MyTranslator.Api.csproj \
    --configuration Release --no-restore --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__DefaultConnection="Data Source=/data/mytranslator.db"

# Docker initializes an empty named volume with this directory's ownership.
# SQLite also needs to write its WAL and SHM files beside the database.
RUN mkdir -p /data && chown app:app /data
COPY --from=build /app/publish/ ./

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MyTranslator.Api.dll"]
