# syntax=docker/dockerfile:1

# ---------- Stage 1: build frontend ----------
FROM node:22-bookworm-slim AS web
WORKDIR /web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# ---------- Stage 2: build backend ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/AgentWorkbench.Webapi/AgentWorkbench.Webapi.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# ---------- Stage 3: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
COPY --from=build /app/publish ./
COPY --from=web /web/dist ./wwwroot
ENTRYPOINT ["dotnet", "AgentWorkbench.Webapi.dll"]
