using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbPilot.Models;

namespace DbPilot.Services;
public sealed record WorkspaceBackup(List<ConnectionProfile> Connections,List<string> Groups,List<SqlTemplate> Templates);
public static class PortableWorkspace
{
    private sealed record Envelope(int Version,int Iterations,string Salt,string Nonce,string Tag,string Ciphertext);
    public static byte[] Encrypt(WorkspaceBackup backup,string password)
    {
        if(password.Length<10)throw new ArgumentException("备份口令至少需要 10 个字符。");
        byte[] salt=RandomNumberGenerator.GetBytes(16),nonce=RandomNumberGenerator.GetBytes(12),tag=new byte[16];
        byte[] plain=JsonSerializer.SerializeToUtf8Bytes(backup),cipher=new byte[plain.Length];
        var key=Rfc2898DeriveBytes.Pbkdf2(password,salt,210000,HashAlgorithmName.SHA256,32);
        try{using var aes=new AesGcm(key,16);aes.Encrypt(nonce,plain,cipher,tag,"DbPilot workspace v1"u8);return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,210000,Convert.ToBase64String(salt),Convert.ToBase64String(nonce),Convert.ToBase64String(tag),Convert.ToBase64String(cipher)));}
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
    public static WorkspaceBackup Decrypt(byte[] bytes,string password)
    {
        if(bytes.Length>16*1024*1024)throw new FormatException("配置备份超过 16 MB。");
        var envelope=JsonSerializer.Deserialize<Envelope>(bytes)??throw new FormatException("备份文件无效。");
        if(envelope.Version!=1||envelope.Iterations!=210000)throw new FormatException("不支持的配置备份版本。");
        var salt=Convert.FromBase64String(envelope.Salt);var nonce=Convert.FromBase64String(envelope.Nonce);var tag=Convert.FromBase64String(envelope.Tag);var cipher=Convert.FromBase64String(envelope.Ciphertext);
        if(salt.Length!=16||nonce.Length!=12||tag.Length!=16)throw new FormatException("配置备份格式损坏。");
        var key=Rfc2898DeriveBytes.Pbkdf2(password,salt,envelope.Iterations,HashAlgorithmName.SHA256,32);var plain=new byte[cipher.Length];
        try{using var aes=new AesGcm(key,16);aes.Decrypt(nonce,cipher,tag,plain,"DbPilot workspace v1"u8);
            var result=JsonSerializer.Deserialize<WorkspaceBackup>(plain)??throw new FormatException("配置数据为空。");
            if(result.Connections is null||result.Groups is null||result.Templates is null||result.Connections.Count>10000)throw new FormatException("配置备份数据无效。");return result;
        }catch(CryptographicException ex){throw new CryptographicException("备份口令错误或文件已被篡改。",ex);}
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
}
