using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

// Selection is a bounded shape relative to its surroundings, not a known RGB
// value. Work on luminance only; a linear background gradient cancels out.
public sealed class SelectionGeometry {
    readonly int width,height;
    readonly byte[] gray;
    public sealed class Evidence {
        public Rectangle Bounds;
        public double Score,Signal,Noise,PairedEdges,SideEdges,PeerExcess;
    }
    public SelectionGeometry(Bitmap bitmap) {
        width=bitmap.Width;height=bitmap.Height;gray=new byte[width*height];
        var data=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try {
            var row=new byte[width*4];
            for(int y=0;y<height;y++) {
                Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),row,0,row.Length);
                for(int x=0;x<width;x++)gray[y*width+x]=(byte)((row[x*4]+row[x*4+1]+row[x*4+2])/3);
            }
        } finally {bitmap.UnlockBits(data);}
    }
    static double Median(IEnumerable<double> source) {
        var a=source.OrderBy(v=>v).ToArray();
        return a.Length==0?0:a.Length%2==1?a[a.Length/2]:(a[a.Length/2-1]+a[a.Length/2])/2;
    }
    double Patch(double x,double y) {
        int ix=Math.Max(1,Math.Min(width-2,(int)Math.Round(x)));
        int iy=Math.Max(1,Math.Min(height-2,(int)Math.Round(y)));
        double sum=0;
        for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)sum+=gray[(iy+dy)*width+ix+dx];
        return sum/9;
    }
    // Predict the unselected background at the inside point from two outside
    // patches. This removes a local slope instead of demanding a uniform fill.
    double Edge(double x,double y,double dx,double dy,double u) {
        double inside=Patch(x+dx*4*u,y+dy*4*u);
        double outside=Patch(x-dx*2*u,y-dy*2*u);
        double farther=Patch(x-dx*8*u,y-dy*8*u);
        return inside-2*outside+farther;
    }
    double Noise(double x,double y,double dx,double dy,double u) {
        double ox=x-dx*2*u,oy=y-dy*2*u;
        return Math.Abs(Patch(ox-dy*4*u,oy+dx*4*u)-2*Patch(ox,oy)+Patch(ox+dy*4*u,oy-dx*4*u));
    }
    double CornerReturn(Rectangle b,bool upper,bool left,double u) {
        double x=left?b.Left+2*u:b.Right-1-2*u;
        double outside=left?b.Left-3*u:b.Right-1+3*u;
        double y=upper?b.Top+2*u:b.Bottom-1-2*u,mid=b.Top+b.Height/2.0;
        return (Patch(x,y)-Patch(x,mid))-(Patch(outside,y)-Patch(outside,mid));
    }
    public Evidence Measure(Rectangle bounds,double u) {
        var e=new Evidence {Bounds=bounds};
        if(bounds.Width<100*u || bounds.Height<18*u || bounds.Height>44*u
            || bounds.Left<2 || bounds.Right>=width-2 || bounds.Top<15*u || bounds.Bottom+15*u>=height)return e;
        var upper=new List<double>();var lower=new List<double>();var noise=new List<double>();
        for(double x=bounds.Left+Math.Max(14*u,bounds.Width*.08);x<=bounds.Right-Math.Max(14*u,bounds.Width*.08);x+=Math.Max(3,6*u)) {
            upper.Add(Edge(x,bounds.Top,0,1,u));lower.Add(Edge(x,bounds.Bottom-1,0,-1,u));
            noise.Add(Noise(x,bounds.Top,0,1,u));noise.Add(Noise(x,bounds.Bottom-1,0,-1,u));
        }
        if(upper.Count<10)return e;
        e.Noise=Math.Max(.5,Median(noise));
        double threshold=Math.Max(1,e.Noise*2);
        var magnitudes=new List<double>();int paired=0;
        for(int i=0;i<upper.Count;i++) {
            double a=upper[i],b=lower[i];
            if(a*b>0 && Math.Min(Math.Abs(a),Math.Abs(b))>threshold)paired++;
            magnitudes.Add(a*b>0?Math.Min(Math.Abs(a),Math.Abs(b)):0);
        }
        e.PairedEdges=paired/(double)upper.Count;e.Signal=Median(magnitudes);
        int sideCount=0,sideHits=0;
        foreach(double fraction in new double[]{.35,.5,.65}) {
            double y=bounds.Top+bounds.Height*fraction;
            foreach(bool isLeft in new bool[]{true,false}) {
                double x=isLeft?bounds.Left:bounds.Right-1,dir=isLeft?1:-1;
                if(x-dir*14*u<1 || x-dir*14*u>=width-1)continue;
                double d=Edge(x,y,dir,0,u),n=Noise(x,y,dir,0,u);
                sideCount++;if(Math.Abs(d)>Math.Max(1.0,n*2))sideHits++;
            }
        }
        e.SideEdges=sideCount==0?0:sideHits/(double)sideCount;
        // Adjacent hover and selected rows can have exactly the same fill and
        // touch in the middle. The missing shared edge must then return to the
        // surrounding background at BOTH rounded corners, with side edges.
        if(e.PairedEdges<.65 && e.SideEdges>=.83) {
            foreach(bool isUpper in new bool[]{true,false}) {
                var intact=isUpper?upper:lower;
                double direction=Median(intact);
                double coverage=intact.Count(v=>v*direction>0&&Math.Abs(v)>threshold)/(double)intact.Count;
                double c1=CornerReturn(bounds,!isUpper,true,u),c2=CornerReturn(bounds,!isUpper,false,u);
                if(coverage>=.65 && c1*direction<0 && c2*direction<0
                    && Math.Min(Math.Abs(c1),Math.Abs(c2))>threshold) {
                    e.PairedEdges=coverage;
                    e.Signal=Math.Min(Math.Abs(direction),Math.Min(Math.Abs(c1),Math.Abs(c2)));
                }
            }
        }
        // Require coherent upper/lower edges across the row AND a vertical side.
        // Text, a smooth backdrop, and full-width video bands fail these tests.
        if(e.PairedEdges>=.65 && e.SideEdges>=.5 && e.Signal>threshold)
            e.Score=10*Math.Min(e.PairedEdges,Math.Min(1,e.SideEdges+.25))*Math.Min(1,e.Signal/(e.Noise*4+1));
        return e;
    }
    public List<Evidence> CompareRows(IEnumerable<Rectangle> bounds,double u) {
        var all=bounds.Select(b=>Measure(b,u)).ToList();
        var ordered=all.Select(e=>e.Signal).OrderBy(v=>v).ToArray();
        // Use the quieter half as peers: a short three-row list can contain
        // both selection and hover, so the median of all rows is contaminated.
        int quietCount=ordered.Length==0?0:Math.Max(1,ordered.Length/2);
        double baseline=quietCount==0?0:ordered[(quietCount-1)/2];
        double deviation=Median(ordered.Take(quietCount).Select(v=>Math.Abs(v-baseline)));
        foreach(var e in all) {
            e.PeerExcess=e.Signal-baseline;
            // An outlier must also pass the geometric tests; strongest row alone
            // is not evidence. Multiple hover/selection shapes remain candidates.
            if(all.Count>=3 && e.PeerExcess<=Math.Max(1,2*deviation))e.Score=0;
        }
        return all;
    }
}
