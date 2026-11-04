namespace CrystalStudio;

/// <summary>Seeded large-scale shore geometry; the native planner supplies the artwork.</summary>
internal sealed record TerrainWaterGeometry(bool Vertical, bool WaterBefore,
    Func<int,double> Centre, Func<int,double> LeftRadius, Func<int,double> RightRadius,
    Func<int,int,double> Distance)
{
    public static TerrainWaterGeometry Create(TerrainArea area, TerrainOptions options, bool? verticalOverride = null)
    {
        var random = new Random(unchecked(options.Seed * 16777619 ^ 0x2A613F));
        bool preferredVertical = random.Next(2) == 0;
        bool vertical = verticalOverride ?? preferredVertical;
        int across = vertical ? area.Width : area.Height, along = vertical ? area.Height : area.Width;
        double Unit(double t) => Math.Clamp(t / Math.Max(1,along - 1),0,1);
        static double Smooth(double t) => t*t*(3-2*t);

        if (options.WaterMode == TerrainWaterMode.Beach)
        {
            bool before = random.Next(2) == 0;
            double baseline = across * (before ? options.WaterCoverage : 1-options.WaterCoverage);
            baseline += (random.NextDouble()-.5)*across*.18;
            // The shore turns gradually between flatter and steeper sections.
            // Opposite water sides use the corresponding original bank family.
            double reach = Math.Min(across*(.5+random.NextDouble()*.28),along*(vertical ? before ? .85 : .43 : .72));
            double direction = before ? -1 : 1;
            double skew = .22+random.NextDouble()*.56;
            double bend = .82+random.NextDouble()*.16;
            int shape = random.Next(3);
            double Progress(double t)
            {
                double eased = shape switch
                {
                    0 => Smooth(t),
                    1 => t*t,
                    _ => 1-(1-t)*(1-t)
                };
                return t*(1-bend)+eased*bend;
            }
            double Centre(int t) => baseline+direction*reach*(Progress(Unit(t))-skew);
            double Distance(int x,int y)
            {
                int u=vertical?x:y,t=vertical?y:x;
                double derivative=(Centre(Math.Min(along-1,t+1))-Centre(Math.Max(0,t-1)))/2;
                return (before?Centre(t)-u:u-Centre(t))/Math.Sqrt(1+derivative*derivative);
            }
            return new(vertical,before,Centre,_=>0,_=>0,Distance);
        }

        // A different inlet position, approach, flare length and widening on
        // each bank produce broad estuaries, angled mouths and narrower bays.
        double radius = Math.Min(options.WaterWidth,across*.27);
        double bankMargin = vertical ? 12 : 16;
        double mouth = Math.Clamp(across*(.28+random.NextDouble()*.44),radius+8,across-radius-bankMargin);
        double inlet = Math.Clamp(across*(.3+random.NextDouble()*.4),radius+12,across-radius-bankMargin);
        double bendAmount = (random.NextDouble()-.5)*across*.16;
        double approach = .3+random.NextDouble()*.45;
        double leftReach = along*(.57+random.NextDouble()*.4);
        double rightReach = along*(.57+random.NextDouble()*.4);
        double leftSea = mouth+across*(.02+random.NextDouble()*.1);
        double rightExtra = across*(.02+random.NextDouble()*.1);
        // Horizontal banks anchor below their visible pixels. Keep this bank
        // inside the lower boundary so its complete sprite can be placed.
        double rightSea = vertical ? across-mouth+rightExtra : across-mouth-bankMargin;
        double leftPower = .8+random.NextDouble()*.6;
        double rightPower = .8+random.NextDouble()*.6;
        // Coast reach changes how far the sea extends towards the inlet.
        double reachFactor = .72+options.WaterCoverage*.6;
        leftReach*=reachFactor;rightReach*=reachFactor;
        double MouthCentre(int t)
        {
            double p=Unit(t),a=Smooth(Math.Clamp(p/approach,0,1));
            return mouth+(inlet-mouth)*a+bendAmount*Math.Sin(p*Math.PI)*Math.Sin(p*Math.PI*2);
        }
        double Left(int t)=>radius+(leftSea-radius)*Math.Pow(Math.Clamp(1-t/leftReach,0,1),leftPower);
        double Right(int t)=>radius+(rightSea-radius)*Math.Pow(Math.Clamp(1-t/rightReach,0,1),rightPower);
        double MouthDistance(int x,int y)
        {
            int u=vertical?x:y,t=vertical?y:x;
            double lo=MouthCentre(t)-Left(t),hi=MouthCentre(t)+Right(t);
            double loSlope=((MouthCentre(Math.Min(along-1,t+1))-Left(Math.Min(along-1,t+1)))-(MouthCentre(Math.Max(0,t-1))-Left(Math.Max(0,t-1))))/2;
            double hiSlope=((MouthCentre(Math.Min(along-1,t+1))+Right(Math.Min(along-1,t+1)))-(MouthCentre(Math.Max(0,t-1))+Right(Math.Max(0,t-1))))/2;
            return Math.Min((u-lo)/Math.Sqrt(1+loSlope*loSlope),(hi-u)/Math.Sqrt(1+hiSlope*hiSlope));
        }
        return new(vertical,false,MouthCentre,Left,Right,MouthDistance);
    }
}
