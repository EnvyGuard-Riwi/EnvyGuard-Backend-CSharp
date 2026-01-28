# Etapa de compilación
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
WORKDIR /src

# Copiamos la solución y el proyecto que están en la raíz
# Copiar archivos desde la raíz
COPY EnvyGuard.sln .
COPY EnvyGuard.Agent.csproj .

# Restaurar indicando el archivo de solución
RUN dotnet restore EnvyGuard.sln
COPY EnvyGuard.Agent.csproj .

# Restaurar dependencias
RUN dotnet restore EnvyGuard.Agent.csproj

# Copiamos todas las carpetas (Messaging, Models, Services, etc.)
# Copiar todo el código
COPY . .

# Publicar el proyecto específico
RUN dotnet publish EnvyGuard.Agent.csproj -c Release -o /app/publish

# Compilar
RUN dotnet publish EnvyGuard.Agent.csproj -c Release -o /app/publish

# Etapa final (Runtime)
# Etapa final
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "EnvyGuard.Agent.dll"]
