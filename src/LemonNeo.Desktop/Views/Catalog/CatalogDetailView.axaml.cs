using Avalonia.Controls;
using LemonNeo.Desktop.Controls.Shared;

namespace LemonNeo.Desktop.Views.Catalog;

public sealed partial class CatalogDetailView : UserControl
{
    public CatalogDetailView()
    {
        InitializeComponent();
        CollapsibleListHeader.Attach(CatalogHeader, CompactTitle, CatalogTools, null, TrackList);
        TrackListInteraction.Attach(TrackList);
    }
}
