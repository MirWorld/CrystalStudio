using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CrystalStudio;

public enum TerrainWaterMode { None, River, Waterways, Beach, RiverToSea }
public sealed record WaterAssemblyRule(int X,int Y,int AnchorX,int AnchorY,int[] States);
public sealed record TerrainBankTile(int X, int Y, int Library, int Image);
public sealed record TerrainWaterPlan(TerrainArea Area, int[] States, IReadOnlyList<TerrainBankTile> Banks, TerrainWaterCatalogue Catalogue, bool AlongVertical)
{
    public WaterState At(int x, int y) => Catalogue.States[States[y * Area.Width + x]];
}

/// <summary>Constraint assembly of original bank sprites. No rotated or fabricated tiles.</summary>
public static class TerrainWaterGenerator
{
    private sealed class Decision(int cell, int[] candidates, int mark)
    {
        public int Cell = cell, Mark = mark, Next;
        public int[] Candidates = candidates;
    }
    private static readonly (int X, int Y)[] Directions = [(1,0),(0,1),(-1,0),(0,-1)];
    private static IEnumerable<int> Bits(BigInteger domain)
    {
        byte[] bytes = domain.ToByteArray(isUnsigned: true);
        for (int i = 0; i < bytes.Length; i++)
        {
            uint b = bytes[i];
            while (b != 0) { int bit = BitOperations.TrailingZeroCount(b); yield return i * 8 + bit; b &= b - 1; }
        }
    }
    private static int Count(BigInteger domain) => domain.ToByteArray(isUnsigned: true).Sum(b => BitOperations.PopCount((uint)b));
    public static bool HasCompleteSprites(TerrainWaterPlan plan)
    {
        int width = plan.Area.Width, height = plan.Area.Height;
        var expected = new Dictionary<int,List<WaterFragment>>();
        foreach (var bank in plan.Banks)
        {
            int x = bank.X - plan.Area.X, y = bank.Y - plan.Area.Y;
            if (x < 0 || y < 0 || x >= width || y >= height) return false;
            var sprite = plan.Catalogue.Sprites.Single(s => s.Library == bank.Library && s.Image == bank.Image);
            foreach (int row in sprite.OccupiedRows)
            {
                int cy = y - row;
                if (cy < 0) { if (plan.Area.Y > 0) return false; continue; }
                int key = cy * width + x;
                if (!expected.TryGetValue(key,out var fragments)) expected[key] = fragments = [];
                fragments.Add(new WaterFragment { Library=bank.Library,Image=bank.Image,Dy=row });
            }
        }
        for (int key = 0; key < plan.States.Length; key++)
            if (!plan.Catalogue.States[plan.States[key]].Fragments.SequenceEqual(expected.GetValueOrDefault(key) ?? [])) return false;
        return true;
    }

