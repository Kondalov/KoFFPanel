# KoFFPanel

### Modern Windows Control Panel for Remote Linux Servers, VPN Infrastructure and Client Management

**KoFFPanel** is a Windows desktop application for managing remote Linux servers and VPN infrastructure through a modern graphical interface.

It combines SSH-based server management, real-time infrastructure monitoring, VPN client management, protocol configuration, subscription generation and Telegram Bot integration into a single desktop control panel.

Instead of switching between SSH terminals, configuration files, database tools and separate administration panels, KoFFPanel provides a centralized workspace for managing your infrastructure.

---

## ✨ What is KoFFPanel?

KoFFPanel is designed for administrators and infrastructure operators who manage one or more Linux servers running modern proxy and VPN cores.

The application allows you to:

* Connect to remote Linux servers via SSH
* Monitor server resources in real time
* Manage VPN users and their access
* Work with Xray-core, sing-box and TrustTunnel
* Create and manage multi-protocol client configurations
* Set traffic limits and expiration dates
* Enable or disable user access
* Generate subscription links
* Open a built-in SSH terminal
* Upload and download files through SSH
* Deploy server configurations
* Synchronize users with an external Telegram Bot
* Manage pending users and reserve configuration pools
* Monitor core status, logs and runtime information
* Store local data in an encrypted SQLite database
* Automatically create database backups

KoFFPanel is built for people who want a **native Windows administration experience without sacrificing the flexibility of Linux server infrastructure**.

---

## 🖥️ Main Dashboard

The main dashboard provides an overview of the selected server and its running infrastructure.

It displays information such as:

* Server status
* CPU usage
* RAM usage
* SSD usage
* Ping / round-trip time
* Uptime
* Load average
* Network speed
* TCP connections
* SYN_RECV connections
* Error rate
* Running core processes
* VPN client statistics
* Total traffic usage

The dashboard continuously monitors the selected server and updates the displayed metrics in the background.

You can switch between multiple configured servers without restarting the application.

---

## 🖥️ Multi-Server Management

KoFFPanel supports multiple remote server profiles.

Each server profile may contain:

* Server name
* IP address
* SSH port
* Username
* Password authentication
* SSH key authentication
* Custom domain
* Core type
* Protocol configuration

When a server is selected, KoFFPanel automatically:

1. Establishes the required SSH connection.
2. Loads the local client database for that server.
3. Starts the monitoring loop.
4. Detects the configured core.
5. Loads current client and infrastructure data.
6. Displays the appropriate management interface.

The application avoids unnecessarily restarting monitoring when the selected server configuration has not changed.

---

## 🔐 SSH Server Management

KoFFPanel uses SSH as the primary communication layer with remote Linux servers.

The SSH abstraction supports:

* Connection management
* Password authentication
* Private key authentication
* Interactive shell sessions
* Command execution
* Command cancellation
* Configurable command timeouts
* Terminal resizing
* Remote directory listing
* File downloads
* File uploads
* Interactive shell streams

This allows KoFFPanel to function as both a management panel and a graphical SSH-based administration tool.

---

## 💻 Built-in SSH Terminal

KoFFPanel includes an integrated terminal for remote server administration.

You can execute commands directly on the selected server without opening a separate terminal application.

The terminal supports:

* Interactive shell sessions
* Command execution
* Shell output streaming
* Terminal resizing
* Remote working directory tracking
* Remote file operations

This makes it possible to combine graphical administration with direct command-line control when needed.

---

## 📊 Real-Time Server Monitoring

The monitoring subsystem collects infrastructure information from the remote server.

Depending on the selected server and configured core, KoFFPanel can display:

### Server Resources

* CPU usage
* RAM usage
* SSD usage
* Uptime
* Load average
* Network speed
* Ping latency

### Network Information

* TCP connections
* SYN_RECV connections
* Connection statistics
* Error rate

### Core Information

* Core status
* Core version
* Core uptime
* Memory usage
* Recent logs
* Last known error
* Running processes

Monitoring runs asynchronously in the background and is connected to the currently selected server.

---

# 🌐 Supported Infrastructure Cores

KoFFPanel is designed to work with multiple modern server-side cores.

## Xray-core

KoFFPanel provides dedicated functionality for Xray-core, including:

* Core status detection
* Log retrieval
* Client retrieval
* Core restart
* Server reboot
* Reality initialization
* Geo data updates
* Client management integration

The application can initialize a Reality-based configuration and generate the resulting client connection information.

---

## sing-box

KoFFPanel provides dedicated sing-box user management.

Supported functionality includes:

* Retrieve users
* Add users
* Remove users
* Enable or disable users
* Update traffic limits
* Update expiration dates
* Reset traffic counters
* Synchronize users to the server core
* Retrieve traffic statistics

The architecture supports multiple protocols and transport configurations, including:

* VLESS
* Hysteria2
* TrustTunnel
* Trojan
* Shadowsocks

A single user can be configured with multiple protocol options depending on the server configuration.

---

## TrustTunnel

KoFFPanel includes a dedicated TrustTunnel user-management integration.

