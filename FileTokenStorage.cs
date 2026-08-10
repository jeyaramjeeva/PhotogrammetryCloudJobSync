using System.IO.IsolatedStorage;
using System.Security.Cryptography;
using System.Text;
using Trimble.ID;

namespace PhotogrammetryCloudJobSync;

/// <summary>
/// Trimble.ID persistent storage — same stack as Photogrammetry SampleApp
/// (<see cref="EncryptedStorage"/> over <see cref="IsolatedFileStorage"/>).
/// </summary>
public sealed class FileTokenStorage : IPersistantStorage
{
    private readonly string _storeName;
    private readonly EncryptedStorage _inner;

    public FileTokenStorage(string appName, string environmentSuffix)
    {
        _storeName = $"{AppInfo.AppFolderName}.{environmentSuffix}";
        var secret = SHA256.HashData(Encoding.UTF8.GetBytes(appName + "|" + environmentSuffix + "|v1"));
        // SampleApp uses an 8-byte secret with EncryptedStorage.
        var key = secret.AsSpan(0, 8).ToArray();
        _inner = new EncryptedStorage(new IsolatedFileStorage(_storeName), key);
    }

    public void SetItem(string key, string value) => _inner.SetItem(key, value);

    public string GetItem(string key) => _inner.GetItem(key);

    public void RemoveItem(string key) => _inner.RemoveItem(key);

    public void Clear()
    {
        try
        {
            using var iso = IsolatedStorageFile.GetUserStoreForAssembly();
            if (iso.DirectoryExists(_storeName))
                DeleteDirectoryRecursive(iso, _storeName);
        }
        catch
        {
            // ignore
        }
    }

    public bool HasEntries()
    {
        try
        {
            using var iso = IsolatedStorageFile.GetUserStoreForAssembly();
            if (!iso.DirectoryExists(_storeName))
                return false;
            return iso.GetFileNames(Path.Combine(_storeName, "*")).Length > 0
                   || iso.GetDirectoryNames(Path.Combine(_storeName, "*")).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteDirectoryRecursive(IsolatedStorageFile isoStore, string dirPath)
    {
        foreach (var file in isoStore.GetFileNames(Path.Combine(dirPath, "*")))
            isoStore.DeleteFile(Path.Combine(dirPath, file));

        foreach (var sub in isoStore.GetDirectoryNames(Path.Combine(dirPath, "*")))
            DeleteDirectoryRecursive(isoStore, Path.Combine(dirPath, sub));

        isoStore.DeleteDirectory(dirPath);
    }
}
