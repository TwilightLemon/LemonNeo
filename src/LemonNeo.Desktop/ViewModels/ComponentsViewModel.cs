using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Infrastructure;

namespace LemonNeo.Desktop.ViewModels;

public sealed class ComponentsViewModel : PageViewModel
{
    public ComponentsViewModel(DesktopContext context) : base(context, DesktopRoutes.Components)
    { SampleCommand = new(() => SetStatus("SampleActionDone")); }
    public RelayCommand SampleCommand { get; }
}
