using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using DbPilot.Models;
using DbPilot.ViewModels;
using DbPilot.Views;

namespace DbPilot.Checks;
internal static class CompletionChecks
{
    public static void Run(MainWindow window,MainViewModel vm,Action<bool,string> check)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var editor=window.FindControl<TextEditor>("SqlEditor")!;var profile=new ConnectionProfile{Kind=DatabaseKind.PostgreSQL,Database="completion_test"};
        var original=vm.Tab!.Connection;string originalText=editor.Text;vm.Tab.Connection=profile;
        string key=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile)));
        var objects=(Dictionary<string,(DateTime,IReadOnlyList<DbObject>)>)typeof(MainWindow).GetField("_objectSuggestions",flags)!.GetValue(window)!;
        var columns=(Dictionary<string,(DateTime,IReadOnlyList<ColumnInfo>)>)typeof(MainWindow).GetField("_columnSuggestions",flags)!.GetValue(window)!;
        objects[key]=(DateTime.UtcNow,[new("completion_test","public","users","表"),new("completion_test","public","orders","表")]);
        var databases=(Dictionary<Guid,IReadOnlyList<string>>)typeof(MainWindow).GetField("_databaseLists",flags)!.GetValue(window)!;databases[profile.Id]=[profile.Database];
        columns[key+"|public|users"]=(DateTime.UtcNow,[new("name","text",true,false,"","用户名称"),new("Display Name","text",true,false,"","")]);
        columns[key+"|public|orders"]=(DateTime.UtcNow,[new("total","numeric",true,false,"","")]);
        CompletionWindow Open(string sql,int caret){editor.Text=sql;editor.CaretOffset=caret;((Task)typeof(MainWindow).GetMethod("ShowCompletion",flags)!.Invoke(window,[false])!).GetAwaiter().GetResult();return (CompletionWindow)typeof(MainWindow).GetField("_completion",flags)!.GetValue(window)!;}
        try{
            var popup=Open("SELECT u. FROM public.users u JOIN public.orders o ON true",9);
            var names=popup.CompletionList.CompletionData.Select(d=>d.Text).ToArray();
            check(names.Contains("name")&&names.Contains("Display Name")&&!names.Contains("total")&&!names.Contains("SELECT"),"real completion popup scopes columns to selected alias");
            var item=popup.CompletionList.CompletionData.Single(d=>d.Text=="Display Name");item.Complete(editor.TextArea,new AvaloniaEdit.Document.SimpleSegment(9,0),EventArgs.Empty);
            check(editor.Text.StartsWith("SELECT u.\"Display Name\" FROM"),"completion inserts quoted field while preserving qualifier and remaining SQL");popup.Close();
            popup=Open("SELECT * FROM public.us",23);check(popup.CompletionList.CompletionData.Single().Text=="users","schema dot completion suggests matching table");popup.Close();
            popup=Open("SELECT * FROM public.users WHERE na",34);check(popup.CompletionList.CompletionData.Any(d=>d.Text=="name")&&!popup.CompletionList.CompletionData.Any(d=>d.Text=="users"),"WHERE completion suggests fields rather than table names");popup.Close();
            popup=Open("SELECT * FROM us",16);check(popup.CompletionList.CompletionData.Single().Text=="users","FROM completion suggests table rather than columns or keywords");popup.Close();
            popup=Open("SEL",3);check(popup.CompletionList.CompletionData.Any(d=>d.Text=="SELECT"),"manual completion suggests SQL keyword");Dispatcher.UIThread.RunJobs();
            check(popup.CompletionList.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="SELECT"&&t.Bounds.Width>0&&t.Bounds.Height>0),"completion candidate text is actually rendered");
            if(Environment.GetEnvironmentVariable("DBPILOT_COMPLETION_PREVIEW") is {} preview && popup.CompletionList.GetVisualRoot() is TopLevel root){using var frame=root.CaptureRenderedFrame();frame?.Save(preview);}popup.Close();
            editor.Text="";editor.CaretOffset=0;editor.Focus();window.KeyTextInput("se");
            var timer=System.Diagnostics.Stopwatch.StartNew();
            while(typeof(MainWindow).GetField("_completion",flags)!.GetValue(window) is null&&timer.ElapsedMilliseconds<2000){Dispatcher.UIThread.RunJobs();Thread.Sleep(10);}
            popup=(CompletionWindow)typeof(MainWindow).GetField("_completion",flags)!.GetValue(window)!;
            check(popup is not null&&popup.CompletionList.CompletionData.Any(d=>d.Text=="SELECT"),"typing opens automatic keyword completion through real editor input");
            window.KeyPressQwerty(PhysicalKey.Tab,RawInputModifiers.None);window.KeyReleaseQwerty(PhysicalKey.Tab,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
            check(editor.Text=="SELECT","Tab accepts suggestion and replaces typed prefix");
            popup=Open("SE",2);window.KeyPressQwerty(PhysicalKey.ArrowDown,RawInputModifiers.None);window.KeyReleaseQwerty(PhysicalKey.ArrowDown,RawInputModifiers.None);window.KeyPressQwerty(PhysicalKey.Enter,RawInputModifiers.None);window.KeyReleaseQwerty(PhysicalKey.Enter,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
            check(editor.Text=="SERIAL","arrow and Enter choose and accept another suggestion");
            popup=Open("SE",2);window.KeyPressQwerty(PhysicalKey.Escape,RawInputModifiers.None);window.KeyReleaseQwerty(PhysicalKey.Escape,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
            check(editor.Text=="SE"&&typeof(MainWindow).GetField("_completion",flags)!.GetValue(window) is null,"Escape closes completion without modifying SQL");
            editor.Text="SELECT * FROM";editor.CaretOffset=editor.Text.Length;editor.Focus();window.KeyTextInput(" ");timer.Restart();
            while(typeof(MainWindow).GetField("_completion",flags)!.GetValue(window) is null&&timer.ElapsedMilliseconds<2000){Dispatcher.UIThread.RunJobs();Thread.Sleep(10);}
            popup=(CompletionWindow)typeof(MainWindow).GetField("_completion",flags)!.GetValue(window)!;
            check(popup is not null&&popup.CompletionList.CompletionData.Any(d=>d.Text=="users")&&!popup.CompletionList.CompletionData.Any(d=>d.Text=="SELECT"),"space after FROM automatically opens table suggestions");popup!.Close();
        }finally{vm.Tab.Connection=original;editor.Text=originalText;objects.Clear();columns.Clear();}
    }
}
