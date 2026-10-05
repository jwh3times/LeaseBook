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
FROM node:26-bookworm-slim@sha256:662933cf47f013bc8e4beb31a6116448427a82057ba7c42c97e4c5ba766504c2 AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# --- Stage 2: publish the ASP.NET Core host ---
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS migrator
# Npgsql probes for the Kerberos/GSSAPI library when opening a connection; the slim base omits it,
# which prints a scary (but non-fatal, password auth still works) load error. Add it so apply is clean.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
# Owned by the app user so the bundle stays executable after the USER switch below.
COPY --from=migrations --chown=$APP_UID:$APP_UID /bundle/ ./
# The production Container Apps Job arms this entrypoint's TLS preflight. Local Compose leaves the
# switch off because its disposable PostgreSQL container intentionally does not serve TLS.
COPY infra/migrator/ ./migrator/
# Non-root, like the runtime stage. This stage holds the schema-owner credential at run time, so it
# is the last one that should apply migrations as root. apt-get above needs root; everything after
# this line does not.
USER $APP_UID
# Validates the production connection before applying pending migrations, then exits with the
# bundle's status. CMD keeps efbundle replaceable in the guard's container-level tests.
ENTRYPOINT ["/bin/sh", "./migrator/entrypoint.sh"]
CMD ["./efbundle"]

# --- Stage 5: runtime (chiseled, non-root) — the application image ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "LeaseBook.Web.dll"]