    public static TerrainWaterPlan Plan(MapDocument document, TerrainArea area, TerrainWaterCatalogue catalogue, TerrainOptions options, CancellationToken cancellationToken)
    {
        int width = area.Width, height = area.Height;
        if (width < 64 || height < 64) throw new ArgumentException("Water needs an area of at least 64×64 map cells for the original banks and turns.");
        if(options.WaterMode==TerrainWaterMode.River&&area.X==0&&area.Y==0&&width==document.Width&&height==document.Height)
            return TerrainRiverGenerator.Plan(area,catalogue,options,cancellationToken);
        var random = new Random(options.Seed ^ 0x51A7E);
        var geometry = options.WaterMode is TerrainWaterMode.Beach or TerrainWaterMode.RiverToSea ? TerrainWaterGeometry.Create(area,options) : null;
        bool vertical = geometry?.Vertical ?? random.Next(2) == 0;
        double phase = random.NextDouble() * Math.Tau;
        double cross = vertical ? width : height, along = vertical ? height : width;
        double radius = Math.Min(options.WaterWidth, cross * .27);
        double baseline = cross * (.44 + random.NextDouble() * .12);
        double Profile(double v) => Math.Clamp(baseline + Math.Sin(v / along * Math.Tau + phase) * cross * .085 + Math.Sin(v / along * Math.PI * 5 + phase) * cross * .025, radius + 12, cross - radius - 12);
        if (area.X == 0 && area.Y == 0 && area.Width == document.Width && area.Height == document.Height && options.WaterMode is TerrainWaterMode.Beach or TerrainWaterMode.RiverToSea)
        {
            var strip = TerrainWaterStrips.Plan(area,catalogue,options,vertical,geometry?.Centre ?? (t=>Profile(t)),cancellationToken,
                waterBefore:geometry?.WaterBefore??false,radiusLeft:geometry?.LeftRadius,radiusRight:geometry?.RightRadius,allowOutside:geometry!=null);
            if (strip != null) return strip;
        }
        double Distance(int x, int y)
        {
            double u = vertical ? x : y, v = vertical ? y : x;
            double river = radius - Math.Abs(u - Profile(v));
            double SmoothUnion(double a, double b)
            {
                double blend = Math.Max(20 - Math.Abs(a-b),0) / 20;
                return Math.Max(a,b) + blend*blend*5;
            }
            double result = options.WaterMode switch
            {
                TerrainWaterMode.Beach or TerrainWaterMode.RiverToSea => geometry!.Distance(x,y),
                TerrainWaterMode.Waterways => SmoothUnion(river, u <= Profile(v) ? Math.Min(radius * .75 - Math.Abs(v - (along * .58 + Math.Sin(u / cross * Math.PI * 2 + phase) * 8)), Profile(v) - u + radius) : -1000),
                _ => river
            };
            // Internal selections retain a land border. The constraints must
            // close the native banks before they reach untouched terrain.
            if (area.X > 0) result = Math.Min(result, x - 16);
            if (area.Y > 0) result = Math.Min(result, y - 16);
            if (area.X + width < document.Width) result = Math.Min(result, width - 17 - x);
            if (area.Y + height < document.Height) result = Math.Min(result, height - 17 - y);
            return result;
        }
        var distances = new double[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) distances[y * width + x] = Distance(x, y);
        try { return PlanShape(area,catalogue,options,distances,vertical,cancellationToken,document); }
        catch (ArgumentException) when (options.WaterMode == TerrainWaterMode.RiverToSea && geometry != null)
        {
            if (area.X == 0 && area.Y == 0 && width == document.Width && height == document.Height)
            {
                // Some native diagonal families have no complete ending on this
                // map boundary. Keep the seeded inlet and flare choices and try
                // the other approach using its own original bank pieces.
                var alternate=TerrainWaterGeometry.Create(area,options,!geometry.Vertical);
                var strip=TerrainWaterStrips.Plan(area,catalogue,options,alternate.Vertical,alternate.Centre,cancellationToken,
                    radiusLeft:alternate.LeftRadius,radiusRight:alternate.RightRadius,allowOutside:true);
                if(strip!=null)return strip;
            }
            throw;
        }
    }