Supported operations include:

* Retrieve users
* Create users
* Remove users
* Enable or disable access
* Update traffic limits
* Update expiration dates
* Reset traffic usage
* Synchronize users with the server
* Generate TrustTunnel connection links

TrustTunnel is handled as a separate core integration rather than being treated as a generic configuration format.

---

# 👥 VPN Client Management

The client management system is designed for real-world subscription and access management.

For each client, KoFFPanel can manage:

* UUID
* Email or identifier
* Server
* Protocol
* Traffic limit
* Used traffic
* Expiration date
* Active/inactive state
* P2P restriction
* Protocol availability
* Generated connection links
* Subscription information
* Activity statistics

Administrators can:

* Add clients
* Delete clients
* Enable access
* Disable access
* Change traffic limits
* Change expiration dates
* Reset traffic usage
* Synchronize clients with the remote core

---

# 🔗 Subscription Management

KoFFPanel supports subscription-based client configuration management.

The subscription system can:

* Initialize subscription infrastructure on a server
* Create or update user subscriptions
* Combine multiple protocol links
* Delete subscriptions
* Generate subscription URLs
* Use a custom domain when configured

This allows a single client to receive a subscription containing multiple available connection methods instead of manually managing individual links.

---

# 📈 Client Analytics

KoFFPanel tracks client activity and traffic-related information.

The system can provide information such as:

* Total users
* Active users
* Total traffic
* Online users
* Daily IP activity
* Last known country
* Connection statistics

GeoIP functionality can be used to associate connection activity with geographical information.

---

# 🤖 Telegram Bot Integration

KoFFPanel can connect to an external Telegram Bot service through an HTTP API.

The bot integration is designed to connect the administration panel with the user-facing service.

The integration can:

* Check bot availability
* Monitor bot health
* Synchronize pending users
* Push server configuration templates
* Manage a reserve key pool
* Synchronize legacy users
* Perform automatic background synchronization

The application communicates with the bot using an API secret.

---

## 🔄 Pending User Synchronization

When new users are created through the bot, they can appear in a pending queue.

KoFFPanel can:

1. Query the bot for pending users.
2. Load their data.
3. Create or update local client records.
4. Save the changes to the local database.
5. Commit the synchronization back to the bot.
6. Deploy the required configuration to the selected server.

This creates a workflow where the bot can accept new users while KoFFPanel remains responsible for infrastructure deployment and configuration management.

---

## 🗝️ Reserve Key Pool

KoFFPanel supports a reserve pool of pre-generated configuration keys.

The reserve pool is designed to make new-user provisioning faster.

The application can:

* Generate reserve client identities.
* Store them locally.
* Push them to the bot.
* Keep a predefined pool of ready-to-use configurations.

When a new client appears, the bot can use an already prepared configuration instead of waiting for a complete server-side provisioning process.

---

## 📡 Automatic Bot Synchronization

The bot integration includes several background processes:

* Health checks
* Automatic synchronization
* Periodic statistics refresh
* Nightly synchronization routines

The application can continuously monitor whether the bot is online and synchronize data without requiring manual interaction for every operation.

---

# 🚀 Server Deployment

KoFFPanel includes a deployment workflow for initial server setup and configuration operations.

The deployment system is designed to simplify tasks that would normally require:

* SSH access
* Manual command execution
* Configuration file editing
* Service restarts
* Key generation
* Protocol configuration

The goal is to provide a guided workflow for setting up and configuring server-side infrastructure.

---

# 🗄️ Local Data Storage

KoFFPanel uses a local SQLite database for application data.

The database stores information such as:

* Server profiles
* VPN clients
* Client traffic data
* Configuration-related metadata
* Synchronization state

The application performs database initialization and optimization during startup.

The infrastructure layer includes:

* Entity Framework Core
* SQLite
* Database migrations
* WAL-related optimization
* Integrity checks
* Automatic backup functionality

---

# 💾 Automatic Backups

KoFFPanel includes a database backup service.

The application can automatically create backups of local application data.

This helps protect:

* Client databases
* Server profiles
* Traffic statistics
* Synchronization data

The backup process is initialized as part of the application lifecycle.

---

# 🔒 Security

Security is an important part of KoFFPanel's architecture.

The project includes mechanisms for protecting sensitive information, including:

* Windows Data Protection API integration
* Protected local secrets
* API secret authentication for bot communication
* SSH authentication support
* Password and key-based server access
* Local database protection capabilities
* Separation of infrastructure and presentation logic

Sensitive configuration data should never be committed to source control.

---

# 🏗️ Architecture

KoFFPanel is organized into multiple layers.

```text
KoFFPanel
│
├── KoFFPanel.Domain
│   └── Core entities and domain models
│
├── KoFFPanel.Application
│   └── Application interfaces and business abstractions
│
├── KoFFPanel.Infrastructure
│   └── SSH, database, cores, persistence and external integrations
│
├── KoFFPanel.Presentation
│   └── WPF desktop interface and MVVM features
│
└── KoFFPanel.Tests
    └── Automated tests
```

## Domain

