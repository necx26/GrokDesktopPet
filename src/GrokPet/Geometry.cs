using System.Windows;
using System.Windows.Media;

namespace GrokPet;

// Ported from the supplied grok_sphere.py. Eyes are patches on a sphere,
// not flat sprites translated over its surface. Hidden arcs follow the limb.
public static class SphereEyes
{
    const double R=109, D=872, EyeR=13.2, Straight=15.5;
    static readonly double F=Math.Sqrt(D*D-R*R), Limb=R*R/D;
    static readonly List<Point> Capsule=Build();
    static List<Point> Build() {
        var p=new List<Point>();
        for(int i=0;i<=10;i++)p.Add(new(EyeR*Math.Cos(Math.PI+i*Math.PI/10),-Straight+EyeR*Math.Sin(Math.PI+i*Math.PI/10)));
        for(int i=1;i<6;i++)p.Add(new(EyeR,-Straight+2*Straight*i/6));
        for(int i=0;i<=10;i++)p.Add(new(EyeR*Math.Cos(i*Math.PI/10),Straight+EyeR*Math.Sin(i*Math.PI/10)));
        for(int i=1;i<6;i++)p.Add(new(-EyeR,Straight-2*Straight*i/6));
        return p;
    }
    public static List<Point> Outline(double yaw,double pitch,double opening,int side) {
        double a=yaw%360*Math.PI/180,b=(pitch-10)*Math.PI/180;
        var surface=new List<(double x,double y,double z)>();
        foreach(var uv in Capsule) {
            double lid=7+2.4*(1-Math.Pow(uv.X/EyeR,2)),sx=side*27.8+uv.X,sy=-17.8+opening*uv.Y+(1-opening)*lid;
            double r2=sx*sx+sy*sy,ray=r2/(F*F),z=(ray*D+Math.Sqrt(R*R-r2))/(1+ray),lift=(D-z)/F;
            double x=sx*lift,y=sy*lift,tx=x*Math.Cos(a)+z*Math.Sin(a),tz=z*Math.Cos(a)-x*Math.Sin(a);
            surface.Add((tx,y*Math.Cos(b)-tz*Math.Sin(b),y*Math.Sin(b)+tz*Math.Cos(b)));
        }
        var visible=surface.Select(p=>p.z>=Limb).ToArray();
        Point Edge(double angle)=>new(128+R*Math.Cos(angle),128+R*Math.Sin(angle));
        if(!visible.Any(v=>v)){var p=surface.MaxBy(p=>p.z);return Enumerable.Repeat(Edge(Math.Atan2(p.y,p.x)),surface.Count).ToList();}
        var result=surface.Select(p=>new Point(128+p.x*F/(D-p.z),128+p.y*F/(D-p.z))).ToList();
        (double,double) Crossing(int first,int second) {
            var p=surface[first];var q=surface[second];double f=(Limb-p.z)/(q.z-p.z);
            return(f,Math.Atan2(p.y+f*(q.y-p.y),p.x+f*(q.x-p.x)));
        }
        int count=surface.Count;
        for(int start=0;start<count;start++) {
            if(visible[start]||!visible[(start+count-1)%count])continue;
            int end=start+1;while(!visible[end%count])end++;
            var(fa,aa)=Crossing((start+count-1)%count,start);var(fb,ab)=Crossing((end-1)%count,end%count);
            double enter=start-1+fa,leave=end-1+fb,sweep=Math.IEEERemainder(ab-aa,2*Math.PI);
            for(int i=start;i<end;i++)result[i%count]=Edge(aa+sweep*(i-enter)/(leave-enter));
        }
        return result;
    }
    public static StreamGeometry Path(double yaw,double pitch,double opening,int side) {
        var p=Outline(yaw,pitch,opening,side);var g=new StreamGeometry();
        Point Mid(Point a,Point b)=>new((a.X+b.X)/2,(a.Y+b.Y)/2);
        using(var c=g.Open()){c.BeginFigure(Mid(p[^1],p[0]),true,true);for(int i=0;i<p.Count;i++)c.QuadraticBezierTo(p[i],Mid(p[i],p[(i+1)%p.Count]),true,false);}
        g.Freeze();return g;
    }
}
