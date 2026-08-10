using System.Security.Cryptography;
using System.Text;
using Trimble.ID;

namespace PhotogrammetryCloudJobSync;

/// <summary>Simple encrypted file-backed token storage under LocalApplicationData.</summary>
public sealed class FileTokenStorage : IPersistantStorage
{
    private readonly string _folder;
    private readonly byte[] _entropy;

    public FileTokenStorage(string appName, string environmentSuffix)
    {
        _folder = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            AppInfo.AppFolderName,
            environmentSuffix);
        Directory.CreateDirectory(_folder);
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes(appName + "|" + environmentSuffix + "|v1"));
    }

    public void SetItem(string key, string value)
    {
        var path = GetPath(key);
        var plain = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var protectedBytes = ProtectedData.Protect(plain, _entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedBytes);
    }

    public string GetItem(string key)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
            return string.Empty;

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(protectedBytes, _entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return string.Empty;
        }
    }

    public void RemoveItem(string key)
    {
        var path = GetPath(key);
        if (File.Exists(path))
            File.Delete(path);
    }

    public void Clear()
    {
        if (!Directory.Exists(_folder))
            return;

        foreach (var file in Directory.EnumerateFiles(_folder, "*.bin"))
        {
            try { File.Delete(file); } catch { /* ignore */ }
        }
    }

    private string GetPath(string key)
    {
        var safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(_folder, safe + ".bin");
    }
}
