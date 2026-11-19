using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace GrokPet;

public static class Program {
    public static readonly string DataDir=Path.Combine(new DirectoryInfo(AppContext.BaseDirectory).Name.Equals("bin",StringComparison.OrdinalIgnoreCase)?Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName:AppContext.BaseDirectory,"data");
    public static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals};
    public static Settings Config=new();
    public static bool Verifying;
    public static bool UiTesting;
    public static bool SuppressChat;
    public static bool VerifyOffline;
    public static int ExitCode;
    public static string VerificationDir=Path.Combine(DataDir,"verification");
    [STAThread] public static int Main(string[] args) {
        using var mutex=new Mutex(true,"Local\\GrokPet.Desktop.v1",out bool first);
        if(!first){try{using var signal=EventWaitHandle.OpenExisting("Local\\GrokPet.OpenControl");signal.Set();}catch{}return 0;}
        using var openSignal=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\GrokPet.OpenControl");
        Directory.CreateDirectory(DataDir);
        try{if(File.Exists(Path.Combine(DataDir,"settings.json")))Config=JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(DataDir,"settings.json")),JsonOptions)??new();}catch{}
        try{Config.Normalize();}catch{Config=new();Config.Normalize();}
        Verifying=args.Contains("--verify");
        VerifyOffline=args.Contains("--offline");
        int verifyIndex=Array.IndexOf(args,"--verify-dir");if(verifyIndex>=0&&verifyIndex+1<args.Length)VerificationDir=Path.GetFullPath(args[verifyIndex+1]);
        UiTesting=args.Contains("--ui-test");
        SuppressChat=args.Contains("--no-chat");
        var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
        app.DispatcherUnhandledException+=(_,e)=>{Log("error",e.Exception.Message);e.Handled=true;};
        var window=new PetWindow();var signalTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(300)};signalTimer.Tick+=(_,_)=>{if(openSignal.WaitOne(0))window.OpenControl();};signalTimer.Start();app.Run(window);return ExitCode;
    }
    public static void Save(){try{Config.Normalize();var path=Path.Combine(DataDir,"settings.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(Config,JsonOptions));File.Move(path+".tmp",path,true);}catch(Exception e){Log("save",e.GetType().Name);throw;}}
    public static void Log(string kind,string info) {
        try{var p=Path.Combine(DataDir,"status.log");if(File.Exists(p)&&new FileInfo(p).Length>200_000)File.WriteAllText(p,"");File.AppendAllText(p,$"{DateTime.Now:O} [{kind}] {info}\n");}catch{}
    }
}
public class PetWindow:Window {
    public const double CX=220,CY=280;
    readonly Brain brain=new();
    ControlCenter? controlCenter;
    public event Action<string,string,double>? ReplyReceived;
    public bool IsThinking=>busy;
    public double LastReplySeconds{get;private set;}
    public string LastError{get;private set;}="";
    public Brain Service=>brain;
    readonly Grid root=new(){Background=null};
    readonly PetDrawing drawing=new();
    readonly Border bubble;readonly TextBlock words;
    readonly TextBlock thinking=new(){Text="· · ·",FontSize=20,FontFamily=new("Segoe UI"),Foreground=new SolidColorBrush(Color.FromRgb(172,175,186)),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,Margin=new(0,178,0,0),Opacity=0,IsHitTestVisible=false};
    readonly Stopwatch clock=Stopwatch.StartNew();
    double last,wx,wy,vx,vy,goalX,goalY,yaw,pitch=10,lidL=1,lidR=1,dx,dy,stretch=1,tilt;
    double releaseAt=-100,actionAt=-100,bubbleUntil,holdUntil;
    bool pressed,dragging,busy,closing,shown,contextOpen;
    Point pressPoint;double pressX,pressY,totalDistance,peakSpeed;Point lastDragPoint;double lastDragTime;
    string? pending;int clickCount;readonly CancellationTokenSource lifetime=new();
    List<MotorFrame> frames=[];MotorFrame origin=new();
    public PetWindow() {
        Title="Grok 桌宠";Width=440;Height=390;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        AllowsTransparency=true;Background=Brushes.Transparent;Topmost=true;ShowInTaskbar=Program.UiTesting;ShowActivated=false;
        UseLayoutRounding=false;Content=root;
        root.Children.Add(drawing);
        words=new(){FontFamily=new("Microsoft YaHei UI"),FontSize=14,LineHeight=23,Foreground=new SolidColorBrush(Color.FromRgb(39,42,50)),TextWrapping=TextWrapping.Wrap,MaxHeight=116};
        bubble=new(){Child=words,Background=new SolidColorBrush(Color.FromArgb(248,255,255,255)),BorderBrush=new SolidColorBrush(Color.FromArgb(110,210,214,225)),BorderThickness=new(1),CornerRadius=new(19),Padding=new(19,13,19,13),Width=310,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,Margin=new(0,70,0,0),Opacity=0,IsHitTestVisible=false,RenderTransformOrigin=new(.5,1),RenderTransform=new TranslateTransform(0,8)};
        root.Children.Add(bubble);root.Children.Add(thinking);
        var area=SystemParameters.WorkArea;
        wx=double.IsFinite(Program.Config.X)?Program.Config.X:area.Right-385;wy=double.IsFinite(Program.Config.Y)?Program.Config.Y:area.Bottom-385;
        (wx,wy)=ClampPosition(wx,wy);goalX=wx;goalY=wy;Left=wx;Top=wy;
        drawing.MouseLeftButtonDown+=Press;drawing.MouseMove+=MovePointer;drawing.MouseLeftButtonUp+=Release;
        drawing.LostMouseCapture+=(_,_)=>{if(pressed){pressed=false;EndDrag();}};
        drawing.MouseRightButtonUp+=(_,e)=>{OpenMenu();e.Handled=true;};
        Loaded+=async(_,_)=>{shown=true;CompositionTarget.Rendering+=Tick;ApplySettings(Program.Config,false);Program.Log("start","desktop companion ready");if(Program.Verifying)_=Verify();else{if(Program.Config.ShowPanelOnStart)OpenControl();try{bool ready=await Bootstrap.EnsureService();if(ready&&!Program.SuppressChat&&Program.Config.OpenChatOnLaunch&&!closing)Bootstrap.OpenChat();}catch{Program.Log("startup","service launch failed");}}};
        SourceInitialized+=(_,_)=>{var h=new WindowInteropHelper(this).Handle;if(!Program.UiTesting){long ex=GetWindowLong(h,-20);SetWindowLong(h,-20,ex|0x08000000|0x00000080);}HwndSource.FromHwnd(h)?.AddHook(Hook);};
        Closed+=(_,_)=>{closing=true;lifetime.Cancel();CompositionTarget.Rendering-=Tick;Program.Config.X=Left;Program.Config.Y=Top;Program.Save();};
    }
    IntPtr Hook(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled) {
        if(msg==0x0021&&!Program.UiTesting){handled=true;return new IntPtr(3);} // MA_NOACTIVATE: touching the pet never steals typing focus.
        if(msg==0x0084&&!pressed&&!contextOpen){var p=CursorLocal();if(!drawing.Hit(p)){handled=true;return new IntPtr(-1);}}
        return IntPtr.Zero;
    }
    Point CursorScreen(){GetCursorPos(out var p);var m=PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice??Matrix.Identity;return m.Transform(new Point(p.X,p.Y));}
    Point CursorLocal(){var p=CursorScreen();return new(p.X-Left,p.Y-Top);}
    (double,double) ClampPosition(double x,double y) {
        // Keep the orb, rather than the transparent window margins, inside the desktop work area.
        var center=new Point(x+CX,y+CY);var matrix=PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice??Matrix.Identity;var physical=matrix.Transform(center);
        var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        var handle=MonitorFromPoint(new NativePoint{X=(int)physical.X,Y=(int)physical.Y},2);
        Rect area=SystemParameters.WorkArea;
        if(GetMonitorInfo(handle,ref info)){var inverse=matrix;inverse.Invert();var a=inverse.Transform(new Point(info.Work.Left,info.Work.Top));var b=inverse.Transform(new Point(info.Work.Right,info.Work.Bottom));area=new(a,b);}
        double r=Program.Config.Size*109/256+12;
        return(Math.Clamp(x,area.Left+r-CX,area.Right-r-CX),Math.Clamp(y,area.Top+Math.Max(180,r)-CY,area.Bottom-r-CY));
    }
    void Press(object sender,MouseButtonEventArgs e){if(e.ChangedButton!=MouseButton.Left)return;pressed=true;dragging=false;totalDistance=0;peakSpeed=0;pressPoint=CursorScreen();lastDragPoint=pressPoint;lastDragTime=clock.Elapsed.TotalSeconds;pressX=wx;pressY=wy;drawing.CaptureMouse();e.Handled=true;}
    void MovePointer(object sender,MouseEventArgs e){if(!pressed)return;var p=CursorScreen();var delta=p-pressPoint;if(!dragging&&delta.Length>4){dragging=true;frames.Clear();HideBubble();}if(dragging){goalX=pressX+delta.X;goalY=pressY+delta.Y;double t=clock.Elapsed.TotalSeconds;double step=Math.Max(.004,t-lastDragTime);var movement=p-lastDragPoint;double speed=movement.Length/step;vx=Blend(vx,movement.X/step,.42);vy=Blend(vy,movement.Y/step,.42);peakSpeed=Math.Max(peakSpeed,speed);totalDistance+=movement.Length;lastDragPoint=p;lastDragTime=t;Left=wx=goalX;Top=wy=goalY;}e.Handled=true;}
    void Release(object sender,MouseButtonEventArgs e){if(!pressed)return;pressed=false;drawing.ReleaseMouseCapture();if(dragging)EndDrag();else if(Program.Config.ReactToClick){clickCount++;_=Interact($"用户轻点你一下，这是第{clickCount}次。用你自己的表情回应。");}e.Handled=true;}
    void EndDrag(){if(!dragging)return;dragging=false;vx*=.04;vy*=.04;releaseAt=clock.Elapsed.TotalSeconds;(goalX,goalY)=ClampPosition(goalX,goalY);Program.Config.X=goalX;Program.Config.Y=goalY;Program.Save();if(Program.Config.ReactToDrag)_=Interact($"用户刚把你拖到新位置，拖动约{totalDistance:F0}像素，速度{(peakSpeed>900?"很快":peakSpeed>350?"轻快":"缓慢")}。回应被拖动的感受。");}
    public async Task Interact(string interaction) {
        if(closing)return;
        if(busy){pending=interaction;return;}
        busy=true;thinking.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(220)));Program.Log("request","interaction received");
        var timer=Stopwatch.StartNew();
        try {
            var reply=await brain.React(interaction,lifetime.Token);if(closing)return;
            frames=reply.frames;origin=new(){yaw=yaw,pitch=pitch,left=lidL,right=lidR,dx=dx,dy=dy,stretch=stretch,tilt=tilt};actionAt=clock.Elapsed.TotalSeconds;
            holdUntil=actionAt+frames.Sum(f=>f.ms)/1000.0+.6;
            LastReplySeconds=timer.Elapsed.TotalSeconds;LastError="";ShowBubble(reply.say,Program.Config.BubbleSeconds);ReplyReceived?.Invoke(interaction,reply.say,LastReplySeconds);
            File.WriteAllText(Path.Combine(Program.DataDir,"last-response.json"),JsonSerializer.Serialize(reply,Program.JsonOptions));
            Program.Log("reply",$"{timer.Elapsed.TotalSeconds:F2}s / {reply.frames.Count} AI keyframes");
        }catch(OperationCanceledException){if(!closing){LastError="等待模型响应超时。";ShowBubble("连接超时，请重试。",6);ReplyReceived?.Invoke(interaction,"连接超时，请检查设置。",0);}}
        catch(Exception e){LastError=e.Message;Program.Log("connection",e.GetType().Name);ShowBubble("连接失败，请检查设置。",7);ReplyReceived?.Invoke(interaction,e.Message,0);}
        finally{busy=false;thinking.BeginAnimation(OpacityProperty,new DoubleAnimation(0,TimeSpan.FromMilliseconds(240)));if(pending is string next&&!closing){pending=null;_=Interact(next);}}
    }
    void ShowBubble(string text,double seconds){words.Text=text;bubble.Margin=new(0);bubble.Measure(new Size(310,180));bubble.Margin=new(0,Math.Max(12,CY-Program.Config.Size*109/256-bubble.DesiredSize.Height-24),0,0);bubbleUntil=clock.Elapsed.TotalSeconds+seconds;bubble.BeginAnimation(OpacityProperty,new DoubleAnimation(bubble.Opacity,1,TimeSpan.FromMilliseconds(310)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});((TranslateTransform)bubble.RenderTransform).BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(8,0,TimeSpan.FromMilliseconds(360)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});}
    void HideBubble(){bubbleUntil=0;bubble.BeginAnimation(OpacityProperty,new DoubleAnimation(0,TimeSpan.FromMilliseconds(350)));}
    static double Blend(double a,double b,double t)=>a+(b-a)*t;
    static double Ease(double t)=>t*t*(3-2*t);
    void Tick(object? sender,EventArgs e) {
        if(!shown||closing)return;double now=clock.Elapsed.TotalSeconds,dt=Math.Clamp(now-last,.001,.032);last=now;
        // Stable sub-stepped damped spring: frame-rate independent and no nested DragMove loop.
        int count=(int)Math.Ceiling(dt/.008);double h=dt/count;
        if(dragging){wx=goalX;wy=goalY;vx*=Math.Exp(-dt*4);vy*=Math.Exp(-dt*4);}
        else for(int i=0;i<count;i++){vx+=(220*(goalX-wx)-23*vx)*h;vy+=(220*(goalY-wy)-23*vy)*h;wx+=vx*h;wy+=vy*h;}
        if(Math.Abs(wx-Left)>.01)Left=wx;if(Math.Abs(wy-Top)>.01)Top=wy;
        var pointer=CursorLocal();double targetYaw=Program.Config.EyeTracking?Math.Clamp(Math.Atan2(pointer.X-CX,270)*180/Math.PI,-67,67)*Program.Config.GazeRange:0;double targetPitch=Program.Config.EyeTracking?10-Math.Clamp(Math.Atan2(pointer.Y-CY,330)*180/Math.PI,-29,29)*Program.Config.GazeRange:10;
        var target=new MotorFrame{yaw=targetYaw,pitch=targetPitch};
        bool action=frames.Count>0&&now<holdUntil&&!dragging;
        if(action){double elapsed=(now-actionAt)*1000;MotorFrame a=origin;foreach(var b in frames){if(elapsed<=b.ms){double t=Ease(Math.Clamp(elapsed/b.ms,0,1));target=new(){yaw=Blend(a.yaw,b.yaw,t),pitch=Blend(a.pitch,b.pitch,t),left=Blend(a.left,b.left,t),right=Blend(a.right,b.right,t),dx=Blend(a.dx,b.dx,t),dy=Blend(a.dy,b.dy,t),stretch=Blend(a.stretch,b.stretch,t),tilt=Blend(a.tilt,b.tilt,t)};break;}elapsed-=b.ms;a=b;target=b;}}
        else if(frames.Count>0&&!dragging)frames.Clear();
        double follow=1-Math.Exp(-dt*13);yaw=Blend(yaw,target.yaw,follow);pitch=Blend(pitch,target.pitch,follow);lidL=Blend(lidL,target.left,follow);lidR=Blend(lidR,target.right,follow);dx=Blend(dx,target.dx,follow);dy=Blend(dy,target.dy,follow);stretch=Blend(stretch,target.stretch,follow);tilt=Blend(tilt,target.tilt,follow);
        double speed=Math.Sqrt(vx*vx+vy*vy),elasticity=Program.Config.ReducedMotion?0:Program.Config.Elasticity,pull=Math.Clamp(speed/1700,0,.13)*elasticity;double rebound=now-releaseAt<1?Math.Sin((now-releaseAt)*19)*Math.Exp(-(now-releaseAt)*6)*.07*elasticity:0;
        double motion=Program.Config.ReducedMotion?0:Program.Config.Motion;
        double renderStretch=1+(stretch-1)*motion+pull+rebound,renderTilt=tilt*motion+Math.Clamp(-vx*.008,-9,9)*elasticity;
        bool changed=Math.Abs(drawing.Yaw-yaw)>.012||Math.Abs(drawing.Pitch-pitch)>.012||Math.Abs(drawing.LeftEye-lidL)>.002||Math.Abs(drawing.RightEye-lidR)>.002||Math.Abs(drawing.DX-dx*motion)>.012||Math.Abs(drawing.DY-dy*motion)>.012||Math.Abs(drawing.Stretch-renderStretch)>.001||Math.Abs(drawing.Tilt-renderTilt)>.015||drawing.Size!=Program.Config.Size;
        if(changed){drawing.Yaw=yaw;drawing.Pitch=pitch;drawing.LeftEye=lidL;drawing.RightEye=lidR;drawing.DX=dx*motion;drawing.DY=dy*motion;drawing.Stretch=renderStretch;drawing.Tilt=renderTilt;drawing.Size=Program.Config.Size;drawing.InvalidateVisual();}
        if(bubbleUntil>0&&now>bubbleUntil)HideBubble();
    }
    void OpenMenu() {
        var menu=new ContextMenu{FontFamily=new("Microsoft YaHei UI"),FontSize=13};
        void Add(string label,Action action){var item=new MenuItem{Header=label};item.Click+=(_,_)=>action();menu.Items.Add(item);}
        Add("打开控制中心…",()=>OpenControl());Add("和 Grok 说句话…",()=>OpenChat());Add("模型连接…",()=>OpenControl("模型连接"));
        menu.Items.Add(new Separator());
        Add("小一点",()=>{Program.Config.Size=Math.Max(96,Program.Config.Size-16);Program.Save();});Add("大一点",()=>{Program.Config.Size=Math.Min(216,Program.Config.Size+16);Program.Save();});
        Add("回到屏幕右下角",()=>{var a=SystemParameters.WorkArea;(goalX,goalY)=ClampPosition(a.Right-385,a.Bottom-385);Program.Config.X=goalX;Program.Config.Y=goalY;Program.Save();});
        Add("打开本地聊天网页",()=>OpenLocalChat());
        menu.Items.Add(new Separator());Add("退出桌宠",()=>Close());
        contextOpen=true;menu.Closed+=(_,_)=>contextOpen=false;menu.IsOpen=true;
    }
    public void OpenControl(string page="概览"){if(controlCenter==null){controlCenter=new ControlCenter(this);controlCenter.Closed+=(_,_)=>controlCenter=null;}controlCenter.Show();if(controlCenter.WindowState==WindowState.Minimized)controlCenter.WindowState=WindowState.Normal;controlCenter.SelectPage(page);controlCenter.Activate();}
    public void ApplySettings(Settings settings,bool save=true){settings.Normalize();if(Program.Config.ActiveConnectionId!=settings.ActiveConnectionId)brain.ClearMemory();Program.Config=settings;Topmost=settings.AlwaysOnTop;Opacity=settings.Opacity;words.FontSize=settings.BubbleFontSize;if(save)Program.Save();drawing.InvalidateVisual();}
    public void ResetPosition(){var a=SystemParameters.WorkArea;(goalX,goalY)=ClampPosition(a.Right-385,a.Bottom-385);Program.Config.X=goalX;Program.Config.Y=goalY;Program.Save();}
    public void Preview(){frames=[new(){yaw=-20,pitch=15,left=1,right=.03,dy=-8,tilt=-8,ms=450},new(){yaw=20,pitch=12,left=.3,right=.3,dy=0,tilt=6,ms=500},new(){yaw=0,pitch=10,left=1,right=1,ms=450}];origin=new(){yaw=yaw,pitch=pitch,left=lidL,right=lidR};actionAt=clock.Elapsed.TotalSeconds;holdUntil=actionAt+2.5;ShowBubble("动作预览 · 不调用 AI",4);}
    public void OpenLocalChat(){var p=Program.Config.Active;if(Uri.TryCreate(p.Endpoint,UriKind.Absolute,out var uri)&&uri.IsLoopback){string url=uri.GetLeftPart(UriPartial.Authority);Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}else OpenControl("模型连接");}
    Window Dialog(string title,double height){return new Window{Title=title,Width=390,Height=height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterScreen,Topmost=true,Background=new SolidColorBrush(Color.FromRgb(247,248,251)),FontFamily=new("Microsoft YaHei UI"),FontSize=14};}
    void OpenChat() {
        var dialog=Dialog("和 Grok 说句话",238);var panel=new StackPanel{Margin=new(22)};
        panel.Children.Add(new TextBlock{Text="我在。你想说什么？",Margin=new(0,0,0,12),FontWeight=FontWeights.SemiBold});
        var input=new TextBox{Height=74,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,Padding=new(10),MaxLength=500};panel.Children.Add(input);
        var send=new Button{Content="告诉 Grok",Height=34,Margin=new(0,12,0,0),IsDefault=false};send.Click+=(_,_)=>{if(string.IsNullOrWhiteSpace(input.Text))return;string text=input.Text.Trim();dialog.Close();_=Interact("用户对你说："+text);};panel.Children.Add(send);
        input.KeyDown+=(_,e)=>{if(e.Key==Key.Enter&&Keyboard.Modifiers==ModifierKeys.Control){send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));e.Handled=true;}};
        dialog.Content=panel;dialog.Loaded+=(_,_)=>input.Focus();dialog.ShowDialog();
    }
    async Task Verify() {
        string testRoot=Program.VerificationDir;Directory.CreateDirectory(testRoot);
        var checks=new List<object>();
        try{
            checks.Add(await Verification.CompatibleApi());
            var control=new ControlCenter(this);
            foreach(string page in new[]{"概览","模型连接","外观与动作","性格与对话","诊断与数据","关于"}){
                control.SelectPage(page);var visual=(FrameworkElement)control.Content;visual.Measure(new Size(1040,720));visual.Arrange(new Rect(0,0,1040,720));visual.UpdateLayout();
                var bitmap=new RenderTargetBitmap(1040,720,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(testRoot,"panel-"+page+".png"));encoder.Save(output);
            }
            control.Close();checks.Add(new{name="all_control_pages_rendered",ok=true,pages=6});
            foreach(int side in new[]{-1,1})foreach(double y in new[]{-78d,-40,0,40,78,180})foreach(double p in new[]{-25d,10,40})foreach(double lid in new[]{.025,1d}){var points=SphereEyes.Outline(y,p,lid,side);if(points.Any(v=>!double.IsFinite(v.X)||!double.IsFinite(v.Y)||(v-new Point(128,128)).Length>109.001))throw new Exception("Sphere geometry escaped silhouette");}
            checks.Add(new{name="sphere_projection",ok=true});
            await Task.Delay(300);RenderPreview(Path.Combine(testRoot,"idle.png"));
            if(!Program.VerifyOffline)foreach(string ev in new[]{"用户轻点你一下。","用户把你轻轻拖到了桌面右侧。","用户说：今天写代码写累了，鼓励我一句。"}){var sw=Stopwatch.StartNew();var r=await brain.React(ev,lifetime.Token);checks.Add(new{name="live_model",input=ev,seconds=sw.Elapsed.TotalSeconds,reply=r});frames=r.frames;origin=new();actionAt=clock.Elapsed.TotalSeconds;holdUntil=actionAt+4;ShowBubble(r.say,8);await Task.Delay(650);RenderPreview(Path.Combine(testRoot,$"reaction-{checks.Count-1}.png"));}
            int countBefore=brain.RequestCount;await Task.Delay(1200);if(brain.RequestCount!=countBefore)throw new Exception("Unexpected model request while idle");checks.Add(new{name="idle_no_model_requests",ok=true});
        }catch(Exception e){Program.ExitCode=1;checks.Add(new{name="failure",error=e.ToString()});}
        File.WriteAllText(Path.Combine(testRoot,"results.json"),JsonSerializer.Serialize(checks,Program.JsonOptions));Close();
    }
    void RenderPreview(string path){root.Measure(new(Width,Height));root.Arrange(new(0,0,Width,Height));root.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Width*2,(int)Height*2,192,192,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);png.Save(file);}
    [StructLayout(LayoutKind.Sequential)]struct NativePoint{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]struct MonitorInfo{public int Size;public NativeRect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")]static extern bool GetCursorPos(out NativePoint p);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]static extern long GetWindowLong(IntPtr h,int n);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]static extern long SetWindowLong(IntPtr h,int n,long value);
    [DllImport("user32.dll")]static extern IntPtr MonitorFromPoint(NativePoint p,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)]static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
}
public class PetDrawing:FrameworkElement {
    public double Yaw,Pitch=10,LeftEye=1,RightEye=1,DX,DY,Stretch=1,Tilt,Size=144;
    static readonly Brush Sphere=MakeSphere(),Ink=new SolidColorBrush(Color.FromRgb(21,21,23));
    static Brush MakeSphere(){var b=new RadialGradientBrush{Center=new(.38,.27),GradientOrigin=new(.35,.22),RadiusX=.8,RadiusY=.8};b.GradientStops.Add(new(Colors.White,0));b.GradientStops.Add(new(Color.FromRgb(253,254,255),.53));b.GradientStops.Add(new(Color.FromRgb(220,226,235),1));b.Freeze();return b;}
    public bool Hit(Point p)=>Math.Pow((p.X-PetWindow.CX-DX)/(Size*109/256+8),2)+Math.Pow((p.Y-PetWindow.CY-DY)/(Size*109/256+8),2)<=1;
    protected override HitTestResult? HitTestCore(PointHitTestParameters p)=>Hit(p.HitPoint)?new PointHitTestResult(this,p.HitPoint):null;
    protected override void OnRender(DrawingContext dc) {
        base.OnRender(dc);double x=PetWindow.CX+DX,y=PetWindow.CY+DY,s=Size/256;
        // Hand-rendered soft shadow stays inside this tiny transparent window.
        for(int i=9;i>=1;i--)dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(2+(9-i)/3),13,19,32)),null,new(x+2,y+Size*.40+i*.5),Size*.36+i*1.4,Size*.045+i*1.1);
        dc.PushTransform(new TranslateTransform(x,y));dc.PushTransform(new RotateTransform(Tilt));dc.PushTransform(new ScaleTransform(s/Math.Sqrt(Math.Max(.6,Stretch)),s*Stretch));dc.PushTransform(new TranslateTransform(-128,-128));
        dc.DrawEllipse(Sphere,new Pen(new SolidColorBrush(Color.FromArgb(110,132,142,161)),.9),new(128,128),109,109);
        dc.PushClip(new EllipseGeometry(new Point(128,128),108.9,108.9));
        dc.DrawGeometry(Ink,null,SphereEyes.Path(Yaw,Pitch,LeftEye,-1));dc.DrawGeometry(Ink,null,SphereEyes.Path(Yaw,Pitch,RightEye,1));dc.Pop();
        dc.Pop();dc.Pop();dc.Pop();dc.Pop();
    }
}
