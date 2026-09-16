# Betsi Patient Flow — service image.
#
# One image serves both roles: with no arguments it runs the API; with `tenants …` or
# `license …` it runs the operator CLI against the same configuration (see
# Betsi/ControlPlane/OperatorCli.cs). A deployment therefore applies migrations with the
# exact build it is about to run, rather than with whatever a separate migration image holds.
#
#   docker build -t betsi .
#   docker run --rm -e ConnectionStrings__ControlPlane='secret:control-plane' betsi tenants list

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Manifests first: restore is cached until a dependency actually changes.
COPY global.json Directory.Build.props Directory.Packages.props Betsi.slnx ./
COPY Betsi/Betsi.csproj Betsi/
COPY tests/Betsi.Tests/Betsi.Tests.csproj tests/Betsi.Tests/
COPY tools/Betsi.LicenseTool/Betsi.LicenseTool.csproj tools/Betsi.LicenseTool/
RUN dotnet restore Betsi/Betsi.csproj

COPY . .
RUN dotnet publish Betsi/Betsi.csproj \
    --configuration Release \
    --no-restore \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Non-root: the process reads a database and writes logs, and needs nothing on this filesystem.
# The base image ships this user; the port is above 1024 so an unprivileged process can bind it.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
EXPOSE 8080

COPY --from=build /app .

# Startup is the readiness gate, not this: the container is healthy once it can serve tenants.
# /health/live deliberately does not touch a database, so a database outage does not restart
# every instance into the same outage.
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=3 \
    CMD ["/app/Betsi.Core", "--health-probe"]

ENTRYPOINT ["/app/Betsi.Core"]
