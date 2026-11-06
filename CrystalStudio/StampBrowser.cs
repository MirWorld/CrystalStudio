namespace CrystalStudio;

public partial class MainWindow
{
    private async Task DiscoverStampsAsync()
    {
        int version=++catalogVersion;stampIndexLoading=true;
        Viewport.Stamp=null;selectedStampPath=null;selectedStampName=null;selectedStampPreview=null;
        if(StampMode || SavedPalette || CategoryPalette)UpdateWorkspace();
        try
        {
            var result=await Task.Run(()=>{var folder=StampCatalog.FindFolder();return (Folder:folder,Entries:StampCatalog.Read(folder));});
            await Dispatcher.UIThread.InvokeAsync(()=>{if(version!=catalogVersion)return;stampFolder=result.Folder;stampEntries=result.Entries;});
        }
        catch(Exception ex){await Dispatcher.UIThread.InvokeAsync(()=>{if(version==catalogVersion){stampEntries=[];stampFolder=null;Status.Text = LocalText.Format("Unable to read Objects folder: {0}",ex.Message);}});}
        finally{await Dispatcher.UIThread.InvokeAsync(()=>{if(version==catalogVersion){stampIndexLoading=false;if(StampMode || SavedPalette || CategoryPalette){page=0;BuildPage();UpdateLabels();}}});}
    }
    private void BuildStampPage()
    {
        int version=++pageVersion;choices.Clear();
        string query=AssetSearch.Text.Trim();var filtered=stampEntries.Where(s=>query.Length==0||s.RelativePath.Contains(query,StringComparison.OrdinalIgnoreCase)).ToList();
        filteredCount=filtered.Count;page=Math.Clamp(page,0,Math.Max(0,(filtered.Count-1)/PageSize));
        foreach(var entry in filtered.Skip(page*PageSize).Take(PageSize))
        {
            var reference=AssetReference.Stamp(entry.FilePath);
            var choice=new AssetChoice { Asset=reference, IsFavourite=assetHistory.IsFavourite(reference), StampPath=entry.FilePath, Label=entry.Name, Description=entry.RelativePath };
            if(selectedStampPath==entry.FilePath){choice.StampCells=Viewport.Stamp;choice.Bitmap=selectedStampPreview;}
            choices.Add(choice);
            if(choice.StampCells==null)_=ReadStampChoiceAsync(choice,version);else if(choice.Bitmap==null&&assets!=null)choice.Bitmap=StampPreview.Create(choice.StampCells,assets);
        }
        PageLabel.Text = (stampIndexLoading ? LocalText.T("Finding Stamps\u2026") : (filtered.Count==0 ? LocalText.T("No Stamps Found") : LocalText.Format("Page {0} of {1}",page+1,Math.Max(1,(filtered.Count+PageSize-1)/PageSize))));
        UpdateWorkspace();
    }
    private async Task ReadStampChoiceAsync(AssetChoice choice,int version)
    {
        await stampReaders.WaitAsync();
        try
        {
            if(version!=pageVersion || (!StampMode && !SavedPalette && !CategoryPalette))return;
            var format=assets?.Format??AssetSourceFormat.Default;
            var cells=await Task.Run(()=>CellCodec.LoadStamp(choice.StampPath!,format));
            await Dispatcher.UIThread.InvokeAsync(()=>{
            if(version!=pageVersion || (!StampMode && !SavedPalette && !CategoryPalette))return;
            choice.StampCells=cells;
            // Report only libraries actually referenced by images, not zero-valued empty layers.
            var missing=cells.SelectMany(s=>ReferencedLibraries(s.Cell)).Distinct().Where(id=>assets?.Libraries.ContainsKey(id)!=true).ToArray();
            choice.Description=$"{choice.Description}\n{cells.Count:N0} cells{(missing.Length==0?"":$" \u00b7 Missing libraries: {string.Join(", ",missing)}")}";
            if(assets!=null && choice.Bitmap==null)choice.Bitmap=StampPreview.Create(cells,assets);
            if(IsSelectedStampChoice(choice))SelectStamp(choice);
            });
        }
        catch(Exception ex){await Dispatcher.UIThread.InvokeAsync(()=>{if(version==pageVersion){choice.LoadError=choice.Description=$"Could not load stamp: {ex.Message}";if(IsSelectedStampChoice(choice))Status.Text = (choice.Description);}});}
        finally{stampReaders.Release();}
    }
    private static IEnumerable<int> ReferencedLibraries(Cell c)
    {
        if((c.BackImage&0x1FFFFFFF)>0)yield return c.BackLibrary;
        if(c.MiddleImage>0)yield return c.MiddleLibrary;
        foreach(var f in c.ForegroundImages)if((f.Image&0x7FFF)>0)yield return f.Library;
    }
    private void SelectStamp(AssetChoice choice)
    {
        if(choice.StampCells==null)return;
        Viewport.Stamp=choice.StampCells;selectedStampPath=choice.StampPath;selectedStampName=choice.Label;Viewport.StampName=choice.Label;selectedStampPreview=choice.Bitmap;
        Viewport.Tool=EditorTool.Stamp;BrushSize.IsEnabled=true;RememberActiveAsset();
        Viewport.InvalidateVisual();UpdateLabels();Status.Text = LocalText.Format("{0} · {1:N0} cells · Click the map to place",choice.Label,choice.StampCells.Count);
    }
}

