using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.ViewModels;

namespace DbPilot.Views;
public sealed class ConnectionDialog : Window
{
    private readonly TextBox _name=new(),_host=new(),_port=new(),_database=new(),_username=new(),_password=new(){PasswordChar='●'},_timeout=new(),_timezone=new(),_group=new();
    private readonly ComboBox _kind=new(){ItemsSource=Enum.GetValues<DatabaseKind>()};
    private readonly CheckBox _ssl=new(){Content="启用 SSL（验证服务器证书）"},_integrated=new(){Content="Windows 身份验证（仅 SQL Server）"};
    private readonly CheckBox _publicKey=new(){Content="允许获取 MySQL RSA 公钥（仅可信服务器）"};
    private readonly TextBlock _message=new(){TextWrapping=Avalonia.Media.TextWrapping.Wrap};
    private readonly Guid _id;
    private readonly CheckBox _ssh=new(){Content="通过 SSH 隧道连接"};
    private readonly TextBox _sshHost=new(),_sshPort=new(),_sshUser=new(),_sshPassword=new(){PasswordChar='●'},_keyPath=new(),_passphrase=new(){PasswordChar='●'},_fingerprint=new(),_poolMin=new(),_poolMax=new();
    private CancellationTokenSource? _test;
    public ConnectionDialog(ConnectionProfile? original=null)
    {
        Title=original is null ? "新建连接" : "编辑连接"; Width=580; Height=770; CanResize=false; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var p=original ?? new ConnectionProfile(); _id=p.Id;
        _name.Text=p.Name;_group.Text=p.Group;_host.Text=p.Host;_port.Text=p.Port.ToString();_database.Text=p.Database;_username.Text=p.Username;_password.Text=p.Password;_timeout.Text=p.Timeout.ToString();_timezone.Text=p.Timezone;
        _kind.SelectedItem=p.Kind; _ssl.IsChecked=p.Ssl; _publicKey.IsChecked=p.AllowPublicKeyRetrieval; _integrated.IsChecked=p.IntegratedSecurity;
        _poolMin.Text=p.MinimumPoolSize.ToString();_poolMax.Text=p.MaximumPoolSize.ToString();_ssh.IsChecked=p.Ssh.Enabled;
        _sshHost.Text=p.Ssh.Host;_sshPort.Text=p.Ssh.Port.ToString();_sshUser.Text=p.Ssh.Username;_sshPassword.Text=p.Ssh.Password;_keyPath.Text=p.Ssh.PrivateKeyPath;_passphrase.Text=p.Ssh.Passphrase;_fingerprint.Text=p.Ssh.HostKeyFingerprint;
        var panel=new StackPanel{Margin=new Thickness(26),Spacing=8};
        panel.Children.Add(new TextBlock{Text="连接到数据库",FontSize=23,FontWeight=Avalonia.Media.FontWeight.SemiBold});
        panel.Children.Add(new TextBlock{Text="配置服务器信息，测试后保存。TDengine 使用 taosAdapter REST。",FontSize=11,Opacity=0.6,TextWrapping=Avalonia.Media.TextWrapping.Wrap});
        Add(panel,"连接名称",_name);Add(panel,"分组（可直接输入新分组名称）",_group);Add(panel,"数据库类型",_kind);
        Add(panel,"主机地址",_host);Add(panel,"端口",_port);Add(panel,"默认数据库",_database);Add(panel,"用户名",_username);Add(panel,"密码",_password);Add(panel,"连接超时（秒）",_timeout);Add(panel,"TDengine 时区",_timezone);
        panel.Children.Add(_ssl);panel.Children.Add(_publicKey);panel.Children.Add(_integrated);Add(panel,"最小连接池",_poolMin);Add(panel,"最大连接池",_poolMax);
        panel.Children.Add(_ssh);Add(panel,"SSH 主机",_sshHost);Add(panel,"SSH 端口",_sshPort);Add(panel,"SSH 用户名",_sshUser);Add(panel,"SSH 密码",_sshPassword);Add(panel,"私钥文件（优先使用）",_keyPath);Add(panel,"私钥口令",_passphrase);Add(panel,"SSH 指纹（SHA256:…）",_fingerprint);
        panel.Children.Add(new TextBlock{Text="首次测试会显示 SSH 指纹，核对后填入。数据库主机填写从 SSH 服务器访问的地址。隧道连接不复用连接池。",TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontSize=11});panel.Children.Add(_message);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Right};
        var test=new Button{Content="测试连接"};var save=new Button{Content="保存连接"};save.Classes.Add("primary");var cancel=new Button{Content="取消"};
        test.Click+=async (_,_)=> {
            try { var profile=Read(); test.IsEnabled=false; _test=new CancellationTokenSource(TimeSpan.FromSeconds(profile.Timeout));
                await using var provider=DbProviderFactory.Create(profile); await provider.TestAsync(_test.Token); _message.Text="连接成功，可以保存。";
            } catch(OperationCanceledException) { _message.Text="连接测试超时或已取消。"; }
            catch(Exception ex) { _message.Text="连接失败："+ex.Message; }
            finally { test.IsEnabled=true;_test?.Dispose();_test=null; }
        };
        save.Click+=(_,_)=>{try { Close(Read()); }catch(Exception ex){_message.Text=ex.Message;}};
        cancel.Click+=(_,_)=>Close();Closed+=(_,_)=>_test?.Cancel();
        actions.Children.Add(test);actions.Children.Add(cancel);actions.Children.Add(save);panel.Children.Add(actions);
        Content=new ScrollViewer{Content=panel};
        _kind.SelectionChanged+=(_,_)=>{
            var kind=(DatabaseKind)_kind.SelectedItem!;
            _port.Text=(kind switch{DatabaseKind.MySQL=>3306,DatabaseKind.PostgreSQL=>5432,DatabaseKind.SqlServer=>1433,_=>6041}).ToString();
            _username.Text=kind switch{DatabaseKind.PostgreSQL=>"postgres",DatabaseKind.SqlServer=>"sa",_=>"root"};
        };
    }
    private static void Add(StackPanel panel,string label,Control control)
    {var row=new Grid{ColumnDefinitions=new ColumnDefinitions("185,*")};row.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,FontSize=11});Grid.SetColumn(control,1);row.Children.Add(control);panel.Children.Add(row);}
    private ConnectionProfile Read()
    {
        if(string.IsNullOrWhiteSpace(_name.Text)||string.IsNullOrWhiteSpace(_host.Text)||string.IsNullOrWhiteSpace(_group.Text)) throw new InvalidOperationException("请填写连接名称、分组和主机地址。");
        if(!int.TryParse(_port.Text,out var port)||port is <1 or >65535)throw new InvalidOperationException("端口必须在 1–65535 之间。");
        if(!int.TryParse(_timeout.Text,out var timeout)||timeout is <1 or >300)throw new InvalidOperationException("超时必须在 1–300 秒之间。");
        if(!int.TryParse(_poolMin.Text,out var min)||!int.TryParse(_poolMax.Text,out var max)||min<0||max<1||max>10000||min>max)throw new InvalidOperationException("连接池范围应满足 0 ≤ 最小值 ≤ 最大值 ≤ 10000，最大值至少为 1。");
        if(!int.TryParse(_sshPort.Text,out var sshPort)||sshPort is <1 or >65535)throw new InvalidOperationException("SSH 端口无效。");
        return new ConnectionProfile{Id=_id,Name=_name.Text.Trim(),Group=_group.Text.Trim(),Kind=(DatabaseKind)_kind.SelectedItem!,Host=_host.Text.Trim(),Port=port,Database=_database.Text?.Trim()??"",Username=_username.Text??"",Password=_password.Text??"",Timeout=timeout,Ssl=_ssl.IsChecked==true,AllowPublicKeyRetrieval=_publicKey.IsChecked==true,IntegratedSecurity=_integrated.IsChecked==true,Timezone=_timezone.Text??"Asia/Shanghai",MinimumPoolSize=min,MaximumPoolSize=max,
            Ssh=new(){Enabled=_ssh.IsChecked==true,Host=_sshHost.Text?.Trim()??"",Port=sshPort,Username=_sshUser.Text??"",Password=_sshPassword.Text??"",PrivateKeyPath=_keyPath.Text??"",Passphrase=_passphrase.Text??"",HostKeyFingerprint=_fingerprint.Text?.Trim()??""}};
    }
}
