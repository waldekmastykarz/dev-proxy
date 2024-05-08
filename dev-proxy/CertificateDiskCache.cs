// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License

using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;

namespace Microsoft.DevProxy;

// based on https://github.com/justcoding121/titanium-web-proxy/blob/9e71608d204e5b67085656dd6b355813929801e4/src/Titanium.Web.Proxy/Certificates/Cache/DefaultCertificateDiskCache.cs
public sealed class CertificateDiskCache : ICertificateCache
{
    private const string DefaultCertificateDirectoryName = "crts";
    private const string DefaultCertificateFileExtension = ".pfx";
    private const string DefaultRootCertificateFileName = "rootCert" + DefaultCertificateFileExtension;
    private const string ProxyConfigurationFolderName = "dev-proxy";

    private string? rootCertificatePath;

    public X509Certificate2? LoadRootCertificate(string pathOrName, string password, X509KeyStorageFlags storageFlags)
    {
        var path = GetRootCertificatePath(pathOrName, false);
        return LoadCertificate(path, password, storageFlags);
    }

    public void SaveRootCertificate(string pathOrName, string password, X509Certificate2 certificate)
    {
        var path = GetRootCertificatePath(pathOrName, true);
        var exported = certificate.Export(X509ContentType.Pkcs12, password);
        File.WriteAllBytes(path, exported);
    }

    /// <inheritdoc />
    public X509Certificate2? LoadCertificate(string subjectName, X509KeyStorageFlags storageFlags)
    {
        var filePath = Path.Combine(GetCertificatePath(false), subjectName + DefaultCertificateFileExtension);
        return LoadCertificate(filePath, string.Empty, storageFlags);
    }

    /// <inheritdoc />
    public void SaveCertificate(string subjectName, X509Certificate2 certificate)
    {
        var filePath = Path.Combine(GetCertificatePath(true), subjectName + DefaultCertificateFileExtension);
        var exported = certificate.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(filePath, exported);
    }

    public void Clear()
    {
        try
        {
            var path = GetCertificatePath(false);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception)
        {
            // do nothing
        }
    }

    private X509Certificate2? LoadCertificate(string path, string password, X509KeyStorageFlags storageFlags)
    {
        byte[] exported;

        if (!File.Exists(path)) return null;

        try
        {
            exported = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            // file or directory not found
            return null;
        }

        return new X509Certificate2(exported, password, storageFlags);
    }

    private string GetRootCertificatePath(string pathOrName, bool create)
    {
        if (Path.IsPathRooted(pathOrName)) return pathOrName;

        return Path.Combine(GetRootCertificateDirectory(create),
            string.IsNullOrEmpty(pathOrName) ? DefaultRootCertificateFileName : pathOrName);
    }

    private string GetCertificatePath(bool create)
    {
        var path = GetRootCertificateDirectory(create);

        var certPath = Path.Combine(path, DefaultCertificateDirectoryName);
        if (create && !Directory.Exists(certPath)) Directory.CreateDirectory(certPath);

        return certPath;
    }

    private string GetRootCertificateDirectory(bool create)
    {
        if (rootCertificatePath == null)
        {
            if (RunTime.IsUwpOnWindows)
            {
                rootCertificatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProxyConfigurationFolderName);
            }
            else if (RunTime.IsLinux)
            {
                rootCertificatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProxyConfigurationFolderName);
            }
            else if (RunTime.IsMac)
            {
                rootCertificatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProxyConfigurationFolderName);
            }
            else
            {
                var assemblyLocation = AppContext.BaseDirectory;

                var path = Path.GetDirectoryName(assemblyLocation);

                rootCertificatePath = path ?? throw new NullReferenceException();
            }
        }

        if (create && !Directory.Exists(rootCertificatePath))
        {
            Directory.CreateDirectory(rootCertificatePath);
        }

        return rootCertificatePath;
    }
}