using System.Net;

namespace MyVpn.Server.Core;

public static class IpUtil
{
    public static uint ToUint(string ip)
    {
        var bytes = IPAddress.Parse(ip.Trim()).GetAddressBytes();
        if (bytes.Length != 4)
            throw new FormatException($"Only IPv4 is supported here: {ip}");
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    public static string FromUint(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return new IPAddress(bytes).ToString();
    }

    public static uint Mask(int prefixLength) => prefixLength switch
    {
        <= 0 => 0u,
        >= 32 => uint.MaxValue,
        _ => uint.MaxValue << (32 - prefixLength),
    };

    public static string Network(string address, int prefixLength)
        => FromUint(ToUint(address) & Mask(prefixLength));

    public static bool IsInSubnet(string address, string network, int prefixLength)
    {
        var mask = Mask(prefixLength);
        return (ToUint(address) & mask) == (ToUint(network) & mask);
    }
}
