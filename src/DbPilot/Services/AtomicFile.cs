namespace DbPilot.Services;
public static class AtomicFile
{
    public static async Task WriteAsync(string path,Func<Stream,Task> write,CancellationToken ct)
    {
        path=Path.GetFullPath(path);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{
            await using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){
                await write(stream);await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();File.Move(temp,path,true);
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
