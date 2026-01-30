#!/bin/bash
# ============================================
# EnvyGuard Agent - Script de Instalación
# Instala el agente como servicio permanente
# ============================================

set -e

INSTALL_DIR="/opt/envyguard-agent"
SERVICE_NAME="envyguard-agent"
REPO_URL="https://github.com/EnvyGuard-Riwi/EnvyGuard-Backend-CSharp.git"

echo "🛡️ Instalador de EnvyGuard Agent"
echo "================================"

# Verificar que sea root
if [ "$EUID" -ne 0 ]; then
    echo "❌ Por favor ejecuta como root: sudo ./install-agent.sh"
    exit 1
fi

# 1. Instalar dependencias
echo "📦 Instalando dependencias..."
apt-get update -qq
apt-get install -y scrot imagemagick git curl

# 2. Verificar .NET SDK (necesario para compilar)
if ! command -v dotnet &> /dev/null || ! dotnet --list-sdks | grep -q "8.0"; then
    echo "📦 Instalando .NET 8.0 SDK..."
    # Método oficial de Microsoft para Ubuntu
    wget -q https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb
    apt-get update -qq
    apt-get install -y dotnet-sdk-8.0
fi

# 3. Clonar o actualizar el repositorio
if [ -d "$INSTALL_DIR" ]; then
    echo "🔄 Actualizando agente existente..."
    cd "$INSTALL_DIR"
    git fetch origin
    git reset --hard origin/main
else
    echo "📥 Descargando agente..."
    git clone "$REPO_URL" "$INSTALL_DIR"
fi

cd "$INSTALL_DIR"

# 4. Compilar el agente (especificar el archivo .csproj correcto)
echo "🔨 Compilando agente..."
dotnet restore EnvyGuard.Agent.csproj
dotnet publish EnvyGuard.Agent.csproj -c Release -o "$INSTALL_DIR/publish"

# 5. Crear servicio systemd
echo "⚙️ Configurando servicio systemd..."

# Obtener el usuario actual que tiene sesión gráfica
XUSER=$(who | grep -E '\(:0\)' | head -1 | awk '{print $1}')
XUSER=${XUSER:-$(logname 2>/dev/null || echo "root")}
XUID=$(id -u "$XUSER" 2>/dev/null || echo "1000")

cat > /etc/systemd/system/$SERVICE_NAME.service << EOF
[Unit]
Description=EnvyGuard Screen Monitoring Agent
After=network.target graphical.target
Wants=graphical.target

[Service]
Type=simple
User=root
WorkingDirectory=$INSTALL_DIR/publish
ExecStart=/usr/bin/dotnet $INSTALL_DIR/publish/EnvyGuard.Agent.dll
Restart=always
RestartSec=10
# Variables para acceso a X11
Environment=DISPLAY=:0
Environment=XAUTHORITY=/run/user/$XUID/gdm/Xauthority

[Install]
WantedBy=graphical.target
EOF

# 6. Habilitar e iniciar el servicio
echo "🚀 Iniciando servicio..."
systemctl daemon-reload
systemctl enable $SERVICE_NAME
systemctl restart $SERVICE_NAME

# Esperar un momento para que inicie
sleep 3

# 7. Verificar estado
echo ""
echo "✅ ¡Instalación completada!"
echo "================================"
echo "El agente ahora:"
echo "  • Se inicia automáticamente al encender el PC"
echo "  • Espera el comando START desde el frontend"
echo "  • Envía capturas cuando el modo espía está activo"
echo ""
echo "📊 Estado del servicio:"
systemctl status $SERVICE_NAME --no-pager || true
echo ""
echo "🔧 Comandos útiles:"
echo "  • Ver logs: journalctl -u $SERVICE_NAME -f"
echo "  • Reiniciar: sudo systemctl restart $SERVICE_NAME"
echo "  • Detener: sudo systemctl stop $SERVICE_NAME"
