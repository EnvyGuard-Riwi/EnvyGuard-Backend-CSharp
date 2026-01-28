# Etapa de compilación
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copiamos la solución y el proyecto que están en la raíz
COPY EnvyGuard.sln .
COPY EnvyGuard.Agent.csproj .

# Restaurar indicando el archivo de solución
RUN dotnet restore EnvyGuard.sln

# Copiamos todas las carpetas (Messaging, Models, Services, etc.)
COPY . .

# Publicar el proyecto específico
RUN dotnet publish EnvyGuard.Agent.csproj -c Release -o /app/publish

# Etapa final (Runtime)
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "EnvyGuard.Agent.dll"]
