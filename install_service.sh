#!/bin/bash
set -e

echo "Publishing EnvyGuard Agent..."
sudo dotnet publish EnvyGuard.Agent.csproj -c Release -o /opt/envyguard-agent

echo "Copying keys..."
if [ -d "./keys" ]; then
    sudo cp -r ./keys /opt/envyguard-agent/
fi

echo "Installing Systemd service..."
sudo cp envyguard-agent.service /etc/systemd/system/envyguard-agent.service
sudo systemctl daemon-reload

echo "Enabling and starting service..."
sudo systemctl enable envyguard-agent
sudo systemctl restart envyguard-agent

echo "Done! Service status:"
sudo systemctl status envyguard-agent --no-pager
