FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 5117

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first for better layer caching
COPY ["src/Miscord.Server/Miscord.Server.csproj", "Miscord.Server/"]
COPY ["src/Miscord.Shared/Miscord.Shared.csproj", "Miscord.Shared/"]
COPY ["src/Miscord.WebRTC/Miscord.WebRTC.csproj", "Miscord.WebRTC/"]

# Restore dependencies
RUN dotnet restore "Miscord.Server/Miscord.Server.csproj"

# Copy source code
COPY src/Miscord.Server/ Miscord.Server/
COPY src/Miscord.Shared/ Miscord.Shared/
COPY src/Miscord.WebRTC/ Miscord.WebRTC/

# Build and publish
RUN dotnet publish "Miscord.Server/Miscord.Server.csproj" -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Create directory for SQLite database
RUN mkdir -p /app/data

ENV ASPNETCORE_URLS=http://+:5117
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Miscord.Server.dll"]
