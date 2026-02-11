# ============================================================================
# Sharpbot — Multi-stage Docker Build
# .NET 9 Web Application with Playwright browser support
# ============================================================================

# ---------------------------------------------------------------------------
# Stage 1: Restore & Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files first for better layer caching
COPY Sharpbot.sln ./
COPY src/Sharpbot/Sharpbot.csproj src/Sharpbot/

# Restore NuGet packages
RUN dotnet restore

# Copy the rest of the source code
COPY src/ src/

# Install PowerShell (needed for Playwright install script) — before publish for layer caching
RUN dotnet tool install --global PowerShell
ENV PATH="$PATH:/root/.dotnet/tools"

# Publish the application in Release mode
RUN dotnet publish src/Sharpbot/Sharpbot.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Install Playwright Chromium browser + OS dependencies in the build stage
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN pwsh /app/publish/.playwright/node/linux-x64/playwright.sh install --with-deps chromium

# ---------------------------------------------------------------------------
# Stage 2: Runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install minimal runtime dependencies required by Chromium
RUN apt-get update && apt-get install -y --no-install-recommends \
    libnss3 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxrandr2 \
    libgbm1 \
    libpango-1.0-0 \
    libcairo2 \
    libasound2 \
    libxshmfence1 \
    libx11-xcb1 \
    fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

# Copy published app from build stage
COPY --from=build /app/publish .

# Copy Playwright browser binaries from build stage
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
COPY --from=build /ms-playwright /ms-playwright

# Create a volume mount point for persistent data (sessions, cron jobs, workspace)
VOLUME /app/data

# Default port — matches Gateway.Port in appsettings.json
EXPOSE 56789

# Environment variables
ENV ASPNETCORE_URLS=http://+:56789
ENV DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["dotnet", "sharpbot.dll"]
