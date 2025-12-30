#!/bin/bash

################################################################################
# FLIS.Executor Deployment Script
#
# This script builds and deploys the FLIS.Executor service as a systemd service.
#
# Usage:
#   sudo ./deploy-executor.sh
#
# Prerequisites:
#   - .NET 8.0 SDK installed
#   - Running as root or with sudo
################################################################################

set -e

# Configuration
APP_NAME="flis-executor"
APP_DIR="/opt/flis-executor"
SERVICE_USER="flis"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$REPO_DIR/src/FLIS.Executor"

echo "=================================="
echo "FLIS.Executor Deployment Script"
echo "=================================="
echo ""

# Check if running as root
if [ "$EUID" -ne 0 ]; then
    echo "ERROR: This script must be run as root (use sudo)"
    exit 1
fi

# Check if .NET is installed
if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET SDK is not installed"
    echo "Install with: wget https://dot.net/v1/dotnet-install.sh && bash dotnet-install.sh --channel 8.0"
    exit 1
fi

echo "Step 1: Creating service user..."
if ! id -u $SERVICE_USER &>/dev/null; then
    useradd -r -s /bin/false $SERVICE_USER
    echo "  ✓ User '$SERVICE_USER' created"
else
    echo "  ✓ User '$SERVICE_USER' already exists"
fi

echo ""
echo "Step 2: Building application..."
cd "$PROJECT_DIR"
dotnet publish -c Release -o "$APP_DIR/app"
echo "  ✓ Application built and published to $APP_DIR/app"

echo ""
echo "Step 3: Setting permissions..."
chown -R $SERVICE_USER:$SERVICE_USER $APP_DIR
chmod +x $APP_DIR/app/FLIS.Executor
echo "  ✓ Permissions set"

echo ""
echo "Step 4: Creating systemd service..."
cat > /etc/systemd/system/$APP_NAME.service <<EOF
[Unit]
Description=FLIS Flash Loan Executor Service
After=network.target

[Service]
Type=notify
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$APP_DIR/app
ExecStart=$APP_DIR/app/FLIS.Executor
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=$APP_NAME
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

# Security settings
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$APP_DIR

[Install]
WantedBy=multi-user.target
EOF

echo "  ✓ Systemd service file created"

echo ""
echo "Step 5: Reloading systemd..."
systemctl daemon-reload
echo "  ✓ Systemd reloaded"

echo ""
echo "Step 6: Enabling service..."
systemctl enable $APP_NAME.service
echo "  ✓ Service enabled"

echo ""
echo "=================================="
echo "Deployment Complete!"
echo "=================================="
echo ""
echo "IMPORTANT: Before starting the service, update the configuration:"
echo "  1. Edit $APP_DIR/app/appsettings.json"
echo "  2. Configure NATS URL, node RPC URLs, and contract addresses"
echo "  3. Set the executor private key (use environment variable or secrets manager)"
echo ""
echo "Service management commands:"
echo "  Start:   sudo systemctl start $APP_NAME"
echo "  Stop:    sudo systemctl stop $APP_NAME"
echo "  Status:  sudo systemctl status $APP_NAME"
echo "  Logs:    sudo journalctl -u $APP_NAME -f"
echo ""
echo "Configuration file: $APP_DIR/app/appsettings.json"
echo ""
