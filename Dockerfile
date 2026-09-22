# syntax=docker/dockerfile:1
# One image serves the API under /api and the built SPA as static files (P16). Multi-stage:
# build the SPA, publish the .NET host, embed the SPA in wwwroot, run on a chiseled runtime.
#
# Two runnable targets:
#   --target runtime  → the application image (SPA + API on :8080). The default.
#   --target migrator → a one-shot image that applies EF migrations as the migrator role,
#                       then exits. Used by docker-compose's `migrate` service. Schema changes
#                       never run as the runtime app role (CLAUDE.md multi-tenancy), so the app
#                       image deliberately cannot migrate — this image does, via an EF bundle.

# --- Stage 1: build the React SPA ---
FROM node:26-bookworm-slim@sha256:582460f614631b59b824ac6020533b9bf339c7fdf3a6d7db31abb6b4065f0212 AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# --- Stage 2: publish the ASP.NET Core host ---
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /src
# Copy build configuration first for layer caching, then source.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ ./src/
COPY seed/ ./seed/
RUN dotnet restore src/LeaseBook.Web/LeaseBook.Web.csproj
RUN dotnet publish src/LeaseBook.Web/LeaseBook.Web.csproj -c Release -o /app/publish --no-restore
# Embed the built SPA as static files served by the host (P16).
COPY --from=web /web/dist/ /app/publish/wwwroot/

# --- Stage 3: build the EF migrations bundle ---
# A framework-dependent, self-applying executable that brings the schema to the latest migration.
# It invokes the design-time factory (AppDbContextDesignTimeFactory) at runtime, which reads the
# migrator connection from ConnectionStrings__Migrations — so the bundle carries no credentials.
FROM build AS migrations
# The pinned dotnet-ef tool (.config/dotnet-tools.json → 10.0.9, matching the EF Core packages).
COPY .config/ ./.config/
RUN dotnet tool restore
RUN dotnet ef migrations bundle \
        --configuration Release \
        --project src/LeaseBook.Web/LeaseBook.Web.csproj \
        --startup-project src/LeaseBook.Web/LeaseBook.Web.csproj \
        --context AppDbContext \
        --output /bundle/efbundle \
        --force

# --- Stage 4: migrator image (one-shot) ---
# aspnet (not chiseled): the bundle loads the Web assembly to reach the design-time factory, so it
# needs the full ASP.NET shared framework. Size is irrelevant — this is local/CD migration tooling.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f AS migrator
# Npgsql probes for the Kerberos/GSSAPI library when opening a connection; the slim base omits it,
# which prints a scary (but non-fatal, password auth still works) load error. Add it so apply is clean.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
# Owned by the app user so the bundle stays executable after the USER switch below.
COPY --from=migrations --chown=$APP_UID:$APP_UID /bundle/ ./
# Non-root, like the runtime stage. This stage holds the schema-owner credential at run time, so it
# is the last one that should apply migrations as root. apt-get above needs root; everything after
# this line does not.
USER $APP_UID
# Applies all pending migrations to ConnectionStrings__Migrations, then exits 0.
ENTRYPOINT ["./efbundle"]

# --- Stage 5: runtime (chiseled, non-root) — the application image ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "LeaseBook.Web.dll"]
