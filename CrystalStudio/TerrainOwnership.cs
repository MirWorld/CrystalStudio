namespace CrystalStudio;

public sealed record TerrainBankFootprint(int X, int Y, int Height);

public sealed partial class MapDocument
{
    internal void CheckTerrainBankBoundary(TerrainArea area)
    {
        foreach (var stamp in placedStamps.Where(s => s.GeneratedBanks != null))
        {
            Dictionary<(int X,int Y),StampDelta>? changes = null;
            foreach (var bank in stamp.GeneratedBanks!)
            {
                int x = stamp.X + bank.X, y = stamp.Y + bank.Y;
                int top = y + 1 - (bank.Height + 31) / 32;
                bool intersects = x >= area.X && x < area.X + area.Width && y >= area.Y && top < area.Y + area.Height;
                if (!intersects || !(y >= area.Y + area.Height || top >= 0 && top < area.Y)) continue;
                // Whole banks need no ownership lookup. Index only if the selection cuts a sprite.
                changes ??= stamp.Changes.ToDictionary(d => (d.X,d.Y));
                if (Get(x,y).Foreground != changes[(bank.X,bank.Y)].OwnedFronts![0]) continue;
                throw new ArgumentException("The selection cuts through a previously generated bank. Extend it to include the complete bank sprite.");
            }
        }
    }

    internal void RemoveGeneratedBanks(TerrainArea area, EditHistory history, Action<int,int> invalidate)
    {
        foreach (var stamp in placedStamps.Where(s => s.GeneratedBanks != null).ToArray())
        {
            var remove = stamp.Changes.Where(d => stamp.X + d.X >= area.X && stamp.X + d.X < area.X + area.Width && stamp.Y + d.Y >= area.Y && stamp.Y + d.Y < area.Y + area.Height).ToList();
            if (remove.Count == 0) continue;
            history.RecordStamps(this);
            foreach (var delta in remove)
            {
                int x = stamp.X + delta.X, y = stamp.Y + delta.Y;
                history.Record(this,x,y);
                var cell = Get(x,y).Copy();
                // Retain any image the user has substituted for the generated bank.
                if (cell.Foreground == delta.OwnedFronts![0]) cell.Foreground = delta.Before.Foreground;
                Set(x,y,cell); invalidate(x,y);
            }
            ForgetStamp(stamp);
            var keys = remove.Select(d => (d.X,d.Y)).ToHashSet();
            var remaining = stamp.Changes.Where(d => !keys.Contains((d.X,d.Y))).ToList();
            if (remaining.Count > 0) AddStamp(stamp with
            {
                Changes = remaining,
                Cells = stamp.Cells.Where(c => !keys.Contains((c.X,c.Y))).ToList(),
                GeneratedBanks = stamp.GeneratedBanks!.Where(b => !keys.Contains((b.X,b.Y))).ToList()
            });
        }
    }

    internal void RememberGeneratedBanks(List<StampDelta> changes, TerrainWaterCatalogue catalogue, EditHistory history)
    {
        if (changes.Count == 0) return;
        var heights = catalogue.Sprites.ToDictionary(s => (s.Library,s.Image), s => s.Height);
        history.RecordStamps(this);
        var cells = changes.Select(d => new StampCell(d.X,d.Y,new Cell { Foreground = d.OwnedFronts!.FirstOrDefault() })).ToList();
        AddStamp(new PlacedStamp(Guid.NewGuid(),"Generated water banks",0,0,cells,changes)
        {
            GeneratedBanks = changes.Select(d => new TerrainBankFootprint(d.X,d.Y,heights[(d.OwnedFronts![0].Library,d.OwnedFronts[0].Image)])).ToList()
        });
    }
}
