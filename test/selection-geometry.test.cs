using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

public static class SelectionGeometryTests {
    static int checks;
    static int rowPitch=42;
    static void Check(bool result,string message) {checks++;if(!result)throw new Exception(message);}
    static Rectangle Row(int i,double u) {return new Rectangle((int)(20*u),(int)((60+rowPitch*i)*u),(int)(240*u),(int)(30*u));}
    static Bitmap Frame(double u,int background,int highlight,bool gradient,bool texture,int selected,int hover=-1,bool stripe=false,bool textOnly=false,bool equalHover=false) {
        var b=new Bitmap((int)(320*u),(int)(340*u),PixelFormat.Format32bppArgb);
        var random=new Random(71);
        for(int y=0;y<b.Height;y++)for(int x=0;x<b.Width;x++) {
            int n=texture?random.Next(-2,3):0;
            int l=Math.Max(0,Math.Min(255,background+(gradient?(int)(x*.018/u+y*.045/u):0)+n));
            b.SetPixel(x,y,Color.FromArgb(l,l,l));
        }
        using(var g=Graphics.FromImage(b)) {
            g.SmoothingMode=SmoothingMode.AntiAlias;
            if(stripe)using(var brush=new SolidBrush(Color.FromArgb(highlight,highlight,highlight)))g.FillRectangle(brush,0,Row(2,u).Top,b.Width,Row(2,u).Height);
            for(int i=0;i<5;i++) {
                var r=Row(i,u);
                if((i==selected||i==hover)&&!textOnly) {
                    int value=i==hover&&!equalHover?(background+highlight)/2:highlight;
                    using(var brush=new SolidBrush(Color.FromArgb(value,value,value)))
                    using(var path=new GraphicsPath()) {
                        int d=(int)(12*u);
                        path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);
                        path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);path.CloseFigure();
                        g.FillPath(brush,path);
                    }
                }
                // Sparse text-like edges must not form a selection boundary.
                using(var pen=new Pen(background>120?Color.Black:Color.White,(float)Math.Max(1,u)))
                    for(int x=40;x<170;x+=9)g.DrawRectangle(pen,(float)(x*u),(float)((70+rowPitch*i)*u),(float)(4*u),(float)(8*u));
            }
        }
        return b;
    }
    static List<SelectionGeometry.Evidence> Measure(Bitmap b,double u) {return new SelectionGeometry(b).CompareRows(Enumerable.Range(0,5).Select(i=>Row(i,u)),u);}
    static void Synthetic() {
        foreach(double u in new double[]{1,1.5,2})
        foreach(bool gradient in new bool[]{false,true})
        foreach(bool texture in new bool[]{false,true})
        foreach(var pair in new int[][]{new int[]{230,205},new int[]{25,58},new int[]{120,95},new int[]{80,170}}) {
            using(var b=Frame(u,pair[0],pair[1],gradient,texture,2)) {
                var e=Measure(b,u);
                Check(e[2].Score>=5,"Missed selection: "+u+","+pair[0]+","+gradient+","+texture+" score="+e[2].Score);
                Check(e.Where((v,i)=>i!=2).All(v=>v.Score<5),"Unselected row accepted");
            }
        }
        foreach(bool gradient in new bool[]{false,true}) {
            using(var b=Frame(1.5,100,160,gradient,true,-1))Check(Measure(b,1.5).All(v=>v.Score<5),"Text/background accepted");
            using(var b=Frame(1.5,100,160,gradient,false,-1,-1,true))Check(Measure(b,1.5).All(v=>v.Score<5),"Video stripe accepted");
        }
        using(var b=Frame(1.5,200,150,false,false,2,3)) {
            var e=Measure(b,1.5);
            Check(e[2].Score>=5&&e[3].Score>=5,"Hover must remain a candidate for title disambiguation");
        }
        rowPitch=31;
        foreach(double u in new double[]{1,1.5,2})
        foreach(bool equal in new bool[]{false,true})
        using(var b=Frame(u,200,150,false,false,2,3,false,false,equal)) {
            var e=Measure(b,u);
            Check(e[2].Score>=5&&e[3].Score>=5,"Touching rounded hover row suppressed selection: "+u+","+equal);
            Check(e.Where((v,i)=>i!=2&&i!=3).All(v=>v.Score<5),"Touching shapes leaked to peers");
            var shortList=new SelectionGeometry(b).CompareRows(new Rectangle[]{Row(1,u),Row(2,u),Row(3,u)},u);
            Check(shortList[0].Score<5&&shortList[1].Score>=5&&shortList[2].Score>=5,"Short list hover polluted peer baseline");
        }
        rowPitch=42;
    }
    static Bitmap Transform(Bitmap original,int mode) {
        var b=new Bitmap(original.Width,original.Height,PixelFormat.Format32bppArgb);
        for(int y=0;y<b.Height;y++)for(int x=0;x<b.Width;x++) {
            Color c=original.GetPixel(x,y);int l=(c.R+c.G+c.B)/3;
            if(mode==0)b.SetPixel(x,y,Color.FromArgb(c.B,c.R,c.G)); // Arbitrary hue permutation.
            if(mode==1)b.SetPixel(x,y,Color.FromArgb(255-c.R,255-c.G,255-c.B));
            if(mode==2) {l=Math.Min(255,145+l/3);b.SetPixel(x,y,Color.FromArgb(l,l,l));}
            if(mode==3) {l=l/3;b.SetPixel(x,y,Color.FromArgb(l,l,l));}
        }
        return b;
    }
    static void Screenshot(string file,bool hovered=false) {
        using(var original=new Bitmap(file)) {
            // Captured UIA rectangles, not an inferred color mask.
            var rows=Enumerable.Range(0,7).Select(i=>new Rectangle(12,520+(int)Math.Floor(46.5*i),371,i%2==0?46:45)).ToList();
            for(int mode=-1;mode<4;mode++) {
                using(var b=mode<0?(Bitmap)original.Clone():Transform(original,mode)) {
                    var e=new SelectionGeometry(b).CompareRows(rows,1.5);
                    Check(e[0].Score>=5,"Screenshot transform missed selection: "+mode);
                    Check(e.Skip(hovered?2:1).All(v=>v.Score<5),"Screenshot transform accepted peer: "+mode);
                    Console.WriteLine("screenshot variant="+mode+" score="+e[0].Score+" paired="+e[0].PairedEdges+" side="+e[0].SideEdges);
                }
            }
        }
    }
    public static void Main(string[] args) {try {Synthetic();if(args.Length>0)Screenshot(args[0]);if(args.Length>1)Screenshot(args[1],true);Console.WriteLine("PASS "+checks+" geometry checks");}catch(Exception ex){Console.WriteLine("FAIL "+ex.Message);Environment.ExitCode=1;}}
}
