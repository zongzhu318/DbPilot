using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbPilot.Models;

namespace DbPilot.Services;
public sealed class WorkspaceStore
{
    private readonly string _folder;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public WorkspaceStore(string? folder = null)
    {
        _folder = folder ?? Environment.GetEnvironmentVariable("DBPILOT_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbPilot");
        Directory.CreateDirectory(_folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private byte[] Key()
    {
        var path = Path.Combine(_folder, "secret.key");
        if (!File.Exists(path))
        {
            var key = RandomNumberGenerator.GetBytes(32);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(key);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return File.ReadAllBytes(path);
    }
    private string Encrypt(string value)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12), plain = Encoding.UTF8.GetBytes(value), encrypted = new byte[plain.Length], tag = new byte[16];
        using var aes = new AesGcm(Key(),16); aes.Encrypt(nonce,plain,encrypted,tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(encrypted).ToArray());
    }
    private string Decrypt(string value)
    {
        var bytes = Convert.FromBase64String(value); if (bytes.Length < 28) throw new CryptographicException("密码数据损坏");
        var plain = new byte[bytes.Length - 28]; using var aes = new AesGcm(Key(),16);
        aes.Decrypt(bytes.AsSpan(0,12),bytes.AsSpan(28),bytes.AsSpan(12,16),plain); return Encoding.UTF8.GetString(plain);
    }
    public async Task<List<ConnectionProfile>> LoadConnectionsAsync()
    {
        var path = Path.Combine(_folder,"connections.json"); if (!File.Exists(path)) return [];
        var profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(await File.ReadAllTextAsync(path)) ?? [];
        foreach (var profile in profiles) {
            profile.Password = Decrypt(profile.Password);
            if(profile.Ssh.Password.Length>0)profile.Ssh.Password=Decrypt(profile.Ssh.Password);
            if(profile.Ssh.Passphrase.Length>0)profile.Ssh.Passphrase=Decrypt(profile.Ssh.Passphrase);
        }
        return profiles;
    }
    public async Task SaveConnectionsAsync(IEnumerable<ConnectionProfile> profiles)
    {
        // Clone before encryption: never replace passwords in live connection models.
        var clones = JsonSerializer.Deserialize<List<ConnectionProfile>>(JsonSerializer.Serialize(profiles))!;
        foreach (var profile in clones) {
            profile.Password = Encrypt(profile.Password);
            if(profile.Ssh.Password.Length>0)profile.Ssh.Password=Encrypt(profile.Ssh.Password);
            if(profile.Ssh.Passphrase.Length>0)profile.Ssh.Passphrase=Encrypt(profile.Ssh.Passphrase);
        }
        await AtomicWrite("connections.json",JsonSerializer.Serialize(clones,_json));
    }
    public async Task<AppSettings> LoadSettingsAsync()
    {
        var path = Path.Combine(_folder,"settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path)) ?? new() : new();
    }
    public Task SaveSettingsAsync(AppSettings settings) => AtomicWrite("settings.json",JsonSerializer.Serialize(settings,_json));
    public async Task<List<string>> LoadGroupsAsync()
    {
        var path=Path.Combine(_folder,"groups.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(path)) ?? [] : ["本地开发","测试环境","生产环境"];
    }
    public Task SaveGroupsAsync(IEnumerable<string> groups) => AtomicWrite("groups.json",JsonSerializer.Serialize(groups,_json));
    public async Task<List<QueryHistoryEntry>> LoadHistoryAsync()
    {
        var path=Path.Combine(_folder,"history.json");if(!File.Exists(path))return [];
        return (JsonSerializer.Deserialize<List<QueryHistoryEntry>>(await File.ReadAllTextAsync(path))??[]).Select(e=>e with{Sql=Decrypt(e.Sql)}).ToList();
    }
    public Task SaveHistoryAsync(IEnumerable<QueryHistoryEntry> entries) => AtomicWrite("history.json",JsonSerializer.Serialize(entries.Take(200).Select(e=>e with{Sql=Encrypt(e.Sql)}),_json));
    public async Task<List<SqlTemplate>> LoadTemplatesAsync()
    {
        var path=Path.Combine(_folder,"templates.json");if(!File.Exists(path))return [];
        return (JsonSerializer.Deserialize<List<SqlTemplate>>(await File.ReadAllTextAsync(path))??[]).Select(t=>t with{Sql=Decrypt(t.Sql)}).ToList();
    }
    public Task SaveTemplatesAsync(IEnumerable<SqlTemplate> templates)=>AtomicWrite("templates.json",JsonSerializer.Serialize(templates.Select(t=>t with{Sql=Encrypt(t.Sql)}),_json));
    public async Task<List<string>> LoadLogsAsync()
    {
        var path=Path.Combine(_folder,"logs.json");if(!File.Exists(path))return [];
        return (JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(path))??[]).Take(500).Select(Decrypt).ToList();
    }
    public Task SaveLogsAsync(IEnumerable<string> logs)=>AtomicWrite("logs.json",JsonSerializer.Serialize(logs.Take(500).Select(Encrypt),_json));
    private async Task AtomicWrite(string name, string text)
    {
        var path = Path.Combine(_folder,name); var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp,text); File.Move(temp,path,true);
    }
}
