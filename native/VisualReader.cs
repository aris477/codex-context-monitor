using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// Read-only, local Windows capture/OCR. No images are written or transmitted.
// The helper only operates while the official Codex window is foreground.
public static class VisualReader {
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder path,ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",EntryPoint="RtlMoveMemory")] static extern void CopyMemory(IntPtr target,IntPtr source,UIntPtr bytes);
    [ComImport, Guid("905a0fef-bc53-11df-8c49-001e4fc686da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IBufferByteAccess { void Buffer(out IntPtr value); }
    static OcrEngine engine;
    static bool engineTried;
    static byte[] lastFingerprint;
    static byte[] lastPillFingerprint;
    static Rectangle lastPillCrop;
    static TextLine lastHeadingLine;
    static IntPtr lastWindow;
    static int lastWidth,lastHeight,lastRight,lastLeft;
    static string lastTitle;
    static bool lastTitleAccessible;
    static List<TextLine> lastRows;
    static DateTime lastFullRead;
    static int ocrPasses,cacheHits;
    static JavaScriptSerializer json = new JavaScriptSerializer();
    static object Missing(string reason) { return new { status="unavailable", reason=reason }; }
    static bool IsCodex(uint pid) {
        IntPtr process=OpenProcess(0x1000,false,pid);
        if(process==IntPtr.Zero)return false;
        try {
              var path=new StringBuilder(32768);int length=path.Capacity;
              if(!QueryFullProcessImageName(process,0,path,ref length))return false;
              string p=path.ToString();
              return p.IndexOf(@"\WindowsApps\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0
                && (Path.GetFileName(p).Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Equals("Codex.exe",StringComparison.OrdinalIgnoreCase));
        } finally { CloseHandle(process); }
    }
    static double Luma(Color c) { return (c.R+c.G+c.B)/3.0; }
    static double Median(List<double> values) { values.Sort(); return values[values.Count/2]; }
    static double PixelBand(Bitmap b,int x,int y) {
        return (Luma(b.GetPixel(Math.Max(0,x-1),y))+Luma(b.GetPixel(x,y))+Luma(b.GetPixel(Math.Min(b.Width-1,x+1),y)))/3.0;
    }
    static Color AvgColor(Bitmap b,int x,int y) {
        int r=0,g=0,bl=0,n=0;
        for(int dy=-1;dy<=1;dy++) {
            int py=Math.Max(0,Math.Min(b.Height-1,y+dy));
            for(int dx=-1;dx<=1;dx++) {
                int px=Math.Max(0,Math.Min(b.Width-1,x+dx));
                Color c=b.GetPixel(px,py);
                r+=c.R; g+=c.G; bl+=c.B; n++;
            }
        }
        return Color.FromArgb(r/n,g/n,bl/n);
    }
    static double ColorDiff(Color a,Color b) {
        double dr=a.R-b.R, dg=a.G-b.G, db=a.B-b.B;
        return Math.Sqrt((dr*dr+dg*dg+db*db)/3.0);
    }
    static double DividerScore(Bitmap b,int x,double u) {
        int gap=Math.Max(2,(int)(4*u));
        if(x<=gap||x>=b.Width-gap)return 0;
        var values=new List<double>();
        int pos=0,neg=0,line=0;
        for(int y=(int)(125*u);y<b.Height-(int)(100*u);y+=Math.Max(3,(int)(7*u))) {
            double l=PixelBand(b,x-gap,y),r=PixelBand(b,x+gap,y),c=Luma(b.GetPixel(x,y));
            double step=r-l, edge=Math.Abs(c-(l+r)/2);
            if(step>1.8)pos++; else if(step<-1.8)neg++;
            if(edge>1.8)line++;
            values.Add(Math.Max(Math.Abs(step),edge));
        }
        if(values.Count<15)return 0;
        double consistency=Math.Max(Math.Max(pos,neg),line)/(double)values.Count;
        return consistency>=0.65?Median(values):0;
    }
    static int Divider(Bitmap b,int from,int to,double u) {
        double best=2.2; int found=-1;
        for(int x=from; x<Math.Min(to,b.Width-1);x++) {
            double score=DividerScore(b,x,u);
            if(score>best) { best=score; found=x; }
        }
        return found;
    }
    static double RowScoreAtY(Bitmap b,int left,int right,int y,int outTopY,int outBotY,double u,out Color pillColor) {
        pillColor=Color.Empty;
        if(y<2||y>=b.Height-2)return 0;
        int gutLx=left+(int)(4*u), gutRx=right-(int)(11*u);
        int inLx=left+(int)(12*u);
        int[] inRxs=new int[] { right-(int)(24*u), right-(int)(32*u), right-(int)(42*u) };
        Color gutL=AvgColor(b,gutLx,y), gutR=AvgColor(b,gutRx,y);
        Color inL=AvgColor(b,inLx,y);
        double vertL=(outTopY>=2&&outBotY<b.Height-2)
            ?Math.Min(ColorDiff(inL,AvgColor(b,inLx,outTopY)),ColorDiff(inL,AvgColor(b,inLx,outBotY)))
            :0;
        double diffL=Math.Max(ColorDiff(inL,gutL),vertL);
        double best=0;
        foreach(int inRx in inRxs) {
            if(inRx<=inLx+(int)(20*u))continue;
            Color inR=AvgColor(b,inRx,y);
            if(ColorDiff(inL,inR)>12)continue;
            double vertR=(outTopY>=2&&outBotY<b.Height-2)
                ?Math.Min(ColorDiff(inR,AvgColor(b,inRx,outTopY)),ColorDiff(inR,AvgColor(b,inRx,outBotY)))
                :0;
            double diffR=Math.Max(ColorDiff(inR,gutR),vertR);
            double s=Math.Min(diffL,diffR);
            if(s>best) { best=s; pillColor=inL; }
        }
        return best;
    }
    static double PillContrast(Bitmap b,int left,int right,int yCenter,double u) {
        int d1=Math.Max(2,(int)(5*u)), d2=Math.Max(4,(int)(9*u));
        int outTopY=yCenter-(int)(19*u), outBotY=yCenter+(int)(19*u);
        Color cMid,cU1,cU2,cD1,cD2;
        double mid=RowScoreAtY(b,left,right,yCenter,outTopY,outBotY,u,out cMid);
        double u1=RowScoreAtY(b,left,right,yCenter-d1,outTopY,outBotY,u,out cU1);
        double u2=RowScoreAtY(b,left,right,yCenter-d2,outTopY,outBotY,u,out cU2);
        double d_1=RowScoreAtY(b,left,right,yCenter+d1,outTopY,outBotY,u,out cD1);
        double d_2=RowScoreAtY(b,left,right,yCenter+d2,outTopY,outBotY,u,out cD2);
        Color cUp=u1>=u2?cU1:cU2;
        Color cDown=d_1>=d_2?cD1:cD2;
        double up=Math.Max(u1,u2), down=Math.Max(d_1,d_2);
        if(up>=5&&down>=5&&ColorDiff(cUp,cDown)<=10)return Math.Min(up,down);
        if(mid>=5&&up>=5&&ColorDiff(cMid,cUp)<=10)return Math.Min(mid,up);
        if(mid>=5&&down>=5&&ColorDiff(cMid,cDown)<=10)return Math.Min(mid,down);
        return 0;
    }
    static List<TextLine> FindPillBands(Bitmap b,int left,int right,int top,int bottom,double u) {
        var bands=new List<TextLine>();
        int step=Math.Max(2,(int)(3*u));
        int bandStart=-1,bandEnd=-1;
        double bandPeak=0;
        for(int y=top;y<=bottom;y+=step) {
            double s=PillContrast(b,left,right,y,u);
            if(s>=5.0) {
                if(bandStart<0)bandStart=y;
                bandEnd=y;
                if(s>bandPeak)bandPeak=s;
            } else if(bandStart>=0) {
                if(bandEnd-bandStart>=(int)(6*u)&&bandEnd-bandStart<=(int)(42*u)) {
                    int cy=(bandStart+bandEnd)/2;
                    int h=Math.Max((int)(20*u),bandEnd-bandStart+(int)(8*u));
                    bands.Add(new TextLine { text="", x=left+(int)(12*u), y=cy-h/2, width=right-left-(int)(55*u), height=h, contrast=bandPeak });
                }
                bandStart=-1; bandPeak=0;
            }
        }
        if(bandStart>=0&&bandEnd-bandStart>=(int)(6*u)&&bandEnd-bandStart<=(int)(42*u)) {
            int cy=(bandStart+bandEnd)/2;
            int h=Math.Max((int)(20*u),bandEnd-bandStart+(int)(8*u));
            bands.Add(new TextLine { text="", x=left+(int)(12*u), y=cy-h/2, width=right-left-(int)(55*u), height=h, contrast=bandPeak });
        }
        return bands;
    }
    static byte[] Fingerprint(Bitmap bitmap,Rectangle crop) {
        var data=bitmap.LockBits(crop,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        var row=new byte[crop.Width*4];
        try {
            using(var hash=SHA256.Create()) {
                for(int y=0;y<crop.Height;y++) {
                    Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),row,0,row.Length);
                    hash.TransformBlock(row,0,row.Length,row,0);
                }
                hash.TransformFinalBlock(new byte[0],0,0);
                return hash.Hash;
            }
        }
        finally { bitmap.UnlockBits(data); }
    }
    static object Sample(IntPtr window,string heading,List<TextLine> selected,int right,double u,bool cached) {
        return new { status="sample",windowId=window.ToInt64().ToString(),observedAt=DateTime.UtcNow.ToString("o"),title=heading,titleSource=lastTitleAccessible?"windows-chat-toolbar":"ocr",selectedRows=selected,sidebarRight=right,dpi=u,cached=cached,ocrPasses=ocrPasses,cacheHits=cacheHits };
    }
    static void EnhanceForOcr(Bitmap part) {
        int tileW=Math.Max(32,part.Width/6);
        for(int tx=0;tx<part.Width;tx+=tileW) {
            int tw=Math.Min(tileW,part.Width-tx);
            int[] hist=new int[256];
            int count=0;
            for(int y=0;y<part.Height;y+=2) {
                for(int x=tx;x<tx+tw;x+=2) {
                    Color c=part.GetPixel(x,y);
                    hist[(c.R+c.G+c.B)/3]++; count++;
                }
            }
            int med=128,acc=0;
            for(int i=0;i<256;i++) { acc+=hist[i]; if(acc>=count/2) { med=i; break; } }
            if(med<170) {
                double floor=med+14;
                double span=Math.Max(35.0,232.0-floor);
                for(int y=0;y<part.Height;y++) {
                    for(int x=tx;x<tx+tw;x++) {
                        Color c=part.GetPixel(x,y);
                        double l=(c.R+c.G+c.B)/3.0;
                        int v=l<=floor?255:(int)Math.Max(0,Math.Min(255,255.0-(l-floor)*255.0/span));
                        part.SetPixel(x,y,Color.FromArgb(255,v,v,v));
                    }
                }
            }
        }
    }
    static List<object> ReadLinesFromPart(Bitmap part,double scale,int ox,int oy) {
        var result=new List<object>();
        var data=part.LockBits(new Rectangle(0,0,part.Width,part.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        uint length=checked((uint)(part.Width*part.Height*4));
        var buffer=new Windows.Storage.Streams.Buffer(length); buffer.Length=length;
        IntPtr ptr; ((IBufferByteAccess)(object)buffer).Buffer(out ptr);
        try { for(int y=0;y<part.Height;y++) CopyMemory(IntPtr.Add(ptr,y*part.Width*4),IntPtr.Add(data.Scan0,y*data.Stride),(UIntPtr)(uint)(part.Width*4)); }
        finally { part.UnlockBits(data); }
        using(var sb=SoftwareBitmap.CreateCopyFromBuffer(buffer,BitmapPixelFormat.Bgra8,part.Width,part.Height,BitmapAlphaMode.Ignore)) {
            var operation=engine.RecognizeAsync(sb);
            try {
              while(operation.Status==Windows.Foundation.AsyncStatus.Started)Thread.Sleep(5);
              var recognized=operation.GetResults();
              foreach(var line in recognized.Lines) {
                if(line.Words.Count==0)continue;
                double left=line.Words.Min(w=>w.BoundingRect.X), top=line.Words.Min(w=>w.BoundingRect.Y);
                double right=line.Words.Max(w=>w.BoundingRect.X+w.BoundingRect.Width), bottom=line.Words.Max(w=>w.BoundingRect.Y+w.BoundingRect.Height);
                result.Add(new TextLine { text=line.Text, x=left/scale+ox,y=top/scale+oy,width=(right-left)/scale,height=(bottom-top)/scale });
              }
            } finally { operation.Close(); }
        }
        return result;
    }
    static List<object> ReadLines(Bitmap b,Rectangle crop,int ox,int oy,double preferredScale=2.0) {
        double scale=Math.Min(preferredScale,OcrEngine.MaxImageDimension/(double)Math.Max(crop.Width,crop.Height));
        using(var part=new Bitmap((int)(crop.Width*scale),(int)(crop.Height*scale),PixelFormat.Format32bppArgb)) {
            using(var g=Graphics.FromImage(part)) {
                g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(b,new Rectangle(0,0,part.Width,part.Height),crop,GraphicsUnit.Pixel);
            }
            var result=ReadLinesFromPart(part,scale,ox,oy);
            if(result.Count==0) {
                EnhanceForOcr(part);
                result=ReadLinesFromPart(part,scale,ox,oy);
            }
            return result;
        }
    }
    class TextLine { public string text; public double x,y,width,height; public double contrast; internal bool accessible; }
    static bool InsideHeaderToolBar(AutomationElement text,System.Windows.Rect bounds) {
        var ancestor=TreeWalker.ControlViewWalker.GetParent(text);
        for(int depth=0;ancestor!=null && depth<8;depth++,ancestor=TreeWalker.ControlViewWalker.GetParent(ancestor)) {
            var parent=ancestor.Current;var area=parent.BoundingRectangle;
            if(parent.ControlType==ControlType.ToolBar && !parent.IsOffscreen
                && bounds.Left>=area.Left-4 && bounds.Left<=area.Right
                && bounds.Top>=area.Top-4 && bounds.Bottom<=area.Bottom+4)return true;
        }
        return false;
    }
    static bool IsHeaderTitle(AutomationElement element,IntPtr window,RECT windowRect,Rectangle crop) {
        var current=element.Current;var bounds=current.BoundingRectangle;
        if(current.IsOffscreen || String.IsNullOrWhiteSpace(current.Name)
            || bounds.Left<windowRect.Left+crop.Left-8 || bounds.Left>windowRect.Left+crop.Right
            || bounds.Top<windowRect.Top+crop.Top || bounds.Bottom>windowRect.Top+crop.Bottom
            || !InsideHeaderToolBar(element,bounds))return false;
        if(current.ControlType==ControlType.Text)return true;
        // Newer Codex exposes the title as the leading toolbar dropdown button.
        // Limit buttons to the title's left edge so toolbar actions cannot become identity.
        double u=Math.Max(1,GetDpiForWindow(window)/96.0);
        return current.ControlType==ControlType.Button && bounds.Left<=windowRect.Left+crop.Left+40*u;
    }
    static string ReadHeadingLabel(IntPtr window,RECT windowRect,Rectangle crop,TextLine line) {
        try {
            double x=windowRect.Left+(line==null?crop.Left+24:line.x+Math.Min(line.width/2,120));
            double y=windowRect.Top+(line==null?crop.Top+crop.Height/2:line.y+line.height/2);
            var element=AutomationElement.FromPoint(new System.Windows.Point(x,y));
            var labels=new HashSet<string>();
            for(int depth=0;element!=null && depth<8;depth++,element=TreeWalker.ControlViewWalker.GetParent(element)) {
                var current=element.Current;var bounds=current.BoundingRectangle;
                // Only accept visible title text or its dropdown inside the header ToolBar.
                // Do not read scrolled chat messages or the embedded monitor's heading.
                if(bounds.Contains(x,y) && IsHeaderTitle(element,window,windowRect,crop))labels.Add(current.Name);
            }
            if(labels.Count==1)return labels.First();
            if(labels.Count>1)return null;
            // Chromium point hit testing passes through pointer-events-none headers
            // into scrolled messages beneath. Read the title text/dropdown inside
            // its own toolbar instead. Do not trust full-window Document.Name as identity.
            var root=AutomationElement.FromHandle(window);
            var texts=root.FindAll(TreeScope.Descendants,new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text),
                new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button)));
            foreach(AutomationElement text in texts) {
                if(IsHeaderTitle(text,window,windowRect,crop))labels.Add(text.Current.Name);
            }
            if(labels.Count==1)return labels.First();
        } catch { /* Keep OCR as the fallback when accessibility is absent. */ }
        return null;
    }
    static void ReadSelectedLabels(IntPtr window,RECT windowRect,int sidebarRight,List<TextLine> rows) {
        if(rows.Count==0)return;
        try {
            foreach(var row in rows) {
                var labels=new HashSet<string>();
                double x=windowRect.Left+row.x+row.width/2,y=windowRect.Top+row.y+row.height/2;
                var button=AutomationElement.FromPoint(new System.Windows.Point(x,y));
                for(int depth=0;button!=null && depth<10;depth++,button=TreeWalker.ControlViewWalker.GetParent(button)) {
                    var current=button.Current;var bounds=current.BoundingRectangle;
                    if(current.ControlType==ControlType.Button && !current.IsOffscreen && bounds.Width>80 && bounds.Left>=windowRect.Left
                        && bounds.Right<=windowRect.Left+sidebarRight && bounds.Contains(x,y)
                        && !String.IsNullOrWhiteSpace(current.Name))labels.Add(current.Name);
                }
                // The pixel-selected row must intersect exactly one named button.
                if(labels.Count==1) {row.text=labels.First();row.accessible=true;}
            }
        } catch { /* Exact OCR remains the fallback when accessibility is absent. */ }
        if(rows.All(row=>row.accessible))return;
        try {
            // Chromium hit testing sometimes returns a full-window generic group
            // instead of the sidebar button, especially during title marquee.
            // Query buttons only on that failure, once per changed-frame OCR pass.
            var root=AutomationElement.FromHandle(window);
            var buttons=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
            foreach(var row in rows.Where(item=>!item.accessible)) {
                double x=windowRect.Left+row.x+row.width/2,y=windowRect.Top+row.y+row.height/2;
                var labels=new HashSet<string>();
                foreach(AutomationElement button in buttons) {
                    var current=button.Current;var bounds=current.BoundingRectangle;
                    if(!current.IsOffscreen && bounds.Width>80 && bounds.Left>=windowRect.Left
                        && bounds.Right<=windowRect.Left+sidebarRight && bounds.Contains(x,y)
                        && !String.IsNullOrWhiteSpace(current.Name))labels.Add(current.Name);
                }
                if(labels.Count==1) {row.text=labels.First();row.accessible=true;}
            }
        } catch { /* Retain exact OCR when the accessibility tree is unavailable. */ }
    }
    static object Capture() {
        IntPtr window=GetForegroundWindow(); uint pid; GetWindowThreadProcessId(window,out pid);
        if(window==IntPtr.Zero)return Missing("无法访问 Windows 前台桌面，请通过桌面快捷方式启动面板");
        if(!IsCodex(pid) || IsIconic(window))return Missing("Codex 未在前台，视觉识别暂停");
        if(!engineTried) {
            engineTried=true;
            try { engine=OcrEngine.TryCreateFromUserProfileLanguages(); if(engine==null)engine=OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN")); }catch { }
        }
        if(engine==null)return Missing("Windows OCR 语言组件不可用");
        RECT r;if(!GetWindowRect(window,out r))return Missing("无法读取 Codex 窗口");
        int width=r.Right-r.Left,height=r.Bottom-r.Top;
        if(width<500||height<400||width>12000||height>8000)return Missing("Codex 窗口尺寸不适合视觉识别");
        double u=Math.Max(1,GetDpiForWindow(window)/96.0);
        int captureWidth=Math.Min(width,(int)(1200*u));
        using(var bitmap=new Bitmap(captureWidth,height,PixelFormat.Format32bppArgb)) {
            using(var g=Graphics.FromImage(bitmap))g.CopyFromScreen(r.Left,r.Top,0,0,new Size(captureWidth,height),CopyPixelOperation.SourceCopy);
            if(GetForegroundWindow()!=window)return Missing("窗口切换中，等待下一次识别");
            bool sameLayout=lastWindow==window&&lastWidth==width&&lastHeight==height;
            int right=sameLayout&&DividerScore(bitmap,lastRight,u)>2.2?lastRight:Divider(bitmap,(int)(180*u),Math.Min((int)(600*u),(int)(width*.45)),u);
            if(right<0)return Missing("未识别到展开的会话侧栏");
            // The final sidebar row extends into the old 32-DIP bottom margin.
            // Keep its full glyphs and selection band, even at the scroll limit.
            int top=(int)(100*u), bottom=height-(int)(8*u);
            int left=sameLayout&&right==lastRight?lastLeft:(right>=(int)(265*u)?Divider(bitmap,(int)(48*u),(int)(58*u),u):-1);
            if(left<0||(left>0&&FindPillBands(bitmap,0,right,top,bottom,u).Count==1))left=0;
            if(right-left<100*u)return Missing("会话侧栏过窄");
            var sidebar=new Rectangle(left+(int)(12*u),top,right-left-(int)(55*u),bottom-top);
            int hx=right+(int)(6*u),hy=(int)(34*u),hw=Math.Min((int)(620*u),captureWidth-hx-(int)(20*u)),hh=(int)(62*u);
            if(hw<100)return Missing("聊天标题区域不可见");
            var titleCrop=new Rectangle(hx,hy,hw,hh);
            var fingerprint=Fingerprint(bitmap,sidebar).Concat(Fingerprint(bitmap,titleCrop)).ToArray();
            if(GetForegroundWindow()!=window)return Missing("窗口切换中，等待下一次识别");
            // An initial OCR fallback during navigation can include the dropdown
            // arrow. Never freeze that unverified title for the 60-second cache
            // period; re-read the live accessible toolbar name on the next sample.
            if(sameLayout&&lastTitleAccessible&&lastFingerprint!=null&&lastFingerprint.SequenceEqual(fingerprint)
                &&DateTime.UtcNow-lastFullRead<TimeSpan.FromSeconds(60)&&!String.IsNullOrWhiteSpace(lastTitle)) {
                string currentHeading=ReadHeadingLabel(window,r,titleCrop,lastHeadingLine);
                if(currentHeading==lastTitle) {
                    cacheHits++;
                    return Sample(window,lastTitle,lastRows,right,u,true);
                }
            }
            var bands=FindPillBands(bitmap,left,right,top,bottom,u);
            if(sameLayout&&lastRows!=null&&lastRows.Count==1&&bands.Count==1&&lastPillFingerprint!=null
                &&lastPillCrop.Width>0&&lastPillCrop.Height>0
                &&Math.Abs((bands[0].y+bands[0].height/2)-(lastPillCrop.Top+lastPillCrop.Height/2))<=4*u
                &&DateTime.UtcNow-lastFullRead<TimeSpan.FromSeconds(60)&&!String.IsNullOrWhiteSpace(lastTitle)) {
                var pillHash=Fingerprint(bitmap,lastPillCrop);
                if(pillHash.SequenceEqual(lastPillFingerprint)) {
                    string quickHeading=ReadHeadingLabel(window,r,titleCrop,lastHeadingLine);
                    if(quickHeading==lastTitle) {
                        lastTitleAccessible=true;
                        cacheHits++;
                        return Sample(window,lastTitle,lastRows,right,u,true);
                    }
                }
            }
            ocrPasses++;
            var lines=ReadLines(bitmap,sidebar,sidebar.X,sidebar.Y).Cast<TextLine>().ToList();
            var selected=new List<TextLine>();
            foreach(var line in lines) {
                int y=Math.Min(height-1,(int)(line.y+line.height/2));
                line.contrast=PillContrast(bitmap,left,right,y,u);
                if(line.contrast>=5)selected.Add(line);
            }
            foreach(var band in bands) {
                double bc=band.y+band.height/2;
                if(!selected.Any(s=>Math.Abs((s.y+s.height/2)-bc)<=14*u))selected.Add(band);
            }
            ReadSelectedLabels(window,r,right,selected);
            foreach(var line in selected) {
                if(!line.accessible) {
                    // A short row crop gives OCR more reliable glyph segmentation
                    // than recognizing the entire dense sidebar in one image.
                    int rowTop=Math.Max(top,(int)line.y-(int)(6*u));
                    int rowBottom=Math.Min(bottom,(int)(line.y+line.height+6*u));
                    var rowLines=ReadLines(bitmap,new Rectangle(left+(int)(12*u),rowTop,right-left-(int)(55*u),rowBottom-rowTop),left+(int)(12*u),rowTop,3.0).Cast<TextLine>().ToList();
                    if(rowLines.Count==1)line.text=rowLines[0].text;
                }
            }
            // Prefer the complete live title. OCR is only needed when the toolbar
            // is temporarily unavailable; failed accessibility is retried next sample.
            string accessibleHeading=ReadHeadingLabel(window,r,titleCrop,null);
            TextLine headingLine=null;
            if(accessibleHeading==null) {
                var headingLines=ReadLines(bitmap,titleCrop,hx,hy,2.5).Cast<TextLine>().ToList();
                headingLine=headingLines.OrderBy(l=>l.x).FirstOrDefault();
                accessibleHeading=ReadHeadingLabel(window,r,titleCrop,headingLine);
            }
            string heading=accessibleHeading??(headingLine==null?"":headingLine.text);
            if(GetForegroundWindow()!=window)return Missing("窗口切换中，等待下一次识别");
            lastWindow=window;lastWidth=width;lastHeight=height;lastRight=right;lastLeft=left;
            lastFingerprint=fingerprint;lastTitle=heading;lastTitleAccessible=accessibleHeading!=null;lastRows=selected;lastHeadingLine=headingLine;lastFullRead=DateTime.UtcNow;
            if(selected.Count==1) {
                int pTop=Math.Max(top,(int)selected[0].y), pBot=Math.Min(bottom,(int)(selected[0].y+selected[0].height));
                int pWidth=Math.Max(40,right-left-(int)(60*u));
                if(pBot-pTop>=8) {
                    lastPillCrop=new Rectangle(left+(int)(14*u),pTop,pWidth,pBot-pTop);
                    lastPillFingerprint=Fingerprint(bitmap,lastPillCrop);
                } else { lastPillFingerprint=null; }
            } else { lastPillFingerprint=null; }
            return Sample(window,heading,selected,right,u,false);
        }
    }
    [STAThread] public static void Main(string[] args) {
        Console.OutputEncoding=new UTF8Encoding(false);Console.InputEncoding=new UTF8Encoding(false);
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        bool once=args.Length>0&&args[0]=="--once";
        do {
            if(!once && Console.ReadLine()==null)break;
            object result;
            try { result=Capture(); }catch { result=Missing("本地截图或 OCR 读取失败"); }
            Console.WriteLine(json.Serialize(result));Console.Out.Flush();
        }while(!once);
    }
}
