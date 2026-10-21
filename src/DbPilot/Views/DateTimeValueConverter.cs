using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DbPilot.Views;

internal sealed class DateTimeValueConverter : IValueConverter
{
    public const string Format="yyyy-MM-dd HH:mm:ss";
    public const string DateFormat="yyyy-MM-dd";
    public static readonly DateTimeValueConverter Instance=new();

    public object? Convert(object? value,Type targetType,object? parameter,CultureInfo culture)=>value switch
    {
        DateTime date=>date.ToString(Format,CultureInfo.InvariantCulture),
        DateTimeOffset date=>date.ToString(Format+"zzz",CultureInfo.InvariantCulture),
        DateOnly date=>date.ToString(DateFormat,CultureInfo.InvariantCulture),
        _=>value
    };

    public object? ConvertBack(object? value,Type targetType,object? parameter,CultureInfo culture)
    {
        if(value is not string text)return value??BindingOperations.DoNothing;
        if(parameter is Type type){
            if(type==typeof(DateTimeOffset))return DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var offset)?offset:BindingOperations.DoNothing;
            if(type==typeof(DateOnly))return DateOnly.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)?day:BindingOperations.DoNothing;
        }
        if(DateTime.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)||DateTime.TryParse(text,culture,DateTimeStyles.None,out date))return date;
        return BindingOperations.DoNothing;
    }
}
