using Avalonia.Controls;
using LemonNeo.Desktop.Controls.Shared;

namespace LemonNeo.Desktop.Views.Search;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        CollapsibleListHeader.Attach(SearchHeader, CompactTitle, SearchTools, SearchEmptyState, TrackList, CatalogList);
        TrackListInteraction.Attach(TrackList);
    }
}
