using Microsoft.UI.Xaml.Data;

namespace MCHarbor.Windows.Controls;

public sealed class FavoriteStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? "★" : "";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
