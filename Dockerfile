# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/StockandriaAgent/StockandriaAgent.csproj ./StockandriaAgent/
RUN dotnet restore ./StockandriaAgent/StockandriaAgent.csproj

COPY src/StockandriaAgent/ ./StockandriaAgent/
RUN dotnet publish ./StockandriaAgent/StockandriaAgent.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

RUN groupadd --gid 1000 stockandria \
 && useradd --uid 1000 --gid 1000 --create-home --shell /bin/bash stockandria

COPY --from=build /app/publish ./

RUN mkdir -p /app/logs /home/stockandria/.config/StockandriaAgent \
 && chown -R stockandria:stockandria /app/logs /home/stockandria/.config

USER stockandria
ENV DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "StockandriaAgent.dll"]