    public static TerrainWaterPlan PlanShape(TerrainArea area,TerrainWaterCatalogue catalogue,TerrainOptions options,
        double[] distances,bool vertical,CancellationToken cancellationToken,MapDocument? document = null,int[]? fixedStates = null,Func<int,int,double>? shapeError = null,IReadOnlyDictionary<int,WaterAssemblyRule[]>? assemblies = null)
    {
        int width=area.Width,height=area.Height;
        document ??= new MapDocument(width,height);
        int n = catalogue.States.Count;
        BigInteger all = (BigInteger.One << n) - 1, water = BigInteger.Zero;
        foreach (var s in catalogue.States.Where(s => s.Water)) water |= BigInteger.One << s.Id;
        BigInteger land = all ^ water;
        bool hasGroundShapes=catalogue.States.Any(s=>s.GroundIndex>=0);
        var groundGroups=catalogue.States.GroupBy(s=>s.GroundKey).Select(g=>g.Aggregate(BigInteger.Zero,(bits,s)=>bits|(BigInteger.One<<s.Id))).ToArray();
        var phases = new BigInteger[4];
        foreach(var s in catalogue.States) for(int p=0;p<4;p++) if((s.Phases&(1<<p))!=0) phases[p]|=BigInteger.One<<s.Id;
        int phaseCount=catalogue.Joins.Any(joins=>joins.Any(join=>join.Phases!=15))?4:1;
        var allows = Enumerable.Range(0,phaseCount*4).Select(_ => new BigInteger[n]).ToArray();
        for (int p=0;p<phaseCount;p++) for (int d = 0; d < 4; d++) foreach (var join in catalogue.Joins[d]) if((join.Phases&(1<<p))!=0) allows[p*4+d][join.A] |= BigInteger.One << join.B;
        int words = (n + 63) / 64;
        var support = Enumerable.Range(0,phaseCount*4).Select(_ => new ulong[n * words]).ToArray();
        for (int d = 0; d < phaseCount*4; d++) for (int i = 0; i < n; i++)
            allows[d][i].TryWriteBytes(MemoryMarshal.AsBytes(support[d].AsSpan(i * words,words)),out _,isUnsigned:true);
        var fit = new BigInteger[height];
        for (int y = 0; y < height; y++)
            foreach (var s in catalogue.States)
                if (s.Fragments.All(f => y + f.Dy < height)) fit[y] |= BigInteger.One << s.Id;
        var initial = new BigInteger[width * height];
        var assemblyMasks=assemblies?.ToDictionary(pair=>pair.Key,pair=>pair.Value.Select(rule=>(Rule:rule,Mask:rule.States.Aggregate(BigInteger.Zero,(bits,id)=>bits|(BigInteger.One<<id)))).ToArray());
        var unionCache = new Dictionary<(BigInteger,int), BigInteger>();
        BigInteger Union(BigInteger domain, int direction)
        {
            if (unionCache.TryGetValue((domain,direction), out var cached)) return cached;
            Span<ulong> bits = stackalloc ulong[words]; bits.Clear();
            foreach (int i in Bits(domain))
                for (int word = 0; word < words; word++) bits[word] |= support[direction][i * words + word];
            BigInteger union = new(MemoryMarshal.AsBytes(bits),isUnsigned:true);
            if (unionCache.Count > 60_000) unionCache.Clear();
            unionCache[(domain,direction)] = union; return union;
        }
        for (int attempt = 0; attempt < 16; attempt++)
        {
            System.Diagnostics.Trace.WriteLine($"Water {options.WaterMode}: attempt {attempt}");
            cancellationToken.ThrowIfCancellationRequested();
            double band = (options.WaterMode == TerrainWaterMode.Waterways ? 16 : 10) + attempt / 4 * 2;
            for (int k = 0; k < initial.Length; k++)
            {
                int x = k % width, y = k / width;
                // Ground occupies 2×2 cells; one body class must cover all four.
                double distance = distances[(y & ~1) * width + (x & ~1)];
                initial[k] = (distance > band ? new BigInteger(2) : distance < -band ? BigInteger.One : all) & fit[y] & phases[(x&1)+(y&1)*2];
                if(fixedStates!=null&&fixedStates[k]>=0)initial[k]&=BigInteger.One<<fixedStates[k];
                else if(shapeError!=null&&assemblies==null)
                {
                    BigInteger matched=BigInteger.Zero;
                    foreach(int id in Bits(initial[k]))if(shapeError(k,id)<=8)matched|=BigInteger.One<<id;
                    initial[k]=matched;
                }
                if (area.X > 0 && x < 8 || area.Y > 0 && y < 8 || area.X + width < document.Width && x >= width - 8 || area.Y + height < document.Height && y >= height - 8) initial[k] = BigInteger.One;
            }
            var wave = (BigInteger[])initial.Clone();
            var queued = Enumerable.Repeat(true, wave.Length).ToArray();
            var queue = new Queue<int>(Enumerable.Range(0,wave.Length));
            var revisions = new int[wave.Length];
            var entropy = new PriorityQueue<(int Cell, int Revision), (int Count, double Tie)>();
            var rng = new Random(unchecked(options.Seed * 397 + attempt * 7919));
            var decisions = new Stack<Decision>();
            var trail = new List<(int Cell, BigInteger Old)>();
            bool failed = false; int steps = 0, backtracks = 0;
            void Change(int cell, BigInteger domain)
            {
                if (decisions.Count > 0) trail.Add((cell,wave[cell]));
                wave[cell] = domain; revisions[cell]++;
                if (!queued[cell]) { queued[cell] = true; queue.Enqueue(cell); }
                int count = Count(domain);
                if (count > 1) entropy.Enqueue((cell, revisions[cell]), (count+(assemblyMasks!=null&&!(domain&3).IsZero?n:0), rng.NextDouble()));
            }
            while (true)
            {
                while (queue.TryDequeue(out int cell))
                {
                    if (steps % 10000 == 0) System.Diagnostics.Trace.WriteLine($"Water propagation {steps}, queue {queue.Count}");
                    if ((steps++ & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                    queued[cell] = false;
                    int x = cell % width, y = cell / width; BigInteger domain = wave[cell];
                    for (int direction = 0; direction < 4; direction++)
                    {
                        int nx = x + Directions[direction].X, ny = y + Directions[direction].Y;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        BigInteger permitted = Union(domain,(phaseCount==1?0:((x&1)+(y&1)*2)*4)+direction);
                        bool sharedGround = direction == 0 && x % 2 == 0 || direction == 2 && x % 2 == 1 || direction == 1 && y % 2 == 0 || direction == 3 && y % 2 == 1;
                        if (sharedGround)
                        {
                            if(hasGroundShapes)
                            {
                                BigInteger compatible=BigInteger.Zero;
                                foreach(var group in groundGroups)if(!(domain&group).IsZero)compatible|=group;
                                permitted&=compatible;
                            }
                            else
                            {
                                if ((domain & water).IsZero) permitted &= land;
                                if ((domain & land).IsZero) permitted &= water;
                            }
                        }
                        int neighbor = ny * width + nx; BigInteger reduced = wave[neighbor] & permitted;
                        if (reduced.IsZero) { failed = true; break; }
                        if (reduced != wave[neighbor]) Change(neighbor,reduced);
                    }
                    if (failed) break;
                    if(assemblyMasks!=null&&BigInteger.IsPow2(domain)&&assemblyMasks.TryGetValue((int)domain.GetBitLength()-1,out var rules))
                    {
                        foreach(var (rule,mask) in rules)
                        {
                            int nx=x+rule.X,ny=y+rule.Y;
                            if(nx<0||nx>=width||ny<0||ny>=height||x+rule.AnchorX<0||x+rule.AnchorX>=width||y+rule.AnchorY<0||y+rule.AnchorY>=height)continue;
                            int neighbor=ny*width+nx;var reduced=wave[neighbor]&mask;
                            if(reduced.IsZero){failed=true;break;}
                            if(reduced!=wave[neighbor])Change(neighbor,reduced);
                        }
                    }
                    if (failed) break;
                }
                if (failed)
                {
                    bool recovered = false;
                    while (decisions.TryPeek(out var decision) && backtracks++ < 2000)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        foreach (int pending in queue) queued[pending] = false;
                        queue.Clear();
                        for (int k = trail.Count - 1; k >= decision.Mark; k--)
                        {
                            var old = trail[k]; wave[old.Cell] = old.Old; revisions[old.Cell]++;
                            int count = Count(old.Old);
                            if (count > 1) entropy.Enqueue((old.Cell,revisions[old.Cell]),(count+(assemblyMasks!=null&&!(old.Old&3).IsZero?n:0),rng.NextDouble()));
                        }
                        trail.RemoveRange(decision.Mark,trail.Count-decision.Mark);
                        if (decision.Next < decision.Candidates.Length)
                        {
                            Change(decision.Cell,BigInteger.One << decision.Candidates[decision.Next++]);
                            failed = false; recovered = true; break;
                        }
                        decisions.Pop();
                    }
                    if (!recovered) break;
                    continue;
                }
                int chosen = -1;
                while (entropy.TryDequeue(out var entry, out _))
                    if (entry.Revision == revisions[entry.Cell]) { chosen = entry.Cell; break; }
                if (chosen < 0)
                {
                    int size = int.MaxValue;
                    for (int k = 0; k < wave.Length; k++) { int count = Count(wave[k]); if (count > 1 && count < size) { chosen = k; size = count; } }
                    if (chosen < 0) break;
                }
                bool AssemblyFits(int id)
                {
                    if(assemblyMasks==null||!assemblyMasks.TryGetValue(id,out var rules))return true;
                    int x=chosen%width,y=chosen/width;
                    foreach(var (rule,mask) in rules)
                    {
                        int nx=x+rule.X,ny=y+rule.Y;
                        if(nx<0||nx>=width||ny<0||ny>=height||x+rule.AnchorX<0||x+rule.AnchorX>=width||y+rule.AnchorY<0||y+rule.AnchorY>=height)continue;
                        if((wave[ny*width+nx]&mask).IsZero)return false;
                    }
                    return true;
                }
                var candidates = Bits(wave[chosen]).Where(AssemblyFits).ToArray();
                if(candidates.Length==0){failed=true;continue;}
                double Weight(int i) => fixedStates == null
                    ? Math.Sqrt(catalogue.States[i].Weight) * (catalogue.States[i].Water == (distances[chosen] > 0) ? 4 : 1)
                    : (i<2?500:1) * (shapeError==null ? (catalogue.States[i].Water == (distances[chosen] > 0) ? 20 : .1) : Math.Exp(-shapeError(chosen,i)*2));
                double AssemblyError(int id)
                {
                    double error=shapeError!(chosen,id)*4;int samples=4;
                    if(assemblyMasks!=null&&assemblyMasks.TryGetValue(id,out var rules))
                    {
                        int x=chosen%width,y=chosen/width;
                        foreach(var (rule,mask) in rules)
                        {
                            int nx=x+rule.X,ny=y+rule.Y;
                            if(nx<0||nx>=width||ny<0||ny>=height||x+rule.AnchorX<0||x+rule.AnchorX>=width||y+rule.AnchorY<0||y+rule.AnchorY>=height)continue;
                            int key=ny*width+nx;error+=Bits(wave[key]&mask).Min(s=>shapeError(key,s));samples++;
                        }
                    }
                    return error/samples+(id<2?-.1:0)+rng.NextDouble()*.02;
                }
                candidates = assemblyMasks==null ? candidates.OrderBy(i => -Math.Log(Math.Max(double.Epsilon,rng.NextDouble())) / Weight(i)).ToArray()
                    : candidates.OrderBy(AssemblyError).ToArray();
                var next = new Decision(chosen,candidates,trail.Count) { Next = 1 }; decisions.Push(next);
                Change(chosen,BigInteger.One << candidates[0]);
            }
            if (failed) { System.Diagnostics.Trace.WriteLine($"Water contradiction: {steps} propagation steps"); continue; }
            var states = wave.Select(v => checked((int)v.GetBitLength() - 1)).ToArray();
            var banks = new Dictionary<(int,int),TerrainBankTile>();
            for (int y = 0; y < height && !failed; y++) for (int x = 0; x < width; x++)
            {
                var state = catalogue.States[states[y * width + x]];
                if (state.GroundKey != catalogue.States[states[(y & ~1) * width + (x & ~1)]].GroundKey) { failed = true; break; }
                foreach (var f in state.Fragments)
                {
                    var tile = new TerrainBankTile(area.X + x + f.Dx, area.Y + y + f.Dy, f.Library, f.Image);
                    var key = (tile.X,tile.Y);
                    if (banks.TryGetValue(key,out var existing) && existing != tile) { failed = true; break; }
                    banks[key] = tile;
                }
            }
            if (failed || banks.Count == 0 || !states.Any(s => catalogue.States[s].Water) || !states.Any(s => !catalogue.States[s].Water)) continue;
            var plan = new TerrainWaterPlan(area,states,banks.Values.OrderBy(t => t.Y).ThenBy(t => t.X).ToList(),catalogue,vertical);
            if (!HasCompleteSprites(plan)) continue;
            return plan;
        }
        throw new ArgumentException("The original shore pieces could not form a continuous bank at this size. Increase the water width or area, or try another seed.");
    }
}
