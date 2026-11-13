using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CylinderWorld;
using EarthMap.Core;

static class RouteAudit
{
    // A coarse search is followed by full-resolution corridor verification; failed cells are excluded and rerouted.
    public static void Run(TerrainModel model,SeamParams p,string output)
    {
        Audit("canada",model,p,output,79,-145,66,-50,71,-135,74,-65);
        Audit("siberia",model,p,output,82,55,68,178,75,65,72,165);
    }

    static void Audit(string name,TerrainModel m,SeamParams p,string output,double north,double west,double south,double east,
        double startLat,double startLon,double endLat,double endLon)
    {
        const int step=32,radius=6,draft=4;
        m.Geometry.FromGeo(north,west,out double wx,out double nz);
        m.Geometry.FromGeo(south,east,out double ex,out double sz);
        int x0=(int)Math.Min(wx,ex),z0=(int)nz,w=(int)Math.Abs(ex-wx)/step+1,h=(int)(sz-nz)/step+1;
        var cells=new EarthPolarModel.Column[w*h];
        var wet=new bool[w*h];
        for(int j=0;j<h;j++) for(int i=0;i<w;i++)
        {
            int k=j*w+i;
            cells[k]=p.EarthPolar.Sample(x0+i*step,z0+j*step);
            wet[k]=cells[k].Ocean && p.SeaLevel-1-cells[k].Top>=draft;
        }
        int Index(double lat,double lon)
        {
            m.Geometry.FromGeo(lat,lon,out double x,out double z);
            return (int)Math.Round((z-z0)/step)*w+(int)Math.Round((x-x0)/step);
        }
        int start=Index(startLat,startLon),end=Index(endLat,endLon);
        var summary=new List<string>{$"# {name} sea route check", "",$"World {p.WorldWidth} × {p.WorldLength}, sea {p.SeaLevel}, seed {p.NoiseSeed}.",
            "Search uses Earth terrain and its actual projection. Candidate routes are verified block by block across a 13-block-wide square corridor with at least four water blocks of depth. This is a model audit, not a voyage through already-generated save chunks.",""};
        foreach(string season in new[]{"terrain-only","current-static","summer","winter"})
        {
            bool Allowed(int k)
            {
                if(!wet[k]) return false;
                double x=x0+k%w*step,z=z0+k/w*step;
                return season=="terrain-only" || (season=="current-static" ? !p.HasArcticIce(x,z,cells[k])
                    : SeaIcePolicy.At(p,x,z,cells[k],season=="summer"?SeaIcePolicy.NorthMinimum:SeaIcePolicy.NorthMaximum)==SeaIceKind.Open);
            }
            var excluded=new HashSet<int>();
            List<int> path=null;
            int attempts=0,verified=0,minDepth=int.MaxValue;
            // Only positive passability claims require full-resolution validation; a failed grid search is reported as such.
            while(attempts++<100)
            {
                path=Find(w,h,start,end,k=>!excluded.Contains(k)&&Allowed(k));
                if(path==null) break;
                var seen=new HashSet<long>();
                int fail=-1;
                minDepth=int.MaxValue;
                for(int a=0;a<path.Count && fail<0;a++)
                {
                    int cell=path[a],prev=a==0?cell:path[a-1];
                    int ax=x0+prev%w*step,az=z0+prev/w*step,bx=x0+cell%w*step,bz=z0+cell/w*step;
                    int length=Math.Max(Math.Abs(bx-ax),Math.Abs(bz-az));
                    for(int t=0;t<=length && fail<0;t++)
                    {
                        int x=length==0?ax:ax+(bx-ax)*t/length,z=length==0?az:az+(bz-az)*t/length;
                        for(int dz=-radius;dz<=radius && fail<0;dz++) for(int dx=-radius;dx<=radius;dx++)
                        {
                            int xx=x+dx,zz=z+dz;long key=((long)xx<<32)|(uint)zz;
                            if(!seen.Add(key)) continue;
                            var c=p.EarthPolar.Sample(xx,zz);
                            int depth=p.SeaLevel-1-c.Top;
                            bool ice=season=="terrain-only"?false:season=="current-static"?p.HasArcticIce(xx,zz,c):
                                SeaIcePolicy.At(p,xx,zz,c,season=="summer"?SeaIcePolicy.NorthMinimum:SeaIcePolicy.NorthMaximum)!=SeaIceKind.Open;
                            if(!c.Ocean || depth<draft || ice) {fail=cell;break;}
                            minDepth=Math.Min(minDepth,depth);
                        }
                    }
                }
                if(fail<0) {verified=seen.Count;break;}
                excluded.Add(fail);
                path=null;
            }
            string result=path==null?"No verified route found":$"PASS: {verified:N0} corridor blocks checked, minimum depth {minDepth}";
            Console.WriteLine($"ROUTE {name} {season}: {result}, searches={attempts}");
            summary.Add($"- **{season}:** {result}. Searches: {attempts}.");
            if(path!=null)
            {
                var lines=new List<string>{"latitude,longitude,x,z"};
                foreach(int k in path)
                {
                    int x=x0+k%w*step,z=z0+k/w*step;var g=m.Geometry.ToGeo(x,z);
                    lines.Add(FormattableString.Invariant($"{g.Lat:F6},{g.Lon:F6},{x},{z}"));
                }
                File.WriteAllLines(Path.Combine(output,$"{name}-{season}-route.csv"),lines);
            }
            {
                var pixels=new byte[w*h*3];
                for(int k=0;k<cells.Length;k++)
                {
                    byte r=wet[k]?(byte)34:(byte)111,g=wet[k]?(byte)83:(byte)115,b=wet[k]?(byte)123:(byte)95;
                    if(wet[k]&&!Allowed(k)) {r=221;g=234;b=240;}
                    pixels[k*3]=r;pixels[k*3+1]=g;pixels[k*3+2]=b;
                }
                if(path!=null) foreach(int k in path) {pixels[k*3]=255;pixels[k*3+1]=95;pixels[k*3+2]=45;}
                Png.WriteRgb(Path.Combine(output,$"{name}-{season}.png"),w,h,pixels);
            }
        }
        File.WriteAllLines(Path.Combine(output,$"{name}-routes.md"),summary);
    }

    static List<int> Find(int w,int h,int start,int end,Func<int,bool> allowed)
    {
        if(!allowed(start)||!allowed(end)) return null;
        var previous=new int[w*h];Array.Fill(previous,-1);
        var distance=new int[w*h];Array.Fill(distance,int.MaxValue);
        var queue=new PriorityQueue<int,int>();distance[start]=0;queue.Enqueue(start,0);
        while(queue.TryDequeue(out int at,out _))
        {
            if(at==end)
            {
                var result=new List<int>();for(int k=end;k!=-1;k=previous[k])result.Add(k);result.Reverse();return result;
            }
            int x=at%w,z=at/w;
            foreach(var (dx,dz) in new[]{(1,0),(-1,0),(0,1),(0,-1)})
            {
                int xx=x+dx,zz=z+dz;if(xx<0||xx>=w||zz<0||zz>=h)continue;
                int next=zz*w+xx,score=distance[at]+1;
                if(score>=distance[next]||!allowed(next))continue;
                distance[next]=score;previous[next]=at;
                queue.Enqueue(next,score+Math.Abs(xx-end%w)+Math.Abs(zz-end/w));
            }
        }
        return null;
    }
}
