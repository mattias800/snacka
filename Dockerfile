FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Install Node.js for SetupWizard build
RUN apt-get update && apt-get install -y nodejs npm && rm -rf /var/lib/apt/lists/*

# Copy project files first for better layer caching
COPY ["src/Snacka.Server/Snacka.Server.csproj", "Snacka.Server/"]
COPY ["src/Snacka.Shared/Snacka.Shared.csproj", "Snacka.Shared/"]
COPY ["src/Snacka.WebRTC/Snacka.WebRTC.csproj", "Snacka.WebRTC/"]

# Restore dependencies
RUN dotnet restore "Snacka.Server/Snacka.Server.csproj"

# Copy source code
COPY src/Snacka.Server/ Snacka.Server/
COPY src/Snacka.Shared/ Snacka.Shared/
COPY src/Snacka.WebRTC/ Snacka.WebRTC/

# Build SetupWizard
WORKDIR /src/Snacka.Server/SetupWizard
RUN npm install && npm run build

# Build and publish
WORKDIR /src
RUN dotnet publish "Snacka.Server/Snacka.Server.csproj" -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Create directories for data persistence
RUN mkdir -p /app/data /app/uploads

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
# Generated secrets (when none are configured), and SQLite if used instead of PostgreSQL.
ENV SNACKA_DATA=/app/data

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "Snacka.Server.dll"]
