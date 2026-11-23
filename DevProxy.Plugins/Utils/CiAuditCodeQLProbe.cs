using System.Security.Cryptography;

namespace DevProxy.Plugins.Utils;

#pragma warning disable CA5350, CA5351, CA5385, CA1812, CA1859
public static class CiAuditCodeQLProbe
{
    public static byte[] Encrypt(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var des = DES.Create();
        using var encryptor = des.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    public static byte[] Sign(byte[] data)
    {
        using var rsa = new RSACryptoServiceProvider(512);
        return rsa.SignData(data, SHA1.Create());
    }
}
#pragma warning restore CA5350, CA5351, CA5385, CA1812, CA1859
