using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DbPilot.Services;

// Minimal SpreadsheetML reader/writer: no third-party package and no temporary files.
public static class XlsxWorkbook
{
    public const int MaxRows = 1_048_576;
    public const int MaxCellText = 32_767;
    private const string MainNs="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PackageRelNs="http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRelNs="http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static async Task<long> WriteAsync(Stream output,IReadOnlyList<string> columns,IAsyncEnumerable<object?[]> rows,string? sheetName,CancellationToken ct)
    {
        if(columns.Count==0)throw new ArgumentException("Excel 导出至少需要一列。",nameof(columns));
        if(columns.Count>16_384)throw new ArgumentException("Excel 单表最多支持 16384 列。",nameof(columns));
        using var archive=new ZipArchive(output,ZipArchiveMode.Create,leaveOpen:true);
        await AddTextAsync(archive,"[Content_Types].xml",ContentTypes,ct);
        await AddTextAsync(archive,"_rels/.rels",PackageRelationships,ct);
        var name=SheetName(sheetName);
        await AddTextAsync(archive,"xl/workbook.xml",$"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{MainNs}\" xmlns:r=\"{OfficeRelNs}\"><sheets><sheet name=\"{Xml(name)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",ct);
        await AddTextAsync(archive,"xl/_rels/workbook.xml.rels",$"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{PackageRelNs}\"><Relationship Id=\"rId1\" Type=\"{OfficeRelNs}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"{OfficeRelNs}/styles\" Target=\"styles.xml\"/></Relationships>",ct);
        await AddTextAsync(archive,"xl/styles.xml",Styles,ct);

        long count=0;
        var entry=archive.CreateEntry("xl/worksheets/sheet1.xml",CompressionLevel.Fastest);
        await using(var stream=entry.Open())
        using(var writer=XmlWriter.Create(stream,new XmlWriterSettings{Async=true,Encoding=new UTF8Encoding(false),CloseOutput=false}))
        {
            await writer.WriteStartDocumentAsync();
            await writer.WriteStartElementAsync(null,"worksheet",MainNs);
            await writer.WriteStartElementAsync(null,"sheetViews",MainNs);
            await writer.WriteStartElementAsync(null,"sheetView",MainNs);
            await writer.WriteAttributeStringAsync(null,"workbookViewId",null,"0");
            await writer.WriteStartElementAsync(null,"pane",MainNs);
            await writer.WriteAttributeStringAsync(null,"ySplit",null,"1");
            await writer.WriteAttributeStringAsync(null,"topLeftCell",null,"A2");
            await writer.WriteAttributeStringAsync(null,"activePane",null,"bottomLeft");
            await writer.WriteAttributeStringAsync(null,"state",null,"frozen");
            await writer.WriteEndElementAsync();
            await writer.WriteEndElementAsync();
            await writer.WriteEndElementAsync();
            await writer.WriteStartElementAsync(null,"cols",MainNs);
            await writer.WriteStartElementAsync(null,"col",MainNs);
            await writer.WriteAttributeStringAsync(null,"min",null,"1");
            await writer.WriteAttributeStringAsync(null,"max",null,columns.Count.ToString(CultureInfo.InvariantCulture));
            await writer.WriteAttributeStringAsync(null,"width",null,"18");
            await writer.WriteAttributeStringAsync(null,"customWidth",null,"1");
            await writer.WriteEndElementAsync();
            await writer.WriteEndElementAsync();
            await writer.WriteStartElementAsync(null,"sheetData",MainNs);
            await WriteRowAsync(writer,1,columns.Cast<object?>().ToArray(),true,ct);
            await foreach(var row in rows.WithCancellation(ct)){
                count++;
                if(count+1>MaxRows)throw new InvalidOperationException($"Excel 单表最多支持 {MaxRows} 行，请改用 CSV 或分批导出。");
                if(row.Length>columns.Count)throw new InvalidOperationException("结果列数与表头不一致，无法导出 Excel。");
                await WriteRowAsync(writer,(int)count+1,row,false,ct);
            }
            await writer.WriteEndElementAsync();
            await writer.WriteEndElementAsync();
            await writer.WriteEndDocumentAsync();
            await writer.FlushAsync();
        }
        return count;
    }

