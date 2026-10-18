using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace DbStudio.Services;
public sealed class SqlCompletionData(string text,string description,string? insertion=null) : ICompletionData
{
    public IImage? Image=>null;
    public string Text=>text;
    public object Content=>text;
    public object Description=>description;
    public double Priority=>0;
    public void Complete(TextArea textArea,ISegment completionSegment,EventArgs insertionRequestEventArgs)=>textArea.Document.Replace(completionSegment,insertion??text);
}
