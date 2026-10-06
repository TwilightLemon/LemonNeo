using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Components;

public sealed class ComponentsViewModel : PageViewModel
{
    public ComponentsViewModel(DesktopContext context) : base(context, DesktopRoutes.Components)
    { SampleCommand = new(() => SetStatus("SampleActionDone")); }
    public RelayCommand SampleCommand { get; }
}
