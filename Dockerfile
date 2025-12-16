# Etapa de construcción
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copiar archivos de proyecto
COPY EnvyGuard.sln .
COPY EnvyGuard.Agent/EnvyGuard.Agent.csproj ./EnvyGuard.Agent/
RUN dotnet restore

# Copiar el resto y compilar
COPY . .
WORKDIR /app/EnvyGuard.Agent
RUN dotnet publish -c Release -o /app/out

# Etapa de ejecución final
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app/out .

# Crear carpeta para las llaves (se montarán como volumen externo)
RUN mkdir -p /app/keys

# El ENTRYPOINT ejecuta la DLL
ENTRYPOINT ["dotnet", "EnvyGuard.Agent.dll"]