using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace RegistrationAdmin.GoogleSheets.Auth;

/// <summary>
/// 以 Windows DPAPI（CurrentUser）加密保存 OAuth token 的 IDataStore。
/// 只有同一位 Windows 使用者能解密；檔名不含帳號或 token 內容。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiDataStore : IDataStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RegistrationAdmin.OAuth.v1");
    private readonly string _folder;

    public DpapiDataStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public Task StoreAsync<T>(string key, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor<T>(key), protectedBytes);
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = PathFor<T>(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = PathFor<T>(key);
        if (!File.Exists(path))
        {
            return Task.FromResult(default(T)!);
        }

        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(Encoding.UTF8.GetString(bytes)));
        }
        catch (CryptographicException)
        {
            // 無法解密（例如換了 Windows 使用者）：刪除後要求重新授權。
            File.Delete(path);
            return Task.FromResult(default(T)!);
        }
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(_folder))
        {
            foreach (var file in Directory.EnumerateFiles(_folder, "*.bin"))
            {
                File.Delete(file);
            }
        }

        return Task.CompletedTask;
    }

    private string PathFor<T>(string key)
    {
        var raw = Encoding.UTF8.GetBytes(typeof(T).FullName + "|" + key);
        var name = Convert.ToHexStringLower(SHA256.HashData(raw))[..24];
        return Path.Combine(_folder, name + ".bin");
    }
}