Contains the core domain models and entities.

The domain layer does not depend on the user interface.

---

## Application

Contains abstractions and application-level contracts.

Examples include interfaces for:

* SSH operations
* Server monitoring
* Xray-core management
* sing-box user management
* TrustTunnel user management
* Subscription management
* Database backups
* Analytics
* Logging

This allows the presentation layer to depend on abstractions instead of concrete infrastructure implementations.

---

## Infrastructure

Contains implementation details such as:

* SSH communication
* SQLite persistence
* Entity Framework Core
* Server monitoring
* Xray integrations
* sing-box integrations
* TrustTunnel integrations
* Subscription infrastructure
* GeoIP
* Database backups
* External API communication

---

## Presentation

Contains the WPF desktop application.

The presentation layer uses:

* WPF
* MVVM
* CommunityToolkit.Mvvm
* WPF-UI
* Microsoft WebView2
* Dependency Injection

The application is divided into feature-oriented areas such as:

* Cabinet
* Bot
* Terminal
* Deploy
* Analytics
* Management
* Configuration
* Shared dialogs

---

# 🧰 Technology Stack

| Technology                           | Purpose                            |
| ------------------------------------ | ---------------------------------- |
| .NET 10                              | Application platform               |
| C#                                   | Primary programming language       |
| WPF                                  | Native Windows desktop UI          |
| MVVM                                 | UI architecture                    |
| CommunityToolkit.Mvvm                | MVVM infrastructure                |
| WPF-UI                               | Modern Windows UI components       |
| SSH.NET                              | SSH communication                  |
| Entity Framework Core                | Data access                        |
| SQLite                               | Local database                     |
| SQLCipher bundle                     | Database encryption capabilities   |
| Microsoft WebView2                   | Embedded web content               |
| MaxMind GeoIP2                       | GeoIP information                  |
| Microsoft.Extensions.Hosting         | Application services and lifecycle |
| Microsoft.Extensions.Http.Resilience | HTTP resilience                    |
| Windows Data Protection              | Local secret protection            |
| xUnit                                | Automated testing                  |

---

# 🖼️ Screenshots

> Screenshots will be added soon.

Recommended screenshots:

* Main dashboard
* Server management
* Client management
* Core status
* Telegram Bot integration
* SSH Terminal
* Deployment wizard
* Analytics

---

# ⚙️ Requirements

## Operating System

* Windows 10 version 1809 or later
* Windows 11 recommended

## Runtime

The project targets:

```text
.NET 10
```

## Remote Server

A Linux server with:

* SSH access
* A supported server-side core
* Valid authentication credentials

The exact requirements depend on the selected integration and deployment workflow.

---

# 🚀 Getting Started

## 1. Clone the repository

```bash
git clone https://github.com/Kondalov/KoFFPanel.git
```

## 2. Open the solution

Open the solution in Visual Studio with .NET 10 SDK installed.

## 3. Configure the application

Configure the required server connection and application settings.

Do not commit:

* passwords
* private SSH keys
* API secrets
* master passwords
* production database files

## 4. Build the project

```bash
dotnet build
```

## 5. Run the application

Start the WPF presentation project.

---

# 🔑 Server Connection

A server profile can use:

* SSH password authentication
* SSH private key authentication

After connecting to a server, KoFFPanel can:

1. Verify the SSH connection.
2. Start monitoring.
3. Detect the configured core.
4. Load local client data.
5. Display the management dashboard.

---

# 🧪 Testing

The repository contains a dedicated test project based on:

* xUnit
* Microsoft.NET.Test.Sdk
* Coverlet

Run tests with:

```bash
dotnet test
```

---

# 🛣️ Roadmap

The roadmap is evolving together with the project.

Potential future directions include:

* Improved onboarding
* Better deployment automation
* More server-side integrations
* Expanded monitoring
* Improved analytics
* More advanced backup management
* Multi-server overview
* Improved documentation
* Automated release builds
* Installer packages
* More comprehensive automated tests
* Plugin-based core integrations
* Additional subscription formats

---

# 🤝 Contributing

Contributions, bug reports and feature requests are welcome.

Before submitting a pull request:

1. Make sure the project builds successfully.
2. Keep changes focused.
3. Follow the existing architecture.
4. Avoid unnecessary duplication.
5. Keep business logic outside the presentation layer where possible.
6. Add or update tests when appropriate.

---

# ⚠️ Disclaimer

KoFFPanel is an administration and infrastructure management tool.

The user is responsible for:

* The servers they manage.
* The services they deploy.
* Their configuration.
* Their network traffic.
* Compliance with applicable laws and regulations.

Always review deployment scripts and configuration changes before applying them to production infrastructure.

---

# 📄 License

License information will be added to the repository.

---

# ⭐ Support the Project

If KoFFPanel is useful to you:

⭐ Star the repository
🐛 Report bugs
💡 Suggest features
🤝 Contribute improvements

---

## KoFFPanel

### Manage your infrastructure from one place.

**Windows desktop administration for Linux servers, VPN cores, clients, subscriptions and automation.**
