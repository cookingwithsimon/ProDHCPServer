namespace ProDHCPServer.Core.Dhcp;

public enum DhcpMessageType : byte
{
    Discover = 1,
    Offer = 2,
    Request = 3,
    Decline = 4,
    Ack = 5,
    Nak = 6,
    Release = 7,
    Inform = 8,
}

public enum BootOp : byte
{
    Request = 1,
    Reply = 2,
}

/// <summary>DHCP option codes used by the server (RFC 2132 and friends).</summary>
public static class DhcpOptionCode
{
    public const byte Pad = 0;
    public const byte SubnetMask = 1;
    public const byte Router = 3;
    public const byte DnsServers = 6;
    public const byte HostName = 12;
    public const byte DomainName = 15;
    public const byte BroadcastAddress = 28;
    public const byte NtpServers = 42;
    public const byte VendorSpecific = 43;
    public const byte RequestedIp = 50;
    public const byte LeaseTime = 51;
    public const byte OptionOverload = 52;
    public const byte MessageType = 53;
    public const byte ServerIdentifier = 54;
    public const byte ParameterRequestList = 55;
    public const byte Message = 56;
    public const byte MaxMessageSize = 57;
    public const byte RenewalTime = 58;
    public const byte RebindingTime = 59;
    public const byte VendorClassId = 60;
    public const byte ClientId = 61;
    public const byte TftpServerName = 66;
    public const byte BootFileName = 67;
    public const byte TftpServerAddress = 150;
    public const byte End = 255;

    public static string Describe(byte code) => code switch
    {
        SubnetMask => "Subnet mask",
        Router => "Router (gateway)",
        DnsServers => "DNS servers",
        HostName => "Host name",
        DomainName => "Domain name",
        BroadcastAddress => "Broadcast address",
        NtpServers => "NTP servers",
        VendorSpecific => "Vendor specific",
        RequestedIp => "Requested IP",
        LeaseTime => "Lease time",
        OptionOverload => "Option overload",
        MessageType => "Message type",
        ServerIdentifier => "Server identifier",
        ParameterRequestList => "Parameter request list",
        Message => "Message",
        MaxMessageSize => "Max message size",
        RenewalTime => "Renewal (T1) time",
        RebindingTime => "Rebinding (T2) time",
        VendorClassId => "Vendor class",
        ClientId => "Client identifier",
        TftpServerName => "TFTP server name",
        BootFileName => "Boot file name",
        TftpServerAddress => "TFTP server address",
        _ => $"Option {code}",
    };
}

public static class DhcpPorts
{
    public const int Server = 67;
    public const int Client = 68;
}
