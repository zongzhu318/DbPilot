using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.ViewModels;

namespace DbPilot.Views;
public sealed class ConnectionDialog : Window
{
    private readonly TextBox _name=new(),_host=new(),_port=new(),_database=new(),_username=new(),_password=new(){PasswordChar='●'},_timeout=new(),_timezone=new();
    private readonly ComboBox _kind=new(){ItemsSource=Enum.GetValues<DatabaseKind>()};
    private readonly CheckBox _ssl=new(){Content="启用 SSL（验证服务器证书）"},_integrated=new(){Content="Windows 身份验证（仅 SQL Server）"};
    private readonly CheckBox _publicKey=new(){Content="允许获取 MySQL RSA 公钥（仅可信服务器）"};
    private readonly TextBlock _message=DialogUi.Status();
    private readonly Guid _id;
    private readonly string _legacyGroup;
    private readonly CheckBox _ssh=new(){Content="通过 SSH 隧道连接"};
    private readonly TextBox _sshHost=new(),_sshPort=new(),_sshUser=new(),_sshPassword=new(){PasswordChar='●'},_keyPath=new(),_passphrase=new(){PasswordChar='●'},_fingerprint=new(),_poolMin=new(),_poolMax=new();
    private CancellationTokenSource? _test;
    public ConnectionDialog(ConnectionProfile? original=null)
    {
        var heading=original is null ? "新建连接" : "编辑连接";
        Title=heading; Width=660; Height=780; MinWidth=620; MinHeight=640; CanResize=false; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Classes.Add("dialog");
        var p=original ?? new ConnectionProfile(); _id=p.Id;_legacyGroup=p.Group;
        _name.Text=p.Name;_host.Text=p.Host;_port.Text=p.Port.ToString();_database.Text=p.Database;_username.Text=p.Username;_password.Text=p.Password;_timeout.Text=p.Timeout.ToString();_timezone.Text=p.Timezone;
        _kind.SelectedItem=p.Kind; _ssl.IsChecked=p.Ssl; _publicKey.IsChecked=p.AllowPublicKeyRetrieval; _integrated.IsChecked=p.IntegratedSecurity;
        _poolMin.Text=p.MinimumPoolSize.ToString();_poolMax.Text=p.MaximumPoolSize.ToString();_ssh.IsChecked=p.Ssh.Enabled;
        _sshHost.Text=p.Ssh.Host;_sshPort.Text=p.Ssh.Port.ToString();_sshUser.Text=p.Ssh.Username;_sshPassword.Text=p.Ssh.Password;_keyPath.Text=p.Ssh.PrivateKeyPath;_passphrase.Text=p.Ssh.Passphrase;_fingerprint.Text=p.Ssh.HostKeyFingerprint;

        var form=new StackPanel{Margin=new Thickness(24,18,24,8)};
        form.Children.Add(Section("服务器"));
        form.Children.Add(DialogUi.Field("连接名称",_name));
        form.Children.Add(DialogUi.Field("数据库类型",_kind));
        form.Children.Add(DialogUi.Field("主机地址",_host));
        form.Children.Add(DialogUi.Field("端口",_port,controlWidth:120));
        form.Children.Add(DialogUi.Field("默认数据库",_database));
        form.Children.Add(DialogUi.Field("用户名",_username));
        form.Children.Add(DialogUi.Field("密码",_password));

        form.Children.Add(DialogUi.Divider());
        form.Children.Add(Section("连接选项"));
        form.Children.Add(DialogUi.Field("连接超时（秒）",_timeout,controlWidth:120));
        form.Children.Add(DialogUi.Field("TDengine 时区",_timezone,controlWidth:260));
        form.Children.Add(DialogUi.Option(_ssl));
        form.Children.Add(DialogUi.Option(_publicKey));
        form.Children.Add(DialogUi.Option(_integrated));
        form.Children.Add(DialogUi.Field("最小连接池",_poolMin,controlWidth:120));
        form.Children.Add(DialogUi.Field("最大连接池",_poolMax,controlWidth:120));

        form.Children.Add(DialogUi.Divider());
        form.Children.Add(Section("SSH 隧道"));
        form.Children.Add(DialogUi.Option(_ssh));
        form.Children.Add(DialogUi.Field("SSH 主机",_sshHost));
        form.Children.Add(DialogUi.Field("SSH 端口",_sshPort,controlWidth:120));
        form.Children.Add(DialogUi.Field("SSH 用户名",_sshUser));
        form.Children.Add(DialogUi.Field("SSH 密码",_sshPassword));
        form.Children.Add(DialogUi.Field("私钥文件（优先使用）",_keyPath));
        form.Children.Add(DialogUi.Field("私钥口令",_passphrase));
        form.Children.Add(DialogUi.Field("SSH 指纹（SHA256:…）",_fingerprint));
        form.Children.Add(new TextBlock{Text="首次测试会显示 SSH 指纹，核对后填入。数据库主机填写从 SSH 服务器访问的地址。隧道连接不复用连接池。",Classes={"hint"},Margin=new Thickness(0,4,0,0)});

        var body=new ScrollViewer{Content=form,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var test=new Button{Content="测试连接"};
        test.Click+=async (_,_)=> {
            try { var profile=Read(); test.IsEnabled=false; _test=new CancellationTokenSource(TimeSpan.FromSeconds(profile.Timeout));
                await using var provider=DbProviderFactory.Create(profile); await provider.TestAsync(_test.Token); DialogUi.Info(_message,"连接成功，可以保存。");
            } catch(OperationCanceledException) { DialogUi.Error(_message,"连接测试超时或已取消。"); }
            catch(Exception ex) { DialogUi.Error(_message,"连接失败："+ex.Message); }
            finally { test.IsEnabled=true;_test?.Dispose();_test=null; }
        };
        var cancel=DialogUi.Secondary("取消",()=>Close());
        var save=DialogUi.Primary("保存连接",()=>{try { Close(Read()); }catch(Exception ex){DialogUi.Error(_message,ex.Message);}});
        Closed+=(_,_)=>_test?.Cancel();
        Content=DialogUi.Shell(heading,"配置服务器信息，测试通过后保存；TDengine 使用 taosAdapter REST。",body,DialogUi.Footer(_message,test,cancel,save));
        _kind.SelectionChanged+=(_,_)=>{
            var kind=(DatabaseKind)_kind.SelectedItem!;
            _port.Text=(kind switch{DatabaseKind.MySQL=>3306,DatabaseKind.PostgreSQL=>5432,DatabaseKind.SqlServer=>1433,_=>6041}).ToString();
            _username.Text=kind switch{DatabaseKind.PostgreSQL=>"postgres",DatabaseKind.SqlServer=>"sa",_=>"root"};
        };
    }
    private static TextBlock Section(string title)=>new(){Text=title,Classes={"sectionTitle"},Margin=new Thickness(0,0,0,12)};
    private ConnectionProfile Read()
    {
        if(string.IsNullOrWhiteSpace(_name.Text)||string.IsNullOrWhiteSpace(_host.Text)) throw new InvalidOperationException("请填写连接名称和主机地址。");
        if(!int.TryParse(_port.Text,out var port)||port is <1 or >65535)throw new InvalidOperationException("端口必须在 1–65535 之间。");
        if(!int.TryParse(_timeout.Text,out var timeout)||timeout is <1 or >300)throw new InvalidOperationException("超时必须在 1–300 秒之间。");
        if(!int.TryParse(_poolMin.Text,out var min)||!int.TryParse(_poolMax.Text,out var max)||min<0||max<1||max>10000||min>max)throw new InvalidOperationException("连接池范围应满足 0 ≤ 最小值 ≤ 最大值 ≤ 10000，最大值至少为 1。");
        if(!int.TryParse(_sshPort.Text,out var sshPort)||sshPort is <1 or >65535)throw new InvalidOperationException("SSH 端口无效。");
        return new ConnectionProfile{Id=_id,Name=_name.Text.Trim(),Group=_legacyGroup,Kind=(DatabaseKind)_kind.SelectedItem!,Host=_host.Text.Trim(),Port=port,Database=_database.Text?.Trim()??"",Username=_username.Text??"",Password=_password.Text??"",Timeout=timeout,Ssl=_ssl.IsChecked==true,AllowPublicKeyRetrieval=_publicKey.IsChecked==true,IntegratedSecurity=_integrated.IsChecked==true,Timezone=_timezone.Text??"Asia/Shanghai",MinimumPoolSize=min,MaximumPoolSize=max,
            Ssh=new(){Enabled=_ssh.IsChecked==true,Host=_sshHost.Text?.Trim()??"",Port=sshPort,Username=_sshUser.Text??"",Password=_sshPassword.Text??"",PrivateKeyPath=_keyPath.Text??"",Passphrase=_passphrase.Text??"",HostKeyFingerprint=_fingerprint.Text?.Trim()??""}};
    }
}
