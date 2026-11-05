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
    [ComImport, Guid("905a0fef-bc53-11df-8c49-001e4fc686da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IBufferByteAccess { void Buffer(out IntPtr value); }
    static OcrEngine engine;
    static byte[] lastFingerprint;
    static IntPtr lastWindow;
    static int lastWidth,lastHeight,lastRight,lastLeft;
    static string lastTitle;
    static List<TextLine> lastRows;
    static DateTime lastFullRead;
    static int ocrPasses,cacheHits;
    static JavaScriptSerializer json = new JavaScriptSerializer();
    static object Missing(string reason) { return new { status="unavailable", reason=reason }; }
    static bool IsCodex(uint pid) {
        try {
            using(var process=Process.GetProcessById((int)pid)) {
              string p = process.MainModule.FileName;
              return p.IndexOf(@"\WindowsApps\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0
                && (Path.GetFileName(p).Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Equals("Codex.exe",StringComparison.OrdinalIgnoreCase));
            }
        } catch { return false; }
    }
    static double Luma(Color c) { return (c.R+c.G+c.B)/3.0; }
    static double Median(List<double> values) { values.Sort(); return values[values.Count/2]; }
    static double PixelBand(Bitmap b,int x,int y) {
        return (Luma(b.GetPixel(Math.Max(0,x-1),y))+Luma(b.GetPixel(x,y))+Luma(b.GetPixel(Math.Min(b.Width-1,x+1),y)))/3.0;
    }
    static int Divider(Bitmap b,int from,int to,double u) {
        double best=2.2; int found=-1;
        int step=Math.Max(3,(int)(7*u)), gap=Math.Max(2,(int)(4*u));
        for(int x=from; x<Math.Min(to,b.Width-gap-1);x++) {
            var values=new List<double>();
            for(int y=(int)(125*u);y<b.Height-(int)(100*u);y+=step) {
                double l=PixelBand(b,x-gap,y),r=PixelBand(b,x+gap,y),c=Luma(b.GetPixel(x,y));
                values.Add(Math.Max(Math.Abs(l-r),Math.Abs(c-(l+r)/2)));
            }
            if(values.Count<15) continue;
            double score=Median(values);
            if(score>best) { best=score; found=x; }
        }
        return found;
    }
    static double DividerScore(Bitmap b,int x,double u) {
        int gap=Math.Max(2,(int)(4*u));
        if(x<=gap||x>=b.Width-gap)return 0;
        var values=new List<double>();
        for(int y=(int)(125*u);y<b.Height-(int)(100*u);y+=Math.Max(3,(int)(7*u))) {
            double l=PixelBand(b,x-gap,y),r=PixelBand(b,x+gap,y),c=Luma(b.GetPixel(x,y));
            values.Add(Math.Max(Math.Abs(l-r),Math.Abs(c-(l+r)/2)));
        }
        return values.Count<15?0:Median(values);
    }
    static byte[] Fingerprint(Bitmap bitmap,Rectangle crop) {
        var data=bitmap.LockBits(crop,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        var bytes=new byte[crop.Width*crop.Height*4];
        try { for(int y=0;y<crop.Height;y++)Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),bytes,y*crop.Width*4,crop.Width*4); }
        finally { bitmap.UnlockBits(data); }
        using(var hash=SHA256.Create())return hash.ComputeHash(bytes);
    }
    static object Sample(IntPtr window,string heading,List<TextLine> selected,int right,double u,bool cached) {
        return new { status="sample",windowId=window.ToInt64().ToString(),observedAt=DateTime.UtcNow.ToString("o"),title=heading,selectedRows=selected,sidebarRight=right,dpi=u,cached=cached,ocrPasses=ocrPasses,cacheHits=cacheHits };
    }
    static List<object> ReadLines(Bitmap b,Rectangle crop,int ox,int oy,double preferredScale=2.0) {
        var result=new List<object>();
        double scale=Math.Min(preferredScale,OcrEngine.MaxImageDimension/(double)Math.Max(crop.Width,crop.Height));
        using(var part=new Bitmap((int)(crop.Width*scale),(int)(crop.Height*scale),PixelFormat.Format32bppArgb)) {
            using(var g=Graphics.FromImage(part)) {
                g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(b,new Rectangle(0,0,part.Width,part.Height),crop,GraphicsUnit.Pixel);
            }
            var data=part.LockBits(new Rectangle(0,0,part.Width,part.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            byte[] bytes=new byte[part.Width*part.Height*4];
            try { for(int y=0;y<part.Height;y++) Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),bytes,y*part.Width*4,part.Width*4); }
            finally { part.UnlockBits(data); }
            var buffer=new Windows.Storage.Streams.Buffer((uint)bytes.Length); buffer.Length=(uint)bytes.Length;
            IntPtr ptr; ((IBufferByteAccess)(object)buffer).Buffer(out ptr); Marshal.Copy(bytes,0,ptr,bytes.Length);
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
        }
        return result;
    }
    class TextLine { public string text; public double x,y,width,height; public double contrast; internal bool accessible; }
    static string ReadHeadingLabel(IntPtr window,RECT windowRect,Rectangle crop,TextLine line) {
        try {
            double x=windowRect.Left+(line==null?crop.Left+20:line.x+line.width/2);
            double y=windowRect.Top+(line==null?crop.Top+crop.Height/2:line.y+line.height/2);
            var element=line==null?null:AutomationElement.FromPoint(new System.Windows.Point(x,y));
            var labels=new HashSet<string>();
            for(int depth=0;element!=null && depth<8;depth++,element=TreeWalker.ControlViewWalker.GetParent(element)) {
                var current=element.Current;var bounds=current.BoundingRectangle;
                // Only accept the visible title text at the OCR-located heading.
                // Do not read the embedded monitor's heading.
                if(current.ControlType==ControlType.Text && !current.IsOffscreen && bounds.Contains(x,y)
                    && bounds.Left>=windowRect.Left+crop.Left && bounds.Right<=windowRect.Left+crop.Right
                    && bounds.Top>=windowRect.Top+crop.Top && bounds.Bottom<=windowRect.Top+crop.Bottom
                    && !String.IsNullOrWhiteSpace(current.Name))labels.Add(current.Name);
            }
            if(labels.Count==1)return labels.First();
            if(labels.Count>1)return null;
            // Chromium point hit testing and document names can be stale after a
            // failed provider resume. Read the actual header Text inside its own
            // toolbar instead. Do not trust full-window Document.Name as identity.
            var root=AutomationElement.FromHandle(window);
            var texts=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text));
            foreach(AutomationElement text in texts) {
                var current=text.Current;var bounds=current.BoundingRectangle;
                if(current.IsOffscreen || String.IsNullOrWhiteSpace(current.Name)
                    || bounds.Left<windowRect.Left+crop.Left || bounds.Right>windowRect.Left+crop.Right
                    || bounds.Top<windowRect.Top+crop.Top || bounds.Bottom>windowRect.Top+crop.Bottom)continue;
                var ancestor=TreeWalker.ControlViewWalker.GetParent(text);
                for(int depth=0;ancestor!=null && depth<6;depth++,ancestor=TreeWalker.ControlViewWalker.GetParent(ancestor)) {
                    var parent=ancestor.Current;var area=parent.BoundingRectangle;
                    if(parent.ControlType==ControlType.ToolBar && !parent.IsOffscreen
                        && area.Contains(bounds.TopLeft) && area.Contains(bounds.BottomRight)) {
                        labels.Add(current.Name);break;
                    }
                }
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
        if(window==IntPtr.Zero || !IsCodex(pid) || IsIconic(window))return Missing("Codex 未在前台，视觉识别暂停");
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
            int left=sameLayout&&right==lastRight?lastLeft:Divider(bitmap,(int)(35*u),Math.Min((int)(110*u),right-100),u);
            if(left<0)left=(int)(52*u);
            if(right-left<100*u)return Missing("会话侧栏过窄");
            // The final sidebar row extends into the old 32-DIP bottom margin.
            // Keep its full glyphs and selection band, even at the scroll limit.
            int top=(int)(100*u), bottom=height-(int)(8*u);
            var sidebar=new Rectangle(left+(int)(12*u),top,right-left-(int)(55*u),bottom-top);
            int hx=right+(int)(20*u),hy=(int)(40*u),hw=Math.Min((int)(550*u),captureWidth-hx-(int)(40*u)),hh=(int)(52*u);
            if(hw<100)return Missing("聊天标题区域不可见");
            var titleCrop=new Rectangle(hx,hy,hw,hh);
            var fingerprint=Fingerprint(bitmap,sidebar).Concat(Fingerprint(bitmap,titleCrop)).ToArray();
            if(GetForegroundWindow()!=window)return Missing("窗口切换中，等待下一次识别");
            if(sameLayout&&lastFingerprint!=null&&lastFingerprint.SequenceEqual(fingerprint)
                &&DateTime.UtcNow-lastFullRead<TimeSpan.FromSeconds(60)&&!String.IsNullOrWhiteSpace(lastTitle)) {
                cacheHits++;
                return Sample(window,lastTitle,lastRows,right,u,true);
            }
            ocrPasses++;
            var lines=ReadLines(bitmap,sidebar,sidebar.X,sidebar.Y).Cast<TextLine>().ToList();
            int sampleLeft=left+(int)(10*u),sampleRight=right-(int)(20*u);
            var baseLeft=new List<double>();var baseRight=new List<double>();
            for(int y=top;y<bottom;y+=Math.Max(3,(int)(5*u))) { baseLeft.Add(PixelBand(bitmap,sampleLeft,y));baseRight.Add(PixelBand(bitmap,sampleRight,y)); }
            double bgLeft=Median(baseLeft), bgRight=Median(baseRight);
            var selected=new List<TextLine>();
            foreach(var line in lines) {
                int y=Math.Min(height-1,(int)(line.y+line.height/2));
                double dl=PixelBand(bitmap,sampleLeft,y)-bgLeft, dr=PixelBand(bitmap,sampleRight,y)-bgRight;
                line.contrast=Math.Min(Math.Abs(dl),Math.Abs(dr));
                if(line.contrast>=5 && dl*dr>0)selected.Add(line);
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
            var headingLines=ReadLines(bitmap,titleCrop,hx,hy).Cast<TextLine>().ToList();
            var headingLine=headingLines.OrderBy(l=>l.x).FirstOrDefault();
            string heading=ReadHeadingLabel(window,r,titleCrop,headingLine)??(headingLine==null?"":headingLine.text);
            if(GetForegroundWindow()!=window)return Missing("窗口切换中，等待下一次识别");
            lastWindow=window;lastWidth=width;lastHeight=height;lastRight=right;lastLeft=left;
            lastFingerprint=fingerprint;lastTitle=heading;lastRows=selected;lastFullRead=DateTime.UtcNow;
            return Sample(window,heading,selected,right,u,false);
        }
    }
    [STAThread] public static void Main(string[] args) {
        Console.OutputEncoding=new UTF8Encoding(false);Console.InputEncoding=new UTF8Encoding(false);
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        try { engine=OcrEngine.TryCreateFromUserProfileLanguages(); if(engine==null)engine=OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN")); }catch { }
        if(engine==null) {Console.WriteLine(json.Serialize(Missing("Windows OCR 语言组件不可用")));return;}
        bool once=args.Length>0&&args[0]=="--once";
        do {
            if(!once && Console.ReadLine()==null)break;
            object result;
            try { result=Capture(); }catch { result=Missing("本地截图或 OCR 读取失败"); }
            Console.WriteLine(json.Serialize(result));Console.Out.Flush();
        }while(!once);
    }
}
