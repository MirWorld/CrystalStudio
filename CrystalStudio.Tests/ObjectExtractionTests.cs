using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

// These tests share Avalonia's application, popup focus and startup language with
// MigrationTests. Parallel classes can close each other's open tooltip windows.
[assembly:CollectionBehavior(DisableTestParallelization=true)]

namespace CrystalStudio.Tests;

public sealed class ObjectExtractionTests
{
    private sealed class Sandbox:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"CrystalExtraction-"+Guid.NewGuid().ToString("N"));
        public string Index=>Path.Combine(Root,"catalogue","palette.json");
        public string Objects=>Path.Combine(Root,"Objects");
        public Sandbox()
        {
            Directory.CreateDirectory(Objects);Directory.CreateDirectory(Path.GetDirectoryName(Index)!);
            File.WriteAllText(Index,"""{"schemaVersion":1,"objectRoot":"Objects","previewRoot":"catalogue","custom":{"keep":42},"objects":[]}""");
        }
        public void Template(string file,string type,params StampCell[] cells)
        {
            CellCodec.SaveStamp(Path.Combine(Objects,file),cells);
            var root=JsonNode.Parse(File.ReadAllText(Index))!;var entries=root["objects"]!.AsArray();
            entries.Add(JsonSerializer.SerializeToNode(new CategorizedStamp(file,file,file,type,null,[],[],[],[],null,"unknown",null,false,"unreviewed"),
                new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));File.WriteAllText(Index,root.ToJsonString());
        }
        public ObjectExtractionService Service(Func<int,int,SpriteHeader?>? headers=null)=>new(Index,headers??((_,_)=>new SpriteHeader(48,96,0,0)));
        public Task<ExtractionSession> Scan(params ExtractionInput[] input)=>Service().ScanAsync(input,default);
        public void Dispose(){Directory.Delete(Root,true);}
    }
    private static StampCell FrontCell(int x,int y,int image,int flags=0,byte frames=0,byte tick=0,short library=2)
        =>new(x,y,new(){Foreground=new(library,(short)image,frames,tick),BackImage=flags});
    private static MapDocument Map(params StampCell[] cells)
    {var map=new MapDocument(128,128);foreach(var cell in cells)map.Set(cell.X,cell.Y,cell.Cell.Copy());return map;}
    private static ExtractionCandidate Candidate(List<StampCell> cells,string source="a.map",int x=0,int y=0)=>
        new(Guid.NewGuid(),"Object","Tree","test",ExtractionKind.Discovered,cells,[],[new(source,x,y,false)],[]);

    [Fact]
    public async Task MultipartTreesDeduplicateWithoutMergingRepeatedImages()
    {
        using var box=new Sandbox();var map=Map(FrontCell(4,4,1),FrontCell(5,4,2),FrontCell(6,4,1),FrontCell(7,4,2));
        var result=await box.Scan(new ExtractionInput("forest.map",map));var tree=Assert.Single(result.Candidates);
        Assert.Equal(2,tree.Cells.Count);Assert.Equal(2,tree.Occurrences.Count);Assert.False(tree.Reviewed);
        Assert.All(tree.Cells,c=>Assert.Equal(2,c.Cell.Foreground!.Library));
    }
    [Fact]
    public async Task DiscoveryRequiresSameLibrarySequenceAndVisibleContact()
    {
        using var box=new Sandbox();var map=Map(FrontCell(3,3,1),FrontCell(4,3,50),FrontCell(5,3,51,library:3),FrontCell(9,3,2));
        var result=await box.Scan(new ExtractionInput("separate.map",map));Assert.Equal(4,result.Candidates.Count);
        Assert.All(result.Candidates,c=>Assert.Single(c.Cells));
    }
    [Fact]
    public async Task ForegroundExtractionIgnoresLargeNumbersOfGroundOnlyTemplateMatches()
    {
        using var box=new Sandbox();box.Template("ground.X","Terrain",new StampCell(0,0,new(){BackImage=1}));
        var map=new MapDocument(320,320);for(int y=0;y<320;y++)for(int x=0;x<320;x++)map.Set(x,y,new(){BackImage=1});
        map.Set(5,5,FrontCell(5,5,2).Cell);
        var result=await box.Scan(new ExtractionInput("ground.map",map));Assert.Single(result.Candidates);Assert.Single(result.Candidates[0].Cells);
    }
    [Fact]
    public async Task LargestTemplateWinsAndCapturesActualAnimationAndOnlyConfirmedCollision()
    {
        using var box=new Sandbox();box.Template("small.X","Decoration",FrontCell(0,0,1));
        box.Template("large.X","Tree",FrontCell(0,0,1,0x20000000),FrontCell(1,0,2));
        var map=Map(FrontCell(10,10,1,0x60000000,5,7),FrontCell(11,10,2));map.Set(12,10,new(){BackImage=0x40000000});
        var result=await box.Scan(new ExtractionInput("map",map));var candidate=Assert.Single(result.Candidates);
        Assert.Equal(ExtractionKind.Template,candidate.Kind);Assert.Equal("Tree",candidate.Category);
        Assert.Equal((byte)5,candidate.Cells.Single(c=>c.Cell.Foreground?.Image==1).Cell.Foreground!.Frames);
        Assert.Equal((byte)7,candidate.Cells.Single(c=>c.Cell.Foreground?.Image==1).Cell.Foreground!.Tick);
        Assert.Equal(0x20000000,candidate.Cells.Single(c=>c.Cell.Foreground?.Image==1).Cell.BackImage);
        Assert.Contains(candidate.SuggestedCollisions,c=>c.Cell.BackImage==0x40000000);
    }
    [Fact]
    public async Task ValidMetadataHasPriorityAndStaleMetadataDoesNotReserveImages()
    {
        using var box=new Sandbox();box.Template("template.X","Tree",FrontCell(0,0,1));
        var map=new MapDocument(128,128);var history=new EditHistory();
        map.PlaceStamp([FrontCell(0,0,1)],10,10,"metadata",history,(_,_)=>{});history.Commit();history.Saved();
        map.PlaceStamp([FrontCell(0,0,1)],20,20,"stale",history,(_,_)=>{});history.Commit();map.Set(20,20,FrontCell(0,0,2).Cell);
        long revision=map.PickupRevision;bool dirty=history.Dirty;var original=map.Cells.ToDictionary(p=>p.Key,p=>p.Value.Copy());
        var result=await box.Scan(new ExtractionInput("map",map));Assert.Equal(2,result.Candidates.Count);
        Assert.Contains(result.Candidates,c=>c.Kind==ExtractionKind.Metadata&&c.Name=="metadata");
        Assert.Contains(result.Candidates,c=>c.Kind==ExtractionKind.Discovered&&c.Cells.Any(s=>s.Cell.Foreground?.Image==2));
        Assert.Equal(revision,map.PickupRevision);Assert.Equal(dirty,history.Dirty);
        Assert.All(map.Cells,p=>Assert.True(p.Value.SameContents(original[p.Key])));
    }
    [Fact]
    public async Task SelectionRetainsWholeObjectAndRequiresBoundaryOverride()
    {
        using var box=new Sandbox();box.Template("tree.X","Tree",FrontCell(0,0,1),FrontCell(1,0,2));
        var result=await box.Scan(new ExtractionInput("map",Map(FrontCell(5,5,1),FrontCell(6,5,2)),new Rect(5,5,1,1)));
        var c=Assert.Single(result.Candidates);Assert.Equal(2,c.Cells.Count);Assert.True(c.Occurrences[0].CrossesSelection);
        c=c with{Reviewed=true};Assert.False(c.CanExport);Assert.True((c with{IncludeBoundary=true}).CanExport);
    }
    [Fact]
    public async Task DedupPreservesAllMapSourcesAndAnimationCollisionVariants()
    {
        using var box=new Sandbox();
        var result=await box.Scan(new ExtractionInput("a.map",Map(FrontCell(2,3,1))),new("b.map",Map(FrontCell(20,30,1))),
            new("animation.map",Map(FrontCell(2,3,1,frames:2,tick:4))),new("collision.map",Map(FrontCell(2,3,1,0x40000000))));
        Assert.Equal(3,result.Candidates.Count);Assert.Equal(4,result.Candidates.Sum(c=>c.Occurrences.Count));
        Assert.Equal(2,result.Candidates.Max(c=>c.Occurrences.Count));
        Assert.Equal(3,result.Candidates.Select(c=>c.Fingerprint).Distinct().Count());
    }
    [Fact]
    public async Task NearbyCollisionStartsUnselectedAndIsConservativeAcrossDuplicateSources()
    {
        using var box=new Sandbox();var a=Map(FrontCell(5,5,1,0x20000000));a.Set(6,5,new(){BackImage=0x40000000});
        var b=Map(FrontCell(15,15,1,0x20000000));
        var result=await box.Scan(new ExtractionInput("a",a),new("b",b));var c=Assert.Single(result.Candidates);
        Assert.Single(c.Cells);Assert.Single(c.SuggestedCollisions);Assert.Equal(0x20000000,c.SuggestedCollisions[0].Cell.BackImage);
        var review=new ExtractionReview([c]);review.Collision(c.Id,c.Cells[0].X,c.Cells[0].Y,0);Assert.Equal(0,review.Candidates[0].Cells[0].Cell.BackImage);
        review.Collision(c.Id,c.Cells[0].X,c.Cells[0].Y,0x20000000);Assert.Equal(0x20000000,review.Candidates[0].Cells[0].Cell.BackImage);
        Assert.Throws<InvalidDataException>(()=>review.Collision(c.Id,1,0,0x40000000));
    }
    [Fact]
    public void ReviewSplitMergeExcludeCollisionAndUndoAreIndependentValueChanges()
    {
        var c=Candidate([FrontCell(0,0,1,0x20000000),FrontCell(1,0,2)]);var review=new ExtractionReview([c]);
        review.Update([c.Id],name:"Tree",category:"Tree",reviewed:true);Assert.True(review.Candidates[0].Reviewed);
        review.RemoveFragments(c.Id,[(1,0)],true);Assert.Equal(2,review.Candidates.Count);Assert.All(review.Candidates,v=>Assert.False(v.Reviewed));
        review.Merge(review.Candidates.Select(v=>v.Id));Assert.Equal(2,Assert.Single(review.Candidates).Cells.Count);
        review.Undo();Assert.Equal(2,review.Candidates.Count);review.Redo();Assert.Single(review.Candidates);
        review.Exclude(review.Candidates.Select(v=>v.Id));Assert.Empty(review.Candidates);review.Undo();Assert.Single(review.Candidates);
        Assert.Equal(2,c.Cells.Count);Assert.Equal(0x20000000,c.Cells[0].Cell.BackImage);
    }
    [Fact]
    public void MergeRepeatingComponentsPreservesUnmatchedSourceOccurrences()
    {
        var a=Candidate([FrontCell(0,0,1)],x:5,y:5) with{Occurrences=[new("map",5,5,false),new("map",15,5,false),new("other",8,8,false)]};
        var b=Candidate([FrontCell(0,0,2)],x:6,y:5) with{Occurrences=[new("map",6,5,false),new("map",16,5,false),new("map",26,5,false)]};
        var review=new ExtractionReview([a,b]);review.Merge([a.Id,b.Id]);
        var merged=Assert.Single(review.Candidates,c=>c.Cells.Count==2);Assert.Equal(2,merged.Occurrences.Count);
        Assert.Equal(2,review.Candidates.Where(c=>c.Cells.Count==1).Sum(c=>c.Occurrences.Count));
        Assert.Equal(6,merged.Occurrences.Count*2+review.Candidates.Where(c=>c.Cells.Count==1).Sum(c=>c.Occurrences.Count));
        review.Undo();Assert.Equal(2,review.Candidates.Count);Assert.Equal(6,review.Candidates.Sum(c=>c.Occurrences.Count));
    }
    [Fact]
    public void SplittingAllFragmentsRetainsOffAnchorCollisionAndMergeRetainsSourceGround()
    {
        var c=Candidate([FrontCell(0,0,1),new(1,0,new(){BackImage=0x40000000})]);var review=new ExtractionReview([c]);
        review.RemoveFragments(c.Id,[(0,0)],true);Assert.Equal(c.Fingerprint,Assert.Single(review.Candidates).Fingerprint);
        var a=Candidate([FrontCell(0,0,1)],x:5,y:5) with{Context=[new(0,0,new(){BackImage=15,Foreground=new(2,1)})]};
        var b=Candidate([FrontCell(0,0,2)],x:6,y:5) with{Context=[new(0,0,new(){MiddleLibrary=1,MiddleImage=12,Foreground=new(2,2)})]};
        review=new([a,b]);review.Merge([a.Id,b.Id]);var merged=Assert.Single(review.Candidates);
        Assert.Equal(15,merged.Context.Single(s=>s.X==0&&s.Y==0).Cell.BackImage);
        Assert.Equal(12,merged.Context.Single(s=>s.X==1&&s.Y==0).Cell.MiddleImage);
    }
    [Fact]
    public async Task MissingArtworkIsReportedAndNeverUsedToMergeFragments()
    {
        using var box=new Sandbox();var result=await box.Service((_,_)=>null).ScanAsync([new("map",Map(FrontCell(3,3,1),FrontCell(4,3,2)))],default);
        Assert.Equal(2,result.Candidates.Count);Assert.Equal(2,result.Warnings.Count);Assert.All(result.Candidates,c=>Assert.False(c.Reviewed));
    }
    [Fact]
    public async Task ClassificationRequiresEightyPercentAndOversizedGroupsRemainIndividual()
    {
        using var box=new Sandbox();
        for(int i=1;i<=4;i++)box.Template($"tree{i}.X","Tree",FrontCell(0,0,i),FrontCell(1,0,100+i));
        var map=Map(Enumerable.Range(1,5).Select(i=>FrontCell(10+i,10,i)).ToArray());
        var result=await box.Scan(new ExtractionInput("map",map));Assert.Equal("Tree",Assert.Single(result.Candidates).Category);
        Assert.Contains("80%",result.Candidates[0].Reason);
        var wide=await box.Service((_,_)=>new SpriteHeader(8192,96,0,0)).ScanAsync([new("wide",Map(FrontCell(1,5,200),FrontCell(66,5,201)))],default);
        Assert.Equal(2,wide.Candidates.Count);Assert.All(wide.Candidates,c=>Assert.Contains("limit",c.Reason));
    }
    [Fact]
    public void HeaderReadValidatesDimensionsWithoutDecodingPixelData()
    {
        using var box=new Sandbox();string file=Path.Combine(box.Root,"sprite.lib");Fixture.WriteLibrary(file,48,96,1,2,3);
        var library=new CrystalLibrary(file);Assert.Equal(new SpriteHeader(48,96,0,0),library.ReadHeader(0));Assert.Null(library.ReadHeader(1));
        // Compressed pixels may be invalid while metadata remains readable.
        using(var stream=new FileStream(file,FileMode.Open,FileAccess.Write,FileShare.None)){stream.Position=33;stream.WriteByte(0);}
        Assert.NotNull(library.ReadHeader(0));Assert.Throws<InvalidDataException>(()=>library.Decode(0));
    }
    [Fact]
    public async Task RealResourcesBatchRoundtripAndCancellationWhenRequested()
    {
        string? resources=Environment.GetEnvironmentVariable("CRYSTAL_EXTRACTION_REAL_RESOURCES");
        if(resources==null)return; // Explicit opt-in: this test never modifies installed game resources.
        string mapRoot=Path.Combine(Path.GetDirectoryName(resources)!,"Map");
        string project=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        // Use the repository catalogue for read-only matching and an empty isolated
        // catalogue for export, so no production object or index is ever written.
        using var box=new Sandbox();using var assets=AssetStore.Load(resources);
        string originalIndex=Path.Combine(project,"catalogue","palette.json");
        if(!File.Exists(originalIndex))originalIndex=StampCategories.FindIndex()!;
        var files=new[]{"0.map","0128.map","0129.map"}.Select(n=>Path.Combine(mapRoot,n)).ToArray();
        var hashes=files.ToDictionary(f=>f,f=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
        var timer=Stopwatch.StartNew();var service=new ObjectExtractionService(originalIndex,assets);
        var result=await service.ScanAsync(files.Select(f=>new ExtractionInput(f)),default);
        double batchSeconds=timer.Elapsed.TotalSeconds;
        Assert.Equal(3,result.Sources.Count);Assert.NotEmpty(result.Candidates);Assert.Contains(result.Candidates,c=>c.Kind==ExtractionKind.Template);
        var selected=result.Candidates.Take(20).Select(c=>c with{Reviewed=true}).ToList();
        var exported=await ObjectExtractionExport.SaveAsync(box.Index,result,selected,default);
        foreach(var output in exported.Objects)Assert.Equal(output.Fingerprint,ExtractionContent.Fingerprint(CellCodec.LoadStamp(Path.Combine(box.Objects,output.File))));
        Assert.All(files,f=>Assert.Equal(hashes[f],Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
        using var stop=new CancellationTokenSource();var cancelTimer=Stopwatch.StartNew();double? cancellationRequestedMs=null;
        var updates=new InlineProgress<ExtractionProgress>(p=>{if(p.Phase=="Matching stamps"){cancellationRequestedMs=cancelTimer.Elapsed.TotalMilliseconds;stop.Cancel();}});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ObjectExtractionService(originalIndex,assets).ScanAsync(files.Select(f=>new ExtractionInput(f)),stop.Token,updates));
        Assert.NotNull(cancellationRequestedMs);double cancellationResponseMs=cancelTimer.Elapsed.TotalMilliseconds-cancellationRequestedMs.Value;
        string evidence=Environment.GetEnvironmentVariable("CRYSTAL_EXTRACTION_EVIDENCE")??box.Root;Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence,"real-resources.json"),JsonSerializer.Serialize(new
        {elapsedSeconds=timer.Elapsed.TotalSeconds,batchSeconds,cancellationSeconds=cancelTimer.Elapsed.TotalSeconds,cancellationResponseMs,maps=files,candidates=result.Candidates.Count,
            existing=result.Candidates.Count(c=>c.Kind!=ExtractionKind.Discovered),discovered=result.Candidates.Count(c=>c.Kind==ExtractionKind.Discovered),
            occurrences=result.Candidates.Sum(c=>c.Occurrences.Count),warnings=result.Warnings.Count,exported=exported.Objects.Count,sourceHashes=hashes},new JsonSerializerOptions{WriteIndented=true}));
    }
    private sealed class InlineProgress<T>(Action<T> action):IProgress<T>{public void Report(T value)=>action(value);}
    [Fact]
    public async Task ExportReloadMatchesEveryFieldAndPlacementReusesNativeObjectContract()
    {
        using var box=new Sandbox();var cell=FrontCell(15,25,1,0x60000000,0x85,7).Cell;cell.Door=3;cell.DoorOffset=4;cell.Light=9;
        var session=await box.Scan(new ExtractionInput("source.map",Map(new StampCell(15,25,cell))));var c=Assert.Single(session.Candidates) with{Reviewed=true,Category="Tree",Name="Test tree"};
        var result=await ObjectExtractionExport.SaveAsync(box.Index,session,[c],default);
        var output=Assert.Single(result.Objects);var restored=CellCodec.LoadStamp(Path.Combine(box.Objects,output.File));
        var expected=ExtractionContent.Normalize(c.Cells);Assert.Equal(expected.Count,restored.Count);
        for(int i=0;i<expected.Count;i++){Assert.Equal((expected[i].X,expected[i].Y),(restored[i].X,restored[i].Y));Assert.True(expected[i].Cell.SameContents(restored[i].Cell));}
        Assert.Equal(c.Fingerprint,ExtractionContent.Fingerprint(restored));Assert.True(File.Exists(result.ReportPath));
        var index=StampCategories.Read(box.Index);Assert.Equal("Tree",Assert.Single(index.Objects).Type);
        Assert.Equal(42,JsonNode.Parse(File.ReadAllText(box.Index))!["custom"]!["keep"]!.GetValue<int>());
        var target=new MapDocument(32,32);var history=new EditHistory();Assert.NotNull(target.PlaceStamp(restored,10,10,"test",history,(_,_)=>{}));
        Assert.Equal(cell.Foreground,target.Get(10,10).Foreground);history.Commit();history.Undo(target,(_,_)=>{});Assert.Null(target.Get(10,10).Foreground);
    }
    [Fact]
    public async Task ExactTemplateIsReusedAndRepeatedExportDoesNotDuplicateSources()
    {
        using var box=new Sandbox();box.Template("existing.X","Tree",FrontCell(0,0,1));
        var original=JsonNode.Parse(File.ReadAllText(box.Index))!;original["objects"]![0]!["customEntry"]=42;File.WriteAllText(box.Index,original.ToJsonString());
        var session=await box.Scan(new ExtractionInput("a",Map(FrontCell(2,3,1))),new("b",Map(FrontCell(5,6,1))));
        var chosen=session.Candidates.Select(c=>c with{Reviewed=true,Name="Renamed tree",Category="Plant"}).ToList();
        for(int i=0;i<2;i++)
        {
            var result=await ObjectExtractionExport.SaveAsync(box.Index,session,chosen,default);var output=Assert.Single(result.Objects);Assert.True(output.Reused);Assert.Equal("existing.X",output.File);
        }
        var root=JsonNode.Parse(File.ReadAllText(box.Index))!;var entry=Assert.Single(root["objects"]!.AsArray());
        Assert.Equal(2,entry!["extractionOccurrences"]!.AsArray().Count);Assert.Single(Directory.GetFiles(box.Objects,"*.X",SearchOption.AllDirectories));
        Assert.Equal("Renamed tree",entry["name"]!.GetValue<string>());Assert.Equal("Plant",entry["type"]!.GetValue<string>());
        Assert.Equal("reviewed",entry["reviewStatus"]!.GetValue<string>());Assert.Equal(42,entry["customEntry"]!.GetValue<int>());
    }
    [Theory]
    [InlineData("object")][InlineData("report")][InlineData("index")]
    public async Task TransactionFailureRetainsIndexAndRemovesOnlyNewOutputs(string stage)
    {
        using var box=new Sandbox();box.Template("unrelated.X","Tree",FrontCell(0,0,100));
        var original=File.ReadAllBytes(box.Index);var session=await box.Scan(new ExtractionInput("map",Map(FrontCell(3,3,1))));
        var selected=session.Candidates.Select(c=>c with{Reviewed=true}).ToList();
        await Assert.ThrowsAsync<IOException>(()=>ObjectExtractionExport.SaveAsync(box.Index,session,selected,default,
            s=>{if(s==stage)throw new IOException("Injected export failure");}));
        Assert.Equal(original,File.ReadAllBytes(box.Index));Assert.Single(Directory.GetFiles(box.Objects,"*.X",SearchOption.AllDirectories));
        Assert.Empty(Directory.Exists(Path.Combine(box.Root,"catalogue","extractions"))?Directory.GetFiles(Path.Combine(box.Root,"catalogue","extractions")):[]);
    }
    [Fact]
    public async Task ConcurrentCatalogueChangeIsRetainedAndPreventsExportCommit()
    {
        using var box=new Sandbox();var session=await box.Scan(new ExtractionInput("map",Map(FrontCell(3,3,1))));
        var selected=session.Candidates.Select(c=>c with{Reviewed=true}).ToList();
        await Assert.ThrowsAsync<IOException>(()=>ObjectExtractionExport.SaveAsync(box.Index,session,selected,default,s=>
        {if(s=="report"){var root=JsonNode.Parse(File.ReadAllText(box.Index))!;root["externalEdit"]=true;File.WriteAllText(box.Index,root.ToJsonString());}}));
        Assert.True(JsonNode.Parse(File.ReadAllText(box.Index))!["externalEdit"]!.GetValue<bool>());Assert.Empty(Directory.GetFiles(box.Objects,"*.X",SearchOption.AllDirectories));
    }
    [Fact]
    public async Task CancellationDuringScanAndExportNeverChangesSourcesOrCatalogue()
    {
        using var box=new Sandbox();using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>box.Service().ScanAsync([new("map",Map(FrontCell(3,3,1)))],stop.Token));
        var session=await box.Scan(new ExtractionInput("map",Map(FrontCell(3,3,1))));byte[] original=File.ReadAllBytes(box.Index);using var exportStop=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ObjectExtractionExport.SaveAsync(box.Index,session,session.Candidates.Select(c=>c with{Reviewed=true}).ToList(),exportStop.Token,
            s=>{if(s=="object")exportStop.Cancel();}));
        Assert.Equal(original,File.ReadAllBytes(box.Index));Assert.Empty(Directory.GetFiles(box.Objects,"*.X",SearchOption.AllDirectories));
    }
    [AvaloniaFact]
    public async Task ExportedObjectAppearsInPaletteWithPreviewAndSelectablePlacement()
    {
        using var box=new Sandbox();var session=await box.Scan(new ExtractionInput("map",Map(FrontCell(5,5,1,0x20000000,3,2))));
        var chosen=session.Candidates.Select(c=>c with{Reviewed=true,Category="Tree",Name="Exported tree"}).ToList();
        await ObjectExtractionExport.SaveAsync(box.Index,session,chosen,default);
        var window=new MainWindow(new AssetHistoryStore(Path.Combine(box.Root,"assets.json")),new RecentMapStore(Path.Combine(box.Root,"recent.json")));
        window.Show();
        async Task Until(Func<bool> ready)
        {for(int i=0;i<200&&!ready();i++){await Task.Delay(15);Dispatcher.UIThread.RunJobs();}Assert.True(ready());}
        try
        {
            var viewport=window.FindControl<MapViewport>("Viewport")!;var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            await Until(()=>viewport.Assets!=null&&!(bool)typeof(MainWindow).GetField("stampIndexLoading",flags)!.GetValue(window)!&&!(bool)typeof(MainWindow).GetField("busy",flags)!.GetValue(window)!);
            typeof(MainWindow).GetField("categoryIndex",flags)!.SetValue(window,StampCategories.Read(box.Index));
            typeof(MainWindow).GetField("categoriesAttempted",flags)!.SetValue(window,true);
            window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Items.Count==4).SelectedIndex=3;
            var tabs=window.FindControl<TabControl>("CategoryTabs")!;
            await Until(()=>tabs.GetVisualDescendants().OfType<ListBox>().Any(l=>l.Name=="CategoryItems"&&l.Items.Count==1));
            var list=tabs.GetVisualDescendants().OfType<ListBox>().Single(l=>l.Name=="CategoryItems");var node=Assert.IsType<StampCategoryNode>(list.Items[0]);
            Assert.Equal("Exported tree",node.Label);await Until(()=>node.Bitmap!=null);list.SelectedIndex=0;await Until(()=>viewport.Stamp!=null);
            Assert.Equal(chosen[0].Fingerprint,ExtractionContent.Fingerprint(viewport.Stamp!));Assert.Equal(EditorTool.Stamp,viewport.Tool);Assert.False(viewport.History.Dirty);
            Assert.NotNull(viewport.Document.PlaceStamp(viewport.Stamp!,12,13,node.Label,viewport.History,(_,_)=>{}));
            Assert.Equal(new Front(2,1,3,2),viewport.Document.Get(12,13).Foreground);viewport.History.Commit();viewport.Undo();Assert.Null(viewport.Document.Get(12,13).Foreground);
            var entry=window.FindControl<MenuItem>("ExtractObjectsButton")!;entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var tool=Assert.IsType<ObjectExtractionWindow>(typeof(MainWindow).GetField("extractionWindow",flags)!.GetValue(window));Assert.True(tool.IsVisible);
            entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Assert.Same(tool,typeof(MainWindow).GetField("extractionWindow",flags)!.GetValue(window));tool.Close();
        }
        finally{window.Close();}
    }
    [AvaloniaFact]
    public async Task CandidateFragmentListCanRecycleRowsAndRebuildDetails()
    {
        using var box=new Sandbox();
        var cells=Enumerable.Range(0,96).Select(i=>FrontCell(10+i%12,10+i/12,1)).ToArray();
        box.Template("large.X","House",cells);
        var map=Map(cells);
        var window=new ObjectExtractionWindow(()=>new("recycle.map",map),TestApplication.Root,box.Index,()=>{});
        window.Show();Dispatcher.UIThread.RunJobs();
        async Task Pump(){await Task.Delay(25);Dispatcher.UIThread.RunJobs();}
        try
        {
            await window.ScanAsync();await Pump();
            Assert.Single(window.Review!.Candidates);
            var candidates=window.GetVisualDescendants().OfType<ListBox>().Single(l=>l.Name=="ExtractionCandidates");
            for(int cycle=0;cycle<4;cycle++)
            {
                candidates.SelectedIndex=0;await Pump();
                var fragments=window.GetVisualDescendants().OfType<ListBox>().Single(l=>l!=candidates);
                Assert.Equal(96,fragments.Items.Count);
                fragments.ScrollIntoView(fragments.Items[^1]!);await Pump();
                fragments.ScrollIntoView(fragments.Items[0]!);await Pump();
                fragments.SelectedIndex=0;
                Assert.Contains(window.GetVisualDescendants().OfType<ExtractionPreview>(),preview=>preview.Selected.Count==1);
                candidates.SelectedIndex=-1;await Pump();
            }
            Assert.All(cells,cell=>Assert.Equal(cell.Cell.Foreground,map.Get(cell.X,cell.Y).Foreground));
        }
        finally{window.Close();await Pump();}
    }

    [AvaloniaFact]
    public async Task ToolWindowScansReviewsSavesAndKeepsEditorHistoryUntouched()
    {
        using var box=new Sandbox();var map=Map(FrontCell(5,5,1));var history=new EditHistory();long revision=map.PickupRevision;bool refreshed=false;
        var window=new ObjectExtractionWindow(()=>new("map",map),Path.Combine(TestApplication.Root,"Data"),box.Index,()=>refreshed=true);
        window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            await window.ScanAsync();Dispatcher.UIThread.RunJobs();var candidate=Assert.Single(window.Review!.Candidates);
            var list=window.GetVisualDescendants().OfType<ListBox>().First();list.SelectedIndex=0;Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ExtractionReviewSelected").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.Review.Candidates.Single().Reviewed);Assert.True(window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ExtractionSave").IsEnabled);
            window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ExtractionSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for(int i=0;i<200&&!refreshed;i++){await Task.Delay(10);Dispatcher.UIThread.RunJobs();}
            Assert.True(refreshed);Assert.Single(StampCategories.Read(box.Index).Objects);
            Assert.Equal(Dock.Left,window.GetVisualDescendants().OfType<TabControl>().Single().TabStripPlacement);
            Assert.False(history.Dirty);Assert.Equal(revision,map.PickupRevision);using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);
            string maps=Path.Combine(box.Root,"Maps");Directory.CreateDirectory(Path.Combine(maps,"nested"));
            map.Save(Path.Combine(maps,"first.map"));map.Save(Path.Combine(maps,"nested","second.map"));File.WriteAllText(Path.Combine(maps,"bad.map"),"invalid");
            window.GetVisualDescendants().OfType<ComboBox>().Single(v=>v.Name=="ExtractionScope").SelectedIndex=2;
            window.GetVisualDescendants().OfType<TextBox>().Single(v=>v.Name=="ExtractionDirectory").Text=maps;
            await window.ScanAsync();Assert.Single(window.Review.Candidates);Assert.Equal(2,window.Review.Candidates[0].Occurrences.Count);
            Assert.False(window.Review.Candidates[0].Reviewed);Assert.Equal(revision,map.PickupRevision);
            var pending=window.ScanAsync();window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ExtractionCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending;Assert.Null(window.Review);Assert.Equal(revision,map.PickupRevision);
        }
        finally{window.Close();await Task.Delay(30);Dispatcher.UIThread.RunJobs();}
    }
}
