# Build from the repository root: docker build -f src/MyTranslator.Web/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source

COPY src/MyTranslator.Web/MyTranslator.Web.csproj src/MyTranslator.Web/
COPY src/MyTranslator.Shared/MyTranslator.Shared.csproj src/MyTranslator.Shared/
RUN dotnet restore src/MyTranslator.Web/MyTranslator.Web.csproj

COPY src/MyTranslator.Web/ src/MyTranslator.Web/
COPY src/MyTranslator.Shared/ src/MyTranslator.Shared/
# Keep Release trimming and precompression, using the bundled WASM runtime.
# Native relinking/AOT is not needed, so no wasm-tools workload is required.
RUN dotnet publish src/MyTranslator.Web/MyTranslator.Web.csproj \
    --configuration Release --no-restore --output /app/publish \
    /p:WasmBuildNative=false

FROM nginx:stable-alpine AS runtime
COPY src/MyTranslator.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot/ /usr/share/nginx/html/
EXPOSE 8080
