#!/bin/bash

# Colores
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${YELLOW}🚀 Iniciando instalación del Agente EnvyGuard como servicio...${NC}"

# 1. Verificar permisos de root
if [ "$EUID" -ne 0 ]; then
  echo -e "${RED}❌ Por favor, ejecuta este script como root (sudo).${NC}"
  exit 1
fi

# Directorios
APP_DIR="/opt/envyguard-agent"
SERVICE_FILE="/etc/systemd/system/envyguard-agent.service"
CURRENT_DIR=$(pwd)

# 2. Detener servicio si ya existe
if systemctl is-active --quiet envyguard-agent; then
    echo -e "${YELLOW}🛑 Deteniendo servicio existente...${NC}"
    systemctl stop envyguard-agent
fi

# 3. Compilar y Publicar
echo -e "${YELLOW}🔨 Compilando aplicación...${NC}"
dotnet publish "EnvyGuard.Agent.csproj" -c Release -o "$APP_DIR"

if [ $? -ne 0 ]; then
    echo -e "${RED}❌ Error en la compilación. Verifica que tengas el SDK de .NET instalado.${NC}"
    exit 1
fi

# 4. Crear archivo de servicio
echo -e "${YELLOW}📝 Creando archivo de servicio systemd...${NC}"

# Detectar usuario real (no root) para permisos si fuera necesario, 
# pero el agente de captura de pantalla suele requerir root o acceso a X11.
# Para simplificar y asegurar acceso a capturas, lo correremos como root con variables de entorno X11.

cat > "$SERVICE_FILE" <<EOL
[Unit]
Description=EnvyGuard Agent Service
After=network.target

[Service]
# Usuario root para asegurar acceso a dispositivos de red y captura
User=root
WorkingDirectory=$APP_DIR
ExecStart=/usr/bin/dotnet $APP_DIR/EnvyGuard.Agent.dll
Restart=always
RestartSec=10
Environment=DOTNET_ENVIRONMENT=Production
# Variables para captura de pantalla (ajusta según tu entorno gráfico si es necesario)
Environment=DISPLAY=:0
Environment=XAUTHORITY=/root/.Xauthority

[Install]
WantedBy=multi-user.target
EOL

# 5. Configurar permisos
chmod +x "$APP_DIR/EnvyGuard.Agent.dll"

# 6. Recargar demonios y arrancar
echo -e "${YELLOW}🔄 Recargando systemd y habilitando servicio...${NC}"
systemctl daemon-reload
systemctl enable envyguard-agent
systemctl start envyguard-agent

# 7. Verificación
if systemctl is-active --quiet envyguard-agent; then
    echo -e "${GREEN}✅ ¡Instalación completada con éxito!${NC}"
    echo -e "${GREEN}Running status:${NC}"
    systemctl status envyguard-agent --no-pager
else
    echo -e "${RED}⚠️ El servicio se instaló pero no parece estar corriendo. Revisa los logs con: journalctl -u envyguard-agent -f${NC}"
fi
