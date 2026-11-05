using System.IO;

namespace CrystalStudio;

public partial class MainWindow
{
    private ObjectExtractionWindow? extractionWindow;
    private void ExtractObjectsClick(object sender,RoutedEventArgs e)
    {
        if(extractionWindow is {IsVisible:true}){extractionWindow.Activate();return;}
        string? index=StampCategories.FindIndex();
        if(index==null){MessageBox.Show(this,"The stamp category catalogue was not found.");return;}
        extractionWindow=new ObjectExtractionWindow(
            ()=>new ExtractionInput(mapPath??("Untitled-"+Viewport.Document.GetHashCode()),Viewport.Document,Viewport.Selection),
            settings.LibraryFolder,index,()=>
            {
                categoryIndex=StampCategories.Read(index);selectedStampPath=null;selectedStampPreview=null;
                _=DiscoverStampsAsync();if(CategoryPalette)BuildPage();UpdateLabels();UpdateMapTabLabels();
            },settings.AssetFormat,()=>settingsStore.Load().Ai??new()){Owner=this};
        extractionWindow.Closed+=(_,_)=>extractionWindow=null;extractionWindow.Show();
    }
}
