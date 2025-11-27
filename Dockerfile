# Etapa de construcción
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copiar archivos de proyecto y restaurar dependencias
COPY EnvyGuard.sln .
COPY EnvyGuard.Agent/EnvyGuard.Agent.csproj ./EnvyGuard.Agent/
RUN dotnet restore

# Copiar el resto del código y compilar
COPY . .
WORKDIR /app/EnvyGuard.Agent
RUN dotnet publish -c Release -o /app/out

# Etapa de ejecución
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app/out .

# Instalar bash si el agente lo necesita para ejecutar comandos
RUN apt-get update && apt-get install -y bash && rm -rf /var/lib/apt/lists/*

ENTRYPOINT ["dotnet", "EnvyGuard.Agent.dll"]