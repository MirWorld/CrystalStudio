namespace CrystalStudio;

/// <summary>Independent deterministic streams for course choices and retries.</summary>
internal static class RiverSeed
{
    internal static int Derive(int seed,int stream)
    {
        unchecked
        {
            uint value=(uint)seed+0x9E3779B9u*((uint)stream+1);
            value=(value^(value>>16))*0x85EBCA6Bu;
            value=(value^(value>>13))*0xC2B2AE35u;
            return (int)(value^(value>>16));
        }
    }

    internal static (int Width,int Height) MainArea(int seed,int width,int height)
    {
        var random=new Random(Derive(seed,1));
        // Reserve a varying amount for tributaries. A fixed three-fifths width
        // forced even large maps back to the same one-group main course.
        int reserveX=width>=404?random.Next(1,5)*24:0;
        int reserveY=height>=280?random.Next(1,4)*24:0;
        return (Math.Max(Math.Min(width,320),width-reserveX),height-reserveY);
    }
}
