using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using DbPilot.Models;
using DbPilot.Services;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    private async void WorkspaceBackup(object? sender,RoutedEventArgs e)
    {
        if(_vm.Busy)return;
        var dialog=DialogUi.Dialog("加密配置备份",640);dialog.SizeToContent=SizeToContent.Height;
        var password=new TextBox{PasswordChar='●',Watermark="备份口令（至少 10 个字符）"};var status=DialogUi.Status();
        bool busy=false;
        var export=DialogUi.Primary("导出加密备份",async()=>{
            if(busy)return;busy=true;try{
                var backup=new WorkspaceBackup(_vm.Connections.ToList(),_vm.Groups.ToList(),await _vm.Store.LoadTemplatesAsync());
                var bytes=await Task.Run(()=>PortableWorkspace.Encrypt(backup,password.Text??""));
                var file=await dialog.StorageProvider.SaveFilePickerAsync(new(){Title="保存加密备份",SuggestedFileName="DbPilot-workspace.dbbackup",DefaultExtension="dbbackup"});if(file is null)return;
                var path=file.TryGetLocalPath()??throw new InvalidOperationException("请选择本地文件。");await AtomicFile.WriteAsync(path,s=>s.WriteAsync(bytes).AsTask(),default);status.Text="加密备份已保存。请妥善保管口令。";
            }catch(Exception ex){status.Text=ex.Message;}finally{busy=false;}
        });
        var import=DialogUi.Secondary("导入备份",async()=>{
            if(busy)return;busy=true;try{
                var files=await dialog.StorageProvider.OpenFilePickerAsync(new(){Title="选择配置备份",AllowMultiple=false});if(files.Count==0)return;
                await using var stream=await files[0].OpenReadAsync();using var buffer=new MemoryStream();var chunk=new byte[16384];int read;
                while((read=await stream.ReadAsync(chunk))>0){if(buffer.Length+read>16*1024*1024)throw new FormatException("配置备份超过 16 MB。");await buffer.WriteAsync(chunk.AsMemory(0,read));}
                var backup=await Task.Run(()=>PortableWorkspace.Decrypt(buffer.ToArray(),password.Text??""));
                // Imported IDs are always fresh so active tabs and existing histories keep their original connections.
                foreach(var profile in backup.Connections){profile.Id=Guid.NewGuid();profile.Name+="（导入）";}
                var profiles=_vm.Connections.Concat(backup.Connections).ToList();var groups=_vm.Groups.Union(backup.Groups).Union(profiles.Select(p=>p.Group)).ToList();
                var templates=(await _vm.Store.LoadTemplatesAsync()).ToList();foreach(var template in backup.Templates){var name=template.Name;while(templates.Any(t=>t.Name==name))name+="（导入）";templates.Add(template with{Name=name});}
                await _vm.Store.SaveTemplatesAsync(templates);await _vm.Store.SaveGroupsAsync(groups);await _vm.Store.SaveConnectionsAsync(profiles);
                _vm.Connections.Clear();foreach(var profile in profiles)_vm.Connections.Add(profile);_vm.Groups.Clear();foreach(var group in groups)_vm.Groups.Add(group);_vm.RebuildTree();status.Text=$"已追加 {backup.Connections.Count} 个连接与 {backup.Templates.Count} 个模板。";
            }catch(Exception ex){status.Text=ex.Message;}finally{busy=false;}
        });
        var body=new StackPanel{Margin=new Thickness(24,20)};
        body.Children.Add(new TextBlock{Text="备份包含连接密码、SSH 凭据和自定义 SQL 模板。导入会追加连接，保留现有配置。",Classes={"hint"},Margin=new Thickness(0,0,0,16)});
        body.Children.Add(DialogUi.Field("备份口令",password,110,360));
        dialog.Content=DialogUi.Shell("加密配置备份","连接密码、SSH 凭据与自定义 SQL 模板会一并加密导出。",body,DialogUi.Footer(status,import,export));
        dialog.Closing+=(_,args)=>{if(busy){args.Cancel=true;status.Text="正在处理配置，请等待完成。";}};await dialog.ShowDialog(this);
    }
}
