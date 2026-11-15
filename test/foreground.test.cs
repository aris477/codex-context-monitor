using System;
using System.Collections.Generic;
public static class ForegroundTests {
    static int assertions;
    static void Check(bool value,string message) {assertions++;if(!value)throw new Exception(message);}
    sealed class Desktop : IForegroundDesktop {
        public IntPtr Active=(IntPtr)10;
        public int Delays;
        public bool Hidden,Iconic,Unavailable;
        public IntPtr Underlying;
        public Queue<IntPtr> Activations=new Queue<IntPtr>();
        public Dictionary<long,uint> Pids=new Dictionary<long,uint>{{10,1},{11,1},{12,1},{13,1},{20,2},{30,3},{40,1}};
        public IntPtr Foreground() {return Activations.Count>0?Activations.Dequeue():Active;}
        public IntPtr Root(IntPtr w) {return w==(IntPtr)11?(IntPtr)10:w;}
        public IntPtr OwnerRoot(IntPtr w) {return w==(IntPtr)12?(IntPtr)10:w;}
        public uint ProcessId(IntPtr w) {uint pid;return Pids.TryGetValue(w.ToInt64(),out pid)?pid:0;}
        public CodexProcessKind Identify(uint pid) {return Unavailable?CodexProcessKind.Unavailable:pid==1?CodexProcessKind.Official:CodexProcessKind.Other;}
        public bool Visible(IntPtr w) {return !Hidden;}
        public bool Minimized(IntPtr w) {return Iconic;}
        public IntPtr UnderSmallOverlay(IntPtr w) {return Underlying;}
        public void RetryDelay() {Delays++;if(Delays==1)Unavailable=false;}
    }
    static void Cases() {
        var d=new Desktop();var f=new CodexForeground(d);var main=f.Read();
        Check(main.CanCapture&&main.Window==(IntPtr)10,"Codex main window");
        d.Active=(IntPtr)11;Check(f.StillCurrent(main),"Child activation resolves to same root");
        d.Active=(IntPtr)13;Check(!f.StillCurrent(main),"A different Codex window must not reuse identity");
        d.Active=(IntPtr)40;Check(f.Read().CanCapture,"Detached Codex window can capture its own identity");
        d.Active=(IntPtr)12;var popup=f.Read();Check(popup.IsCodex&&!popup.CanCapture&&popup.Code=="codex-popup","Owned popup is Codex, not other app");
        Check(!f.StillCurrent(main),"Do not capture under a popup");
        d.Active=(IntPtr)20;Check(!f.Read().IsCodex&&f.Read().Code=="other-app","Another application immediately pauses");
        Check(d.Delays==0,"No grace period for another app");
        d.Active=(IntPtr)10;Check(f.Read().CanCapture,"Return to Codex recovers without relaunch");
        d.Activations.Enqueue(IntPtr.Zero);d.Activations.Enqueue((IntPtr)10);
        Check(f.Read().CanCapture&&d.Delays==1,"Transient null HWND retries");
        d.Activations.Enqueue(IntPtr.Zero);d.Activations.Enqueue((IntPtr)20);
        Check(f.Read().Code=="other-app","Null followed by other app never reuses old identity");
        d.Unavailable=true;Check(f.Read().Code=="process-unavailable","Persistent query failure has its own reason");
        d.Unavailable=false;d.Active=IntPtr.Zero;Check(f.Read().Code=="no-foreground","No desktop never guesses");
        d.Active=(IntPtr)10;d.Hidden=true;Check(!f.Read().CanCapture,"Hidden main window pauses");
        d.Hidden=false;d.Iconic=true;Check(f.Read().Code=="window-hidden","Minimized reason");
        d=new Desktop {Unavailable=true};f=new CodexForeground(d);
        Check(f.Read().CanCapture&&d.Delays==1,"Transient process query failure retries");
        string family="OpenAI.Codex_2p2nqsd0c76g0";
        Check(WindowsForegroundDesktop.OfficialIdentity(family,null),"Package identity independent of image path");
        Check(WindowsForegroundDesktop.OfficialIdentity(null,@"D:\WindowsApps\OpenAI.Codex_99.1_x64__2p2nqsd0c76g0\app\ChatGPT.exe"),"WindowsApps fallback handles install drive/version");
        Check(WindowsForegroundDesktop.OfficialIdentity(null,@"C:\Program Files\WindowsApps\OpenAI.Codex_26_x64__2p2nqsd0c76g0\app\Codex.exe"),"Executable rename supported");
        foreach(string path in new string[]{@"C:\ChatGPT.exe",@"C:\WindowsApps\OpenAI.ChatGPT_1_x64__2p2nqsd0c76g0\app\ChatGPT.exe",@"C:\WindowsApps\OpenAI.Codex_1_x64__fake\app\ChatGPT.exe",@"C:\WindowsApps\OpenAI.Codex_1_x64__2p2nqsd0c76g0\app\resources\codex.exe"})
            Check(!WindowsForegroundDesktop.OfficialIdentity(null,path),"Unrelated process rejected");
        d=new Desktop();f=new CodexForeground(d);main=f.Read();d.Active=(IntPtr)30;
        Check(!f.StillCurrent(main),"Actual other app clears sampling identity");
        d.Active=(IntPtr)30;d.Underlying=(IntPtr)10;
        Check(f.Read().Code=="ready-under-overlay"&&f.StillCurrent(main),"Small floating overlay preserves the actual underlying Codex window");
        d.Underlying=(IntPtr)20;Check(f.Read().Code=="other-app","Overlay above another app never selects background Codex");
        OverlayCases();
    }
    static WindowsForegroundDesktop.WindowInfo Window(int id,int width,int height,long style=0,long extended=0) {
        return new WindowsForegroundDesktop.WindowInfo {Handle=(IntPtr)id,Width=width,Height=height,Style=style,ExtendedStyle=extended,Visible=true};
    }
    static void OverlayCases() {
        var pet=Window(30,468,660,0x14000000,0x280028);
        var codex=Window(10,2560,1528,0x15070000,0x240100);
        var browser=Window(20,2560,1528,0x17CF0000,0x200100);
        var top=Window(40,540,192,0x16080000,0x80008);
        Check(WindowsForegroundDesktop.SelectUnderOverlay(pet.Handle,new[]{pet,top,codex})==codex.Handle,"Real pet and secondary floating overlay");
        Check(WindowsForegroundDesktop.SelectUnderOverlay(pet.Handle,new[]{pet,browser,codex})==browser.Handle,"Stop at browser immediately beneath overlay");
        var regular=Window(30,468,660,0x14CF0000,0x40000);
        Check(WindowsForegroundDesktop.SelectUnderOverlay(regular.Handle,new[]{regular,codex})==IntPtr.Zero,"Normal small app never treated as overlay");
        var large=Window(30,2560,1528,0x14000000,0x280028);
        Check(WindowsForegroundDesktop.SelectUnderOverlay(large.Handle,new[]{large,codex})==IntPtr.Zero,"Fullscreen overlay pauses");
        var distant=Window(30,468,660,0x14000000,0x280028);distant.Left=3000;
        Check(WindowsForegroundDesktop.SelectUnderOverlay(distant.Handle,new[]{distant,codex})==IntPtr.Zero,"Overlay on another monitor cannot promote Codex");
        var cloaked=Window(20,2560,1528);cloaked.Cloaked=true;
        Check(WindowsForegroundDesktop.SelectUnderOverlay(pet.Handle,new[]{pet,cloaked,codex})==codex.Handle,"Ignore cloaked windows");
        Check(WindowsForegroundDesktop.SelectUnderOverlay((IntPtr)99,new[]{pet,codex})==IntPtr.Zero,"Missing overlay identity never guesses");
    }
    public static void Main(string[] args) {
        try {Cases();Console.WriteLine("PASS "+assertions+" foreground assertions");}
        catch(Exception e){Console.WriteLine("FAIL "+e.Message);Environment.ExitCode=1;}
    }
}
