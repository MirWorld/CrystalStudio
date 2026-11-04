using System.IO;
namespace CrystalStudio;

public sealed class StampScanWindow:EditorWindow
{
    private readonly TextBlock folderLabel=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,12)};
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,12)};
    private readonly ProgressBar progress=new(){Height=5,Minimum=0,Maximum=100};
    private readonly Button choose=new(){Content = LocalText.T("Choose Folder\u2026")};
    private readonly Button start=new(){Content = LocalText.T("Identify Objects"),IsDefault=true};
    private readonly Button cancel=new(){Content = LocalText.T("Close")};
    private readonly CancellationTokenSource cancellation=new();
    private string? folder;
    private bool running;
    public StampScanResult? Result {get;private set;}
    public StampScanWindow(MapDocument document,string? initialFolder)
    {
        this.Title = LocalText.T("Identify Objects");Width=600;Height=440;CanResize=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;UseLayoutRounding=true;
        var panel=new StackPanel{Margin=new(24),Background=Background};Content = panel;
        panel.Children.Add(new TextBlock{Text = LocalText.T("Identify Existing Objects"),FontSize=20,Margin=new(0,0,0,12)});
        panel.Children.Add(new TextBlock{Text = LocalText.T("Stamp Folder"),FontWeight=FontWeight.SemiBold});panel.Children.Add(folderLabel);
        choose.HorizontalAlignment=HorizontalAlignment.Left;panel.Children.Add(choose);
        panel.Children.Add(progress);panel.Children.Add(status);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};actions.Children.Add(cancel);actions.Children.Add(start);panel.Children.Add(actions);
        start.SetResource(ThemeProperty,"PrimaryButton");cancel.SetResource(ThemeProperty,"QuietButton");choose.SetResource(ThemeProperty,"QuietButton");
        void UpdateFolder(){folderLabel.Text = (folder ?? LocalText.T("Choose the Folder Containing Your .X Files"));ToolTip.SetTip(folderLabel, (folder));start.IsEnabled=folder!=null&&Directory.Exists(folder);status.Text = ("");}
        folder=initialFolder;UpdateFolder();
        choose.Click+=(_,_)=>{var picker=new OpenFolderDialog{Title = "Choose .X Objects to Identify",InitialDirectory=folder??Environment.CurrentDirectory};if(picker.ShowDialog(this)==true){folder=picker.FolderName;UpdateFolder();}};
        cancel.Click+=(_,_)=>{if(running){cancellation.Cancel();cancel.IsEnabled=false;status.Text = LocalText.T("Cancelling\u2026");}else DialogResult=false;};
        Closing+=(_,e)=>{if(running){e.Cancel=true;cancellation.Cancel();cancel.IsEnabled=false;status.Text = LocalText.T("Cancelling\u2026");}};
        Closed+=(_,_)=>cancellation.Dispose();
        start.Click+=async(_,_)=>
        {
            if(folder==null||running)return;
            running=true;start.IsEnabled=choose.IsEnabled=false;cancel.Content = LocalText.T("Cancel Scan");progress.IsIndeterminate=true;
            var updates=new Progress<StampScanProgress>(p=>{progress.IsIndeterminate=p.Total<=1;progress.Value=p.Total==0?0:100.0*p.Completed/p.Total;status.Text = LocalText.Format("{0} · {1:N0}/{2:N0} · {3:N0} matches",p.Phase,p.Completed,p.Total,p.Matches);});
            try{Result=await Task.Run(()=>StampIdentifier.Scan(document,folder,cancellation.Token,updates));running=false;DialogResult=true;}
            catch(OperationCanceledException){running=false;DialogResult=false;}
            catch(Exception ex){running=false;progress.IsIndeterminate=false;status.Text = LocalText.Format("Unable to identify objects: {0}",ex.Message);start.IsEnabled=choose.IsEnabled=true;cancel.IsEnabled=true;cancel.Content = LocalText.T("Close");}
        };
    }
}
