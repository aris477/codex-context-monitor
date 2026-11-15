using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public enum CodexProcessKind { Other, Official, Unavailable }
public interface IForegroundDesktop {
    IntPtr Foreground();
    IntPtr Root(IntPtr window);
    IntPtr OwnerRoot(IntPtr window);
    uint ProcessId(IntPtr window);
    CodexProcessKind Identify(uint pid);
    bool Visible(IntPtr window);
    bool Minimized(IntPtr window);
    IntPtr UnderSmallOverlay(IntPtr window);
    void RetryDelay();
}
public sealed class CodexForegroundState {
    public IntPtr Window,ForegroundWindow;
    public uint ProcessId;
    public string Code,Reason;
    public bool IsCodex,CanCapture;
}
public sealed class CodexForeground {
    readonly IForegroundDesktop desktop;
    public CodexForeground(IForegroundDesktop desktop) {this.desktop=desktop;}
    CodexForegroundState Once() {
        IntPtr raw=desktop.Foreground();
        if(raw==IntPtr.Zero)return new CodexForegroundState {Code="no-foreground",Reason="暂时无法读取 Windows 前台窗口，等待桌面恢复"};
        IntPtr root=desktop.Root(raw);if(root==IntPtr.Zero)root=raw;
        uint pid=desktop.ProcessId(root);
        var kind=desktop.Identify(pid);
        if(kind==CodexProcessKind.Unavailable)return new CodexForegroundState {ForegroundWindow=raw,Code="process-unavailable",Reason="前台进程信息暂时无法读取，正在重试"};
        // Never infer foreground from a background Codex process, window title,
        // last successful sample, or a visually similar window.
        bool overlay=false;
        if(kind!=CodexProcessKind.Official) {
            IntPtr underlying=desktop.UnderSmallOverlay(root);
            if(underlying==IntPtr.Zero || desktop.Identify(desktop.ProcessId(underlying))!=CodexProcessKind.Official)
                return new CodexForegroundState {ForegroundWindow=raw,Code="other-app",Reason="当前前台属于其他应用，Codex 识别暂停"};
            root=underlying;pid=desktop.ProcessId(root);overlay=true;
        }
        IntPtr owner=desktop.OwnerRoot(root);
        if(owner!=IntPtr.Zero && owner!=root && desktop.Identify(desktop.ProcessId(owner))==CodexProcessKind.Official) {
            return new CodexForegroundState {Window=owner,ForegroundWindow=raw,ProcessId=desktop.ProcessId(owner),
                IsCodex=true,Code="codex-popup",Reason="Codex 菜单或弹窗在前台，关闭后自动恢复聊天识别"};
        }
        if(!desktop.Visible(root)||desktop.Minimized(root))return new CodexForegroundState {Window=root,ForegroundWindow=raw,ProcessId=pid,
            IsCodex=true,Code="window-hidden",Reason="Codex 窗口已最小化或不可见"};
        return new CodexForegroundState {Window=root,ForegroundWindow=raw,ProcessId=pid,IsCodex=true,CanCapture=true,Code=overlay?"ready-under-overlay":"ready",Reason=""};
    }
    public CodexForegroundState Read() {
        var state=Once();
        // Windows may return no HWND, or deny the first process query, during
        // activation. Retry only that uncertainty; another app pauses at once.
        for(int i=0;i<2 && (state.Code=="no-foreground"||state.Code=="process-unavailable");i++) {
            desktop.RetryDelay();state=Once();
        }
        return state;
    }
    public bool StillCurrent(CodexForegroundState previous) {
        var current=Read();
        return current.CanCapture && current.Window==previous.Window && current.ProcessId==previous.ProcessId;
    }
}
public sealed class WindowsForegroundDesktop : IForegroundDesktop {
    public sealed class WindowInfo {
        public IntPtr Handle;
        public long Style,ExtendedStyle;
        public int Left,Top,Width,Height;
        public bool Visible,Minimized,Cloaked;
    }
    [StructLayout(LayoutKind.Sequential)] struct RECT {public int Left,Top,Right,Bottom;}
    delegate bool EnumWindowsCallback(IntPtr window,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsCallback callback,IntPtr data);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out RECT bounds);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder path,ref int length);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr process,ref uint length,StringBuilder family);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr process);
    public IntPtr Foreground() {return GetForegroundWindow();}
    public IntPtr Root(IntPtr window) {return GetAncestor(window,2);}
    public IntPtr OwnerRoot(IntPtr window) {return GetAncestor(window,3);}
    public uint ProcessId(IntPtr window) {uint pid;GetWindowThreadProcessId(window,out pid);return pid;}
    public bool Visible(IntPtr window) {return IsWindowVisible(window);}
    public bool Minimized(IntPtr window) {return IsIconic(window);}
    static bool OverlayStyle(WindowInfo w) {
        return (w.ExtendedStyle&8)!=0 && (w.ExtendedStyle&0x40000)==0
            && ((w.ExtendedStyle&0x80)!=0 || ((w.ExtendedStyle&0x80000)!=0 && (w.Style&0xC00000)==0));
    }
    static bool Usable(WindowInfo w) {return w.Visible&&!w.Minimized&&!w.Cloaked&&w.Width>=40&&w.Height>=30;}
    public static IntPtr SelectUnderOverlay(IntPtr foreground,WindowInfo[] windows) {
        int start=Array.FindIndex(windows,w=>w.Handle==foreground);
        if(start<0 || !Usable(windows[start]) || !OverlayStyle(windows[start]))return IntPtr.Zero;
        var overlays=new List<WindowInfo> {windows[start]};
        for(int i=start+1;i<windows.Length;i++) {
            var candidate=windows[i];
            if(!Usable(candidate))continue;
            if(OverlayStyle(candidate)) {overlays.Add(candidate);continue;}
            // Only the FIRST ordinary visible window underneath may qualify.
            // Never scan past a browser/dialog looking for a background Codex.
            if(candidate.Width<500||candidate.Height<400)return IntPtr.Zero;
            foreach(var overlay in overlays) {
                long area=(long)overlay.Width*overlay.Height,total=(long)candidate.Width*candidate.Height;
                if(area>total/4 || overlay.Width>=candidate.Width || overlay.Height>=candidate.Height
                    || overlay.Left>=candidate.Left+candidate.Width || overlay.Top>=candidate.Top+candidate.Height
                    || overlay.Left+overlay.Width<=candidate.Left || overlay.Top+overlay.Height<=candidate.Top)
                    return IntPtr.Zero;
            }
            return candidate.Handle;
        }
        return IntPtr.Zero;
    }
    public IntPtr UnderSmallOverlay(IntPtr window) {
        var windows=new List<WindowInfo>();
        EnumWindows(delegate(IntPtr handle,IntPtr unused) {
            if(!IsWindowVisible(handle)||IsIconic(handle))return true;
            RECT rect;if(!GetWindowRect(handle,out rect))return true;
            int cloaked=0;
            try {DwmGetWindowAttribute(handle,14,out cloaked,4);}catch(EntryPointNotFoundException){}
            windows.Add(new WindowInfo {Handle=handle,Left=rect.Left,Top=rect.Top,Width=rect.Right-rect.Left,Height=rect.Bottom-rect.Top,
                Visible=true,Cloaked=cloaked!=0,Style=GetWindowLongPtr(handle,-16).ToInt64(),ExtendedStyle=GetWindowLongPtr(handle,-20).ToInt64()});
            return true;
        },IntPtr.Zero);
        return SelectUnderOverlay(window,windows.ToArray());
    }
    public void RetryDelay() {Thread.Sleep(40);}
    public static bool OfficialIdentity(string family,string image) {
        if(String.Equals(family,"OpenAI.Codex_2p2nqsd0c76g0",StringComparison.OrdinalIgnoreCase))return true;
        if(String.IsNullOrWhiteSpace(image))return false;
        // Package identity normally works regardless of install drive/version.
        // Retain the known WindowsApps path only for unavailable package APIs.
        string normalized=image.Replace('/','\\');
        int marker=normalized.IndexOf(@"\WindowsApps\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase);
        if(marker<0)return false;
        int end=normalized.IndexOf('\\',marker+13);
        if(end<0)return false;
        string package=normalized.Substring(marker+13,end-marker-13);
        if(!package.EndsWith("_2p2nqsd0c76g0",StringComparison.OrdinalIgnoreCase))return false;
        string relative=normalized.Substring(end+1);
        return relative.Equals(@"app\ChatGPT.exe",StringComparison.OrdinalIgnoreCase)
            || relative.Equals(@"app\Codex.exe",StringComparison.OrdinalIgnoreCase);
    }
    public CodexProcessKind Identify(uint pid) {
        if(pid==0)return CodexProcessKind.Unavailable;
        IntPtr process=OpenProcess(0x1000,false,pid);
        if(process==IntPtr.Zero)return CodexProcessKind.Unavailable;
        try {
            uint size=0;string family=null;
            try {
                if(GetPackageFamilyName(process,ref size,null)==122 && size>0 && size<4096) {
                    var name=new StringBuilder((int)size);
                    if(GetPackageFamilyName(process,ref size,name)==0)family=name.ToString();
                }
            }catch(EntryPointNotFoundException){}
            if(OfficialIdentity(family,null))return CodexProcessKind.Official;
            var path=new StringBuilder(32768);int length=path.Capacity;
            if(!QueryFullProcessImageName(process,0,path,ref length))return CodexProcessKind.Unavailable;
            return OfficialIdentity(family,path.ToString())?CodexProcessKind.Official:CodexProcessKind.Other;
        } finally {CloseHandle(process);}
    }
}
