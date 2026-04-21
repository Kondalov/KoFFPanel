namespace KoFFPanel.Application.Interfaces;

public record struct ServerResources(
    int Cpu, 
    int Ram, 
    int Ssd, 
    string Uptime, 
    string LoadAvg, 
    string NetworkSpeed, 
    int XrayProcesses, 
    int TcpConnections, 
    int SynRecv, 
    int ErrorRate
);
