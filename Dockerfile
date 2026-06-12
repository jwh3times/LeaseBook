# syntax=docker/dockerfile:1
# One image serves the API under /api and the built SPA as static files (P16). Multi-stage:
# build the SPA, publish the .NET host, embed the SPA in wwwroot, run on a chiseled runtime.

# --- Stage 1: build the React SPA ---
FROM node:24-bookworm-slim AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# --- Stage 2: publish the ASP.NET Core host ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Copy build configuration first for layer caching, then source.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ ./src/
COPY seed/ ./seed/
RUN dotnet restore src/LeaseBook.Web/LeaseBook.Web.csproj
RUN dotnet publish src/LeaseBook.Web/LeaseBook.Web.csproj -c Release -o /app/publish --no-restore
# Embed the built SPA as static files served by the host (P16).
COPY --from=web /web/dist/ /app/publish/wwwroot/

# --- Stage 3: runtime (chiseled, non-root) ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "LeaseBook.Web.dll"]
