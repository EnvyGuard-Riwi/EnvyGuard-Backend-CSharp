FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copiar archivos desde la raíz
COPY EnvyGuard.sln .
COPY EnvyGuard.Agent.csproj .

# Restaurar dependencias
RUN dotnet restore EnvyGuard.Agent.csproj

# Copiar todo el código
COPY . .

# Compilar
RUN dotnet publish EnvyGuard.Agent.csproj -c Release -o /app/publish

# Etapa final
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "EnvyGuard.Agent.dll"]