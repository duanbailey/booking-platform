# The API image (T27a), built from the repository root:
#   docker build -f src/BookingPlatform.Api/Dockerfile -t booking-api .
# Runs the web API by default; the same image runs the operator commands with the command overridden
# (`db migrate` in the deploy script, `admin ...` from the runbook).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/BookingPlatform.Api/BookingPlatform.Api.csproj src/BookingPlatform.Api/
RUN dotnet restore src/BookingPlatform.Api/BookingPlatform.Api.csproj
COPY src/BookingPlatform.Api/ src/BookingPlatform.Api/
RUN dotnet publish src/BookingPlatform.Api/BookingPlatform.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Time zone data for IANA names (R2): NodaTime's bundled tzdb comes first, and ZoneResolver falls back to the OS
# data for a zone it lacks. The Debian-based image normally has it; installing it makes that explicit.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# The worker's database grants, applied by `db migrate` (Db:WorkerGrantsFile defaults to db/worker-grants.sql here).
COPY worker/db/worker-grants.sql db/worker-grants.sql
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "BookingPlatform.Api.dll"]
