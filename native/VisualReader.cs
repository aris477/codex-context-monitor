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
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("kernel32.dll",EntryPoint="RtlMoveMemory")] static extern void CopyMemory(IntPtr target,IntPtr source,UIntPtr bytes);
    [ComImport, Guid("905a0fef-bc53-11df-8c49-001e4fc686da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IBufferByteAccess { void Buffer(out IntPtr value); }
    static OcrEngine engine;
    static bool engineTried;
    static int ocrPasses;
    static bool diagnostics;
    static JavaScriptSerializer json = new JavaScriptSerializer();
    static readonly CodexForeground foreground=new CodexForeground(new WindowsForegroundDesktop());
    static string foregroundState;
    static object Missing(string reason) { return new { status="unavailable", reason=reason,foregroundState=foregroundState }; }
    static double Luma(Color c) { return (c.R+c.G+c.B)/3.0; }
    static double Median(List<double> values) { values.Sort(); return values[values.Count/2]; }
    static double PixelBand(Bitmap b,int x,int y) {
        return (Luma(b.GetPixel(Math.Max(0,x-1),y))+Luma(b.GetPixel(x,y))+Luma(b.GetPixel(Math.Min(b.Width-1,x+1),y)))/3.0;
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
    class TextLine { public string text; public double x,y,width,height; public double contrast,pairedEdges,sideEdges,peerExcess; internal bool accessible; }
    static List<TextLine> DistinctPaintedRows(List<TextLine> rows,double u) {
        var distinct=new List<TextLine>();
        foreach(var row in rows) {
            // OCR can split one label into multiple lines, each producing the
            // same highlighted shape a pixel apart. Merge only observations of
            // that SAME shape, never same-named rows at different positions.
            bool duplicate=distinct.Any(other=>row.text==other.text
                && Math.Abs(row.x-other.x)<=2*u && Math.Abs(row.y-other.y)<=2*u
                && Math.Abs(row.width-other.width)<=2*u && Math.Abs(row.height-other.height)<=2*u);
            if(!duplicate)distinct.Add(row);
        }
        return distinct;
    }
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
    sealed class SidebarLayout {
        public int Left,Right;
        public string Source;
        public List<TextLine> Rows=new List<TextLine>();
    }
    static SidebarLayout ReadSidebarLayout(IntPtr window,RECT windowRect,double u) {
        try {
            var root=AutomationElement.FromHandle(window);
            var items=root.FindAll(TreeScope.Descendants,new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ToolBar),
                new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem)));
            var toolbars=new List<System.Windows.Rect>();
            foreach(AutomationElement item in items) {
                var c=item.Current;var b=c.BoundingRectangle;
                if(c.IsOffscreen || c.ControlType!=ControlType.ToolBar)continue;
                if(b.Left>=windowRect.Left+160*u && b.Left<windowRect.Left+(windowRect.Right-windowRect.Left)*.48
                    && b.Top>=windowRect.Top+20*u && b.Bottom<=windowRect.Top+100*u && b.Width>240*u)toolbars.Add(b);
            }
            if(toolbars.Count!=1)return null;
            var layout=new SidebarLayout {Right=(int)(toolbars[0].Left-windowRect.Left-6*u),Source="windows-control-bounds"};
            // Current Codex exposes the full painted chat row as a ListItem;
            // its inner title button is narrower and supports ExpandCollapse
            // instead of Invoke. Measure the row, not that inner button.
            foreach(AutomationElement item in items) {
                var c=item.Current;var b=c.BoundingRectangle;
                if(c.IsOffscreen || c.ControlType!=ControlType.ListItem || String.IsNullOrWhiteSpace(c.Name)
                    || b.Left<windowRect.Left || b.Right>windowRect.Left+layout.Right
                    || b.Width<110*u || b.Height<18*u || b.Height>44*u
                    || b.Top<windowRect.Top+100*u || b.Bottom>windowRect.Bottom-8*u)continue;
                var parent=TreeWalker.ControlViewWalker.GetParent(item);
                if(parent==null || parent.Current.ControlType!=ControlType.List)continue;
                var labels=item.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
                bool namedChat=false;
                foreach(AutomationElement label in labels) {
                    var lc=label.Current;var lb=lc.BoundingRectangle;
                    if(!lc.IsOffscreen && lc.Name==c.Name && b.Contains(lb)
                        && label.GetSupportedPatterns().Contains(ScrollItemPattern.Pattern)) {namedChat=true;break;}
                }
                if(namedChat)layout.Rows.Add(new TextLine {text=c.Name,x=b.Left-windowRect.Left,y=b.Top-windowRect.Top,width=b.Width,height=b.Height,accessible=true});
            }
            if(layout.Rows.Count>0)layout.Source="windows-listitem-bounds";
            foreach(AutomationElement item in items) {
                var c=item.Current;var b=c.BoundingRectangle;
                if(c.IsOffscreen || c.ControlType!=ControlType.Button || String.IsNullOrWhiteSpace(c.Name)
                    || b.Left<windowRect.Left || b.Right>windowRect.Left+layout.Right
                    || b.Width<110*u || b.Height<18*u || b.Height>44*u
                    || b.Top<windowRect.Top+100*u || b.Bottom>windowRect.Bottom-8*u)continue;
                var patterns=item.GetSupportedPatterns();
                if(!patterns.Contains(InvokePattern.Pattern)||!patterns.Contains(ScrollItemPattern.Pattern)
                    || patterns.Contains(ExpandCollapsePattern.Pattern)||patterns.Contains(ScrollPattern.Pattern))continue;
                // A renderer may expose both models. Do not count a title
                // button again when its full ListItem row was already found.
                if(layout.Rows.Any(row=>new System.Windows.Rect(windowRect.Left+row.x,windowRect.Top+row.y,row.width,row.height).Contains(b)))continue;
                layout.Rows.Add(new TextLine {text=c.Name,x=b.Left-windowRect.Left,y=b.Top-windowRect.Top,width=b.Width,height=b.Height,accessible=true});
            }
            if(layout.Rows.Count>0)layout.Left=Math.Max(0,(int)layout.Rows.Min(row=>row.x)-(int)(8*u));
            return layout.Right-layout.Left>=100*u?layout:null;
        }catch{return null;}
    }
    static object Capture() {
        var active=foreground.Read();foregroundState=active.Code;
        if(!active.CanCapture)return Missing(active.Reason);
        IntPtr window=active.Window;
        RECT r;if(!GetWindowRect(window,out r))return Missing("无法读取 Codex 窗口");
        int width=r.Right-r.Left,height=r.Bottom-r.Top;
        if(width<500||height<400||width>12000||height>8000)return Missing("Codex 窗口尺寸不适合视觉识别");
        double u=Math.Max(1,GetDpiForWindow(window)/96.0);
        var layout=ReadSidebarLayout(window,r,u);
        for(int retry=0;(layout==null || layout.Rows.Count==0) && retry<2;retry++) {
            Thread.Sleep(40);
            if(!foreground.StillCurrent(active))return Missing("前台窗口已切换，等待下一次识别");
            layout=ReadSidebarLayout(window,r,u);
        }
        int captureWidth=Math.Min(width,(int)(1200*u));
        using(var bitmap=new Bitmap(captureWidth,height,PixelFormat.Format32bppArgb)) {
            using(var g=Graphics.FromImage(bitmap))g.CopyFromScreen(r.Left,r.Top,0,0,new Size(captureWidth,height),CopyPixelOperation.SourceCopy);
            if(!foreground.StillCurrent(active))return Missing("前台窗口已切换，等待下一次识别");
            int top=(int)(100*u),bottom=height-(int)(8*u);
            if(layout==null) {
                int divider=Divider(bitmap,(int)(180*u),Math.Min((int)(600*u),(int)(width*.45)),u);
                if(divider<0)return Missing("未识别到展开的会话侧栏");
                layout=new SidebarLayout {Right=divider,Left=0,Source="grayscale-divider-fallback"};
            }
            int right=layout.Right,left=layout.Left;
            int hx=right+(int)(6*u),hy=(int)(34*u),hw=Math.Min((int)(620*u),captureWidth-hx-(int)(20*u)),hh=(int)(62*u);
            if(hw<100)return Missing("聊天标题区域不可见");
            var titleCrop=new Rectangle(hx,hy,hw,hh);
            string accessibleHeading=ReadHeadingLabel(window,r,titleCrop,null);
            var geometry=new SelectionGeometry(bitmap);
            if(layout.Rows.Count==0) {
                if(!EnsureOcr())return Missing("侧栏无障碍布局与 Windows OCR 均不可用");
                var sidebar=new Rectangle(left+(int)(12*u),top,right-left-(int)(30*u),bottom-top);
                var lines=ReadLines(bitmap,sidebar,sidebar.X,sidebar.Y).Cast<TextLine>().ToList();ocrPasses++;
                foreach(var line in lines) {
                    double cy=line.y+line.height/2;SelectionGeometry.Evidence best=null;
                    foreach(int dipHeight in new int[]{28,30,32,34}) {
                        int h=(int)(dipHeight*u);
                        var e=geometry.Measure(new Rectangle(left+(int)(8*u),(int)(cy-h/2),right-left-(int)(26*u),h),u);
                        if(best==null||e.Score>best.Score)best=e;
                    }
                    if(best!=null&&best.Score>=5) {line.x=best.Bounds.Left;line.y=best.Bounds.Top;line.width=best.Bounds.Width;line.height=best.Bounds.Height;line.contrast=best.Score;layout.Rows.Add(line);}
                }
            } else {
                var evidence=geometry.CompareRows(layout.Rows.Select(row=>new Rectangle((int)row.x,(int)row.y,(int)row.width,(int)row.height)),u);
                for(int i=0;i<layout.Rows.Count;i++) {
                    layout.Rows[i].contrast=evidence[i].Score;
                    layout.Rows[i].pairedEdges=evidence[i].PairedEdges;layout.Rows[i].sideEdges=evidence[i].SideEdges;
                    layout.Rows[i].peerExcess=evidence[i].PeerExcess;
                }
            }
            var selected=layout.Rows.Where(row=>row.contrast>=5).ToList();
            ReadSelectedLabels(window,r,right,selected);
            selected=DistinctPaintedRows(selected,u);
            TextLine headingLine=null;
            if(accessibleHeading==null) {
                if(!EnsureOcr())return Missing("Windows OCR 语言组件不可用");
                var headingLines=ReadLines(bitmap,titleCrop,hx,hy,2.5).Cast<TextLine>().ToList();ocrPasses++;
                headingLine=headingLines.OrderBy(line=>line.x).FirstOrDefault();
                accessibleHeading=ReadHeadingLabel(window,r,titleCrop,headingLine);
            }
            string heading=accessibleHeading??(headingLine==null?"":headingLine.text);
            if(!foreground.StillCurrent(active))return Missing("前台窗口已切换，等待下一次识别");
            if(accessibleHeading!=null) {
                string recheckedHeading=ReadHeadingLabel(window,r,titleCrop,null);
                // Chromium may briefly omit the title while its accessibility
                // tree is being updated. Retry only a missing result, never a
                // different title, and accept only a fresh matching read.
                for(int retry=0;recheckedHeading==null && retry<2;retry++) {
                    Thread.Sleep(40);
                    if(!foreground.StillCurrent(active))return Missing("前台窗口已切换，等待下一次识别");
                    recheckedHeading=ReadHeadingLabel(window,r,titleCrop,null);
                }
                if(recheckedHeading!=accessibleHeading) {
                    if(diagnostics)return new {status="unavailable",reason="聊天切换中，等待标题与选中行稳定",foregroundState=foregroundState,
                        firstTitle=accessibleHeading,recheckedTitle=recheckedHeading};
                    return Missing("聊天切换中，等待标题与选中行稳定");
                }
            }
            return new {status="sample",windowId=window.ToInt64().ToString(),observedAt=DateTime.UtcNow.ToString("o"),
                title=heading,titleSource=accessibleHeading!=null?"windows-chat-toolbar":"ocr",selectedRows=selected,foregroundState=foregroundState,
                titleRowVisible=layout.Rows.Any(row=>row.accessible&&row.text==heading),rowDiagnostics=diagnostics?layout.Rows:null,
                selectionMethod="relative-edge-geometry",layoutSource=layout.Source,sidebarRight=right,dpi=u,cached=false,ocrPasses=ocrPasses};
        }
    }
    static bool EnsureOcr() {
        if(!engineTried) {
            engineTried=true;
            try {engine=OcrEngine.TryCreateFromUserProfileLanguages();if(engine==null)engine=OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"));}catch{}
        }
        return engine!=null;
    }
    [STAThread] public static void Main(string[] args) {
        Console.OutputEncoding=new UTF8Encoding(false);Console.InputEncoding=new UTF8Encoding(false);
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        if(args.Contains("--foreground")) {
            var state=foreground.Read();
            Console.WriteLine(json.Serialize(new {state.Code,state.Reason,state.IsCodex,state.CanCapture,
                windowId=state.Window.ToInt64().ToString(),foregroundWindow=state.ForegroundWindow.ToInt64().ToString()}));
            return;
        }
        bool once=args.Length>0&&args[0]=="--once";
        diagnostics=args.Contains("--diagnostics");
        do {
            if(!once && Console.ReadLine()==null)break;
            object result;
            try { result=Capture(); }catch { result=Missing("本地截图或 OCR 读取失败"); }
            Console.WriteLine(json.Serialize(result));Console.Out.Flush();
        }while(!once);
    }
}
