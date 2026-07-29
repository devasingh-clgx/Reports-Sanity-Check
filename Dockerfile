# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build stage: restore + publish the app with the .NET 10 SDK.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached) using just the project file.
COPY ["Reports Sanity Check.csproj", "./"]
RUN dotnet restore "Reports Sanity Check.csproj"

# Copy the rest of the source and publish a framework-dependent build.
COPY . .
RUN dotnet publish "Reports Sanity Check.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ---------------------------------------------------------------------------
# Runtime stage: the Playwright .NET image already contains Chromium and every
# OS dependency it needs, so the headless sanity check runs without an extra
# "playwright install" step. The image tag's Playwright version must match the
# Microsoft.Playwright package version referenced by the project (1.61.0).
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/playwright/dotnet:v1.61.0 AS final
WORKDIR /app

# Image metadata. Bump org.opencontainers.image.version on every release so the
# running container's version is discoverable via `docker inspect`.
LABEL org.opencontainers.image.title="Reports Sanity Check" \
      org.opencontainers.image.version="2.0.0" \
      org.opencontainers.image.description="Power BI report sanity-check Azure Container Apps Job"

# East US single daily run: the app listens on 8080 inside the container.
ENV ASPNETCORE_HTTP_PORTS=8080
# The headless browser navigates to the app's own loopback origin to load the
# shared check page; keep this aligned with the listening port above.
ENV Api__HostBaseUrl=http://localhost:8080/
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Reports_Sanity_Check.dll"]
