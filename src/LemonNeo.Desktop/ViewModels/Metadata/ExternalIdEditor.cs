using CommunityToolkit.Mvvm.ComponentModel;

namespace LemonNeo.Desktop.ViewModels.Metadata;

public sealed partial class ExternalIdEditor : ObservableObject
{
    [ObservableProperty] private string _namespace = "";
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _value = "";
}