    private static async Task WriteRowAsync(XmlWriter writer,int rowNumber,object?[] values,bool header,CancellationToken ct)
    {
        await writer.WriteStartElementAsync(null,"row",MainNs);
        await writer.WriteAttributeStringAsync(null,"r",null,rowNumber.ToString(CultureInfo.InvariantCulture));
        for(int index=0;index<values.Length;index++){
            ct.ThrowIfCancellationRequested();
            var value=values[index];
            if(value is null or DBNull)continue;
            var reference=ColumnName(index)+rowNumber.ToString(CultureInfo.InvariantCulture);
            if(!header && value is DateTime date){
                await WriteNumberAsync(writer,reference,date.ToOADate().ToString("R",CultureInfo.InvariantCulture),"2");
                continue;
            }
            if(!header && value is DateOnly dateOnly){
                await WriteNumberAsync(writer,reference,dateOnly.ToDateTime(TimeOnly.MinValue).ToOADate().ToString("R",CultureInfo.InvariantCulture),"2");
                continue;
            }
            if(!header && IsNumeric(value,out var number)){
                await WriteNumberAsync(writer,reference,number,header?"1":null);
                continue;
            }
            if(!header && value is bool flag){
                await writer.WriteStartElementAsync(null,"c",MainNs);
                await writer.WriteAttributeStringAsync(null,"r",null,reference);
                await writer.WriteAttributeStringAsync(null,"t",null,"b");
                await writer.WriteElementStringAsync(null,"v",MainNs,flag?"1":"0");
                await writer.WriteEndElementAsync();
                continue;
            }
            var text=value switch{
                byte[] bytes=>Convert.ToBase64String(bytes),
                DateTimeOffset offset=>offset.ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture),
                TimeOnly time=>time.ToString("HH:mm:ss.fff",CultureInfo.InvariantCulture),
                TimeSpan span=>span.ToString("c",CultureInfo.InvariantCulture),
                _=>SqlTools.Display(value)
            };
            if(text.Length>MaxCellText)throw new InvalidOperationException($"单元格内容超过 Excel 上限（{MaxCellText} 字符），请改用 CSV 或 JSON 导出。");
            await writer.WriteStartElementAsync(null,"c",MainNs);
            await writer.WriteAttributeStringAsync(null,"r",null,reference);
            await writer.WriteAttributeStringAsync(null,"t",null,"inlineStr");
            if(header)await writer.WriteAttributeStringAsync(null,"s",null,"1");
            await writer.WriteStartElementAsync(null,"is",MainNs);
            await writer.WriteStartElementAsync(null,"t",MainNs);
            if(text.Length>0&&(char.IsWhiteSpace(text[0])||char.IsWhiteSpace(text[^1])))await writer.WriteAttributeStringAsync("xml","space","http://www.w3.org/XML/1998/namespace","preserve");
            await writer.WriteStringAsync(text);
            await writer.WriteEndElementAsync();
            await writer.WriteEndElementAsync();
            await writer.WriteEndElementAsync();
        }
        await writer.WriteEndElementAsync();
    }

    private static async Task WriteNumberAsync(XmlWriter writer,string reference,string value,string? style)
    {
        await writer.WriteStartElementAsync(null,"c",MainNs);
        await writer.WriteAttributeStringAsync(null,"r",null,reference);
        if(style is not null)await writer.WriteAttributeStringAsync(null,"s",null,style);
        await writer.WriteElementStringAsync(null,"v",MainNs,value);
        await writer.WriteEndElementAsync();
    }

    private static bool IsNumeric(object value,out string text)
    {
        switch(value){
            case sbyte or byte or short or ushort or int or uint or long or ulong or decimal:
                text=Convert.ToString(value,CultureInfo.InvariantCulture)!;return true;
            case float single when float.IsFinite(single):
                text=single.ToString("R",CultureInfo.InvariantCulture);return true;
            case double number when double.IsFinite(number):
                text=number.ToString("R",CultureInfo.InvariantCulture);return true;
            default:
                text="";return false;
        }
    }

    public static async IAsyncEnumerable<string[]> ReadAsync(Stream input,[EnumeratorCancellation] CancellationToken ct)
    {
        using var archive=new ZipArchive(input,ZipArchiveMode.Read,leaveOpen:true);
        var shared=await ReadSharedStringsAsync(archive,ct);
        var dateStyles=ReadDateStyles(archive);
        var sheet=FindSheet(archive)??throw new InvalidDataException("Excel 文件缺少工作表。");
        await using var stream=sheet.Open();
        using var reader=XmlReader.Create(stream,new XmlReaderSettings{Async=true,IgnoreWhitespace=true,DtdProcessing=DtdProcessing.Prohibit});
        while(await reader.ReadAsync()){
            ct.ThrowIfCancellationRequested();
            if(reader.NodeType!=XmlNodeType.Element||reader.LocalName!="row")continue;
            int rowDepth=reader.Depth;
            var cells=new SortedDictionary<int,string>();
            int nextColumn=0;
            if(reader.IsEmptyElement){yield return [];continue;}
            while(await reader.ReadAsync()){
                if(reader.NodeType==XmlNodeType.EndElement&&reader.Depth==rowDepth&&reader.LocalName=="row")break;
                if(reader.NodeType!=XmlNodeType.Element||reader.LocalName!="c"||reader.Depth!=rowDepth+1)continue;
                int column=ColumnIndex(reader.GetAttribute("r"))??nextColumn;nextColumn=column+1;
                var type=reader.GetAttribute("t")??"";
                int style=int.TryParse(reader.GetAttribute("s"),CultureInfo.InvariantCulture,out var parsed)?parsed:0;
                string raw="",inline="";
                if(!reader.IsEmptyElement){
                    int cellDepth=reader.Depth;
                    while(await reader.ReadAsync()){
                        if(reader.NodeType==XmlNodeType.EndElement&&reader.Depth==cellDepth&&reader.LocalName=="c")break;
                        if(reader.NodeType!=XmlNodeType.Element)continue;
                        var tag=reader.LocalName;
                        if(tag is not ("v" or "t"))continue;
                        if(reader.IsEmptyElement)continue;
                        int valueDepth=reader.Depth;
                        var content=new StringBuilder();
                        while(await reader.ReadAsync()){
                            if(reader.NodeType==XmlNodeType.EndElement&&reader.Depth==valueDepth&&reader.LocalName==tag)break;
                            if(reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)content.Append(reader.Value);
                        }
                        if(tag=="v")raw=content.ToString();
                        else inline+=content.ToString();
                    }
                }
                var text=type switch{
                    "inlineStr"=>inline,
                    "s"=>int.TryParse(raw,CultureInfo.InvariantCulture,out var sharedIndex)&&sharedIndex>=0&&sharedIndex<shared.Count?shared[sharedIndex]:"",
                    "b"=>raw=="1"?"TRUE":"FALSE",
                    "d"=>raw,
                    _=>dateStyles.Contains(style)&&double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out var serial)?FromSerial(serial):raw
                };
                cells[column]=text;
            }
            if(cells.Count==0){yield return [];continue;}
            var row=new string[cells.Keys.Max()+1];Array.Fill(row,"");
            foreach(var pair in cells)row[pair.Key]=pair.Value;
            yield return row;
        }
    }

    private static async Task<List<string>> ReadSharedStringsAsync(ZipArchive archive,CancellationToken ct)
    {
        var entry=archive.GetEntry("xl/sharedStrings.xml");if(entry is null)return [];
        await using var stream=entry.Open();
        using var reader=XmlReader.Create(stream,new XmlReaderSettings{Async=true,IgnoreWhitespace=true,DtdProcessing=DtdProcessing.Prohibit});
        var result=new List<string>();
        while(await reader.ReadAsync()){
            ct.ThrowIfCancellationRequested();
            if(reader.NodeType!=XmlNodeType.Element||reader.LocalName!="si")continue;
            int depth=reader.Depth;var text=new StringBuilder();
            if(reader.IsEmptyElement){result.Add("");continue;}
            while(await reader.ReadAsync()){
                if(reader.NodeType==XmlNodeType.EndElement&&reader.Depth==depth&&reader.LocalName=="si")break;
                if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="t")text.Append(await reader.ReadElementContentAsStringAsync());
            }
            result.Add(text.ToString());
        }
        return result;
    }

    private static HashSet<int> ReadDateStyles(ZipArchive archive)
    {
        var result=new HashSet<int>();var entry=archive.GetEntry("xl/styles.xml");if(entry is null)return result;
        try{
            using var stream=entry.Open();var document=XDocument.Load(stream);XNamespace ns=MainNs;
            var formats=document.Root?.Element(ns+"numFmts")?.Elements(ns+"numFmt")
                .Where(e=>int.TryParse(e.Attribute("numFmtId")?.Value,out _))
                .ToDictionary(e=>int.Parse(e.Attribute("numFmtId")!.Value,CultureInfo.InvariantCulture),e=>e.Attribute("formatCode")?.Value??"")??[];
            var styles=document.Root?.Element(ns+"cellXfs")?.Elements(ns+"xf").ToArray()??[];
            for(int i=0;i<styles.Length;i++){
                var id=int.TryParse(styles[i].Attribute("numFmtId")?.Value,CultureInfo.InvariantCulture,out var parsed)?parsed:0;
                if(IsDateStyle(id,formats))result.Add(i);
            }
        }catch(XmlException){}
        return result;
    }

    private static bool IsDateStyle(int id,IReadOnlyDictionary<int,string> formats)
    {
        if(id is >= 14 and <= 22 or >= 45 and <= 47)return true;
        if(!formats.TryGetValue(id,out var code))return false;
        bool quoted=false;
        foreach(var character in code){
            if(character=='"'){quoted=!quoted;continue;}
            if(quoted)continue;
            if("ymdhs".Contains(char.ToLowerInvariant(character)))return true;
        }
        return false;
    }

    private static ZipArchiveEntry? FindSheet(ZipArchive archive)
    {
        try{
            var workbook=archive.GetEntry("xl/workbook.xml");
            var relationships=archive.GetEntry("xl/_rels/workbook.xml.rels");
            if(workbook is not null&&relationships is not null){
                XNamespace ns=MainNs;XNamespace rel=OfficeRelNs;XNamespace package=PackageRelNs;
                XDocument book;using(var stream=workbook.Open())book=XDocument.Load(stream);
                var id=book.Root?.Element(ns+"sheets")?.Elements(ns+"sheet").FirstOrDefault()?.Attribute(rel+"id")?.Value;
                if(id is not null){
                    XDocument links;using(var stream=relationships.Open())links=XDocument.Load(stream);
                    var target=links.Root?.Elements(package+"Relationship").FirstOrDefault(e=>e.Attribute("Id")?.Value==id)?.Attribute("Target")?.Value;
                    if(target is not null){var path=ResolvePath("xl",target);var entry=archive.GetEntry(path);if(entry is not null)return entry;}
                }
            }
        }catch(XmlException){}
        return archive.Entries.FirstOrDefault(e=>e.FullName.StartsWith("xl/worksheets/",StringComparison.OrdinalIgnoreCase)&&e.FullName.EndsWith(".xml",StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolvePath(string basePath,string target)
    {
        if(target.StartsWith('/'))return target.TrimStart('/');
        var parts=new List<string>(basePath.Split('/',StringSplitOptions.RemoveEmptyEntries));
        foreach(var part in target.Replace('\\','/').Split('/',StringSplitOptions.RemoveEmptyEntries)){
            if(part==".")continue;
            if(part==".."){if(parts.Count>0)parts.RemoveAt(parts.Count-1);continue;}
            parts.Add(part);
        }
        return string.Join("/",parts);
    }

    private static int? ColumnIndex(string? reference)
    {
        if(string.IsNullOrEmpty(reference))return null;
        int value=0;bool found=false;
        foreach(var character in reference){
            if(character is >= 'A' and <= 'Z'){value=value*26+(character-'A'+1);found=true;}
            else if(character is >= 'a' and <= 'z'){value=value*26+(character-'a'+1);found=true;}
            else break;
        }
        return found?value-1:null;
    }

    private static string ColumnName(int index)
    {
        var text="";
        for(int value=index+1;value>0;value=(value-1)/26)text=(char)('A'+(value-1)%26)+text;
        return text;
    }

    private static string FromSerial(double serial)
    {
        try{
            var value=DateTime.FromOADate(serial);
            return value.TimeOfDay==TimeSpan.Zero?value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture):value.ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture);
        }catch(ArgumentException){return serial.ToString("R",CultureInfo.InvariantCulture);}
    }

    private static string SheetName(string? name)
    {
        var value=new string((name??"").Where(character=>"[]:*?/\\".IndexOf(character)<0).ToArray()).Trim();
        if(value.Length==0)value="Sheet1";
        return value.Length>31?value[..31]:value;
    }

    private static async Task AddTextAsync(ZipArchive archive,string path,string text,CancellationToken ct)
    {
        var entry=archive.CreateEntry(path,CompressionLevel.Fastest);
        await using var stream=entry.Open();
        await using var writer=new StreamWriter(stream,new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(),ct);
        await writer.FlushAsync(ct);
    }

    private static string Xml(string value)=>System.Security.SecurityElement.Escape(value)??"";

    private const string ContentTypes="""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""";
    private const string PackageRelationships="""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""";
    private const string Styles="""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm:ss"/></numFmts><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""";
}
