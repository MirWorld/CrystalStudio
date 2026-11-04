using System.Runtime.CompilerServices;

namespace CrystalStudio;

/// <summary>Bounded immutable geometry cache, independent of document contents and terrain materials.</summary>
internal static class NativeWaterPlanCache
{
    internal readonly record struct Key(int Seed,int Width,int Height,bool Combined,bool Bridge,bool Fallback=false,bool Compact=false);
    internal sealed record Result(TerrainCoastPlacement? Placement,string Failure);
    private sealed class Plans
    {
        internal readonly object Gate=new();
        internal readonly LinkedList<(Key Key,Result Value)> Recent=[];
    }
    private static readonly ConditionalWeakTable<RiverShoreComponents,Plans> Catalogues=new();
    internal static Result Get(RiverShoreComponents shore,Key key,Func<TerrainCoastPlacement> build,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();var plans=Catalogues.GetValue(shore,_=>new());
        lock(plans.Gate)
        {
            for(var node=plans.Recent.First;node!=null;node=node.Next)
                if(node.Value.Key==key)
                {
                    var value=node.Value.Value;plans.Recent.Remove(node);plans.Recent.AddFirst((key,value));return value;
                }
        }
        Result result;
        try{result=new(build(),"");}
        catch(ArgumentException error){result=new(null,error.Message);}
        cancellation.ThrowIfCancellationRequested();
        lock(plans.Gate)
        {
            for(var node=plans.Recent.First;node!=null;node=node.Next)
                if(node.Value.Key==key){plans.Recent.Remove(node);break;}
            plans.Recent.AddFirst((key,result));
            while(plans.Recent.Count>12)plans.Recent.RemoveLast();
        }
        return result;
    }
}
