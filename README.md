# EnvyGuard-Backend-C-
# 🛡️ EnvyGuard Agent (C# Backend)

> **The ultimate laboratory monitoring and control agent.**

This repository contains the **C# Worker Service** for the EnvyGuard ecosystem. It acts as the "Agent" responsible for executing remote administrative commands via SSH, monitoring network status via ICMP (Ping), performing Wake-on-LAN operations, and providing real-time screen surveillance.

---

## 👥 Authors & Credits

**Clan Van Rossum — C# Technology**

This project was architected and developed by:

* 👩‍💻 Mariana Restrepo Acevedo
* 👨‍💻 Diego Alejandro Zuluaga Yepes

---

## 🚀 Features

The EnvyGuard Agent is a multi-threaded background service powered by **.NET 9.0** and **RabbitMQ**.

### 1. Remote Command Execution (SSH)

Connects to target Linux machines to perform administrative tasks securely:

* Power Management: Shutdown, Reboot.
* Session Control: Lock active user sessions (loginctl).
* Internet Control: Block/Unblock specific websites (modifying /etc/hosts).
* Security & Cleanup: "Format" mode which detects intruder users, deletes them, and cleans up home directories of authorized users.
* Software Management: Install applications (apt-get, snap).

### 2. Network Radar (Scanner)

* Fetches a dynamic list of computers from the EnvyGuard Java Backend API.
* Continuously pings devices to detect their status (ONLINE / OFFLINE).
* Reports real-time status updates to RabbitMQ.

### 3. Screen Surveillance (Spy Mode)

* Captures screenshots of the host machine using scrot.
* Optimizes images using mogrify (ImageMagick).
* Smart Upload: Only uploads images when visual changes are detected (Hash comparison) to save bandwidth.
* Controllable via RabbitMQ (START/STOP commands).

### 4. Wake-on-LAN (WOL)

* Sends "Magic Packets" to broadcast addresses to wake up powered-off machines using their MAC Address.

---

## 📂 Project Structure

```text
EnvyGuard-Backend-CSharp-dev/
├── 🐳 Dockerfile                 # Docker build definition for the Agent
├── 📄 EnvyGuard.sln              # Visual Studio Solution file
├── 📂 EnvyGuard.Agent/           # Main Project Folder
│   ├── 📄 EnvyGuard.Agent.csproj # .NET 9.0 Project settings & dependencies
│   ├── 📄 Program.cs             # Entry point & Dependency Injection setup
│   ├── 📄 Worker.cs              # Main background lifecycle manager
│   ├── 📂 Messaging/             # RabbitMQ Communication Layer
│   │   ├── 📄 CommandConsumer.cs # Listens for admin commands (Shutdown, Block, etc.)
│   │   └── 📄 RabbitMqConnection.cs # Connection factory wrapper
│   ├── 📂 Models/                # Data Transfer Objects (DTOs)
│   │   └── 📄 PcCommand.cs       # Structure of commands received from RabbitMQ
│   ├── 📂 Services/              # Core Business Logic
│   │   ├── 📄 CommandExecutor.cs # Logic for SSH connections & Linux commands
│   │   ├── 📄 NetworkScannerWorker.cs # Periodic Ping scanner & API Fetcher
│   │   └── 📄 ScreenSpyWorker.cs # Screen capture & optimization logic
│   └── 📂 keys/                  # (GitIgnored) Folder for SSH Private Keys
└── 📂 deploy/                    # Deployment configurations
```
---

## 🛠️ Tech Stack

Framework: .NET 9.0 (Worker Service)
Messaging: RabbitMQ (RabbitMQ.Client v7.2)
Remote Access: SSH.NET
Containerization: Docker & Docker Compose
System Tools (Linux): scrot, mogrify (ImageMagick), ping

---

## ⚙️ Configuration

The application is configured via appsettings.json or Environment Variables.

Key Configuration Settings (appsettings.json):

{
"BackendApiUrl": "https://api.envyguard.crudzaso.com/api/computers",
"RabbitMQ": {
"HostName": "rabbitmq.envy.crudzaso.com",
"UserName": "your_user",
"Password": "your_password",
"QueueName": "pc_commands",
"StatusQueueName": "pc_status_updates"
},
"SshConfig": {
"User": "envyguard_admin",
"KeyPath": "keys/id_rsa",
"Password": ""
}
}

SshConfig:
You can use either a password OR a private key (KeyPath).
The code prioritizes the password if set; otherwise, it looks for the key file.

---

## 🏃‍♂️ How to Run

Prerequisites:

* .NET 9.0 SDK
* RabbitMQ Server (Local or Cloud)
* Linux Environment (Optional, but required for ScreenSpyWorker functionality)

---

### Option 1: Running Locally (Development)

Clone the repository:

git clone https://github.com/YourRepo/EnvyGuard-Backend-CSharp.git
cd EnvyGuard-Backend-CSharp-dev

Restore dependencies:

dotnet restore

Setup Environment:

Ensure you have an appsettings.json or a .env file with valid RabbitMQ credentials.
If using SSH Keys, place your private key in EnvyGuard.Agent/keys/id_rsa.

Run the application:

cd EnvyGuard.Agent
dotnet run

---

### Option 2: Running with Docker (Production)

Build the Image:

docker build -t envyguard-agent .

Run the Container:

docker run -d \
--name envyguard-agent \
--restart always \
-v $(pwd)/keys:/app/keys \
-e RabbitMQ__HostName="rabbitmq.yourdomain.com" \
-e RabbitMQ__UserName="admin" \
-e RabbitMQ__Password="secret_pass" \
envyguard-agent

---

### Option 3: Docker Compose

Edit docker-compose.yml (ensure credentials match).

Start services:

docker-compose up -d --build

---

## ⚠️ Known Dependencies (Linux)

For the Screen Spy module to work correctly on a physical Linux machine, the following packages must be installed:

sudo apt-get update
sudo apt-get install scrot imagemagick

If these are missing, the Spy module will log an error but the rest of the application will continue to function.

---

By Clan Van Rossum
