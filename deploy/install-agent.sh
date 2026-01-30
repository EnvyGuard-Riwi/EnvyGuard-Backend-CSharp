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

# 2. Instalar .NET 9.0 si no está instalado
if ! command -v dotnet &> /dev/null; then
    echo "📦 Instalando .NET 9.0 Runtime..."
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0 --runtime dotnet --install-dir /usr/share/dotnet
    ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
fi

# 3. Clonar o actualizar el repositorio
if [ -d "$INSTALL_DIR" ]; then
    echo "🔄 Actualizando agente existente..."
    cd "$INSTALL_DIR"
    git pull
else
    echo "📥 Descargando agente..."
    git clone "$REPO_URL" "$INSTALL_DIR"
fi

cd "$INSTALL_DIR"

# 4. Compilar el agente
echo "🔨 Compilando agente..."
dotnet publish -c Release -o "$INSTALL_DIR/publish"

# 5. Crear servicio systemd
echo "⚙️ Configurando servicio systemd..."
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
Environment=DISPLAY=:0
Environment=XAUTHORITY=/run/user/1000/.Xauthority

[Install]
WantedBy=graphical.target
EOF

# 6. Habilitar e iniciar el servicio
echo "🚀 Iniciando servicio..."
systemctl daemon-reload
systemctl enable $SERVICE_NAME
systemctl restart $SERVICE_NAME

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
systemctl status $SERVICE_NAME --no-pager
echo ""
echo "🔧 Comandos útiles:"
echo "  • Ver logs: journalctl -u $SERVICE_NAME -f"
echo "  • Reiniciar: sudo systemctl restart $SERVICE_NAME"
echo "  • Detener: sudo systemctl stop $SERVICE_NAME"
