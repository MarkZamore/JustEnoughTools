using System;using System.Runtime.InteropServices;using System.Windows;using System.Windows.Interop;using System.Windows.Media;

// Windows SDK IUISettings3 ABI: six IInspectable slots precede GetColorValue.
namespace JustEnoughTools
{
public static class WindowsTheme
{
 [StructLayout(LayoutKind.Sequential)]struct NativeColor{public byte A,R,G,B;}
 [StructLayout(LayoutKind.Sequential)]struct Margins{public int Left,Right,Top,Bottom;}
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int GetColorValue(IntPtr self,int type,out NativeColor color);
 [DllImport("combase.dll")]static extern int RoInitialize(uint type);
 [DllImport("combase.dll")]static extern void RoUninitialize();
 [DllImport("combase.dll",CharSet=CharSet.Unicode)]static extern int WindowsCreateString(string text,uint length,out IntPtr value);
 [DllImport("combase.dll")]static extern int WindowsDeleteString(IntPtr value);
 [DllImport("combase.dll")]static extern int RoActivateInstance(IntPtr name,out IntPtr instance);
 [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
 [DllImport("dwmapi.dll")]static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd,ref Margins margins);
 public static string AccentSource {get;private set;}
 public static bool MicaEnabled {get;private set;}
 public static Color AccentFill {get;private set;}
 public static Color AccentText {get;private set;}
 public static void Initialize(Application app){RefreshAccent(app);}
 public static void RefreshAccent(Application app)
 {
  Color fill=Color.FromRgb(96,205,255),text=Color.FromRgb(153,235,255);AccentSource="Fluent fallback";
  IntPtr name=IntPtr.Zero,instance=IntPtr.Zero,settings=IntPtr.Zero;int initialized=-1;
  try{
   initialized=RoInitialize(0);
   string className="Windows.UI.ViewManagement.UISettings";
   Marshal.ThrowExceptionForHR(WindowsCreateString(className,(uint)className.Length,out name));
   Marshal.ThrowExceptionForHR(RoActivateInstance(name,out instance));
   Guid iid=new Guid("03021be4-5254-4781-8194-5168f7d06d7b");Marshal.ThrowExceptionForHR(Marshal.QueryInterface(instance,ref iid,out settings));
   IntPtr vtable=Marshal.ReadIntPtr(settings),method=Marshal.ReadIntPtr(vtable,6*IntPtr.Size);
   GetColorValue get=(GetColorValue)Marshal.GetDelegateForFunctionPointer(method,typeof(GetColorValue));
   NativeColor light2,light3;Marshal.ThrowExceptionForHR(get(settings,7,out light2));Marshal.ThrowExceptionForHR(get(settings,8,out light3));
   fill=Color.FromArgb(light2.A,light2.R,light2.G,light2.B);text=Color.FromArgb(light3.A,light3.R,light3.G,light3.B);AccentSource="Windows.UI.ViewManagement.UISettings";
  }catch{}finally{if(settings!=IntPtr.Zero)Marshal.Release(settings);if(instance!=IntPtr.Zero)Marshal.Release(instance);if(name!=IntPtr.Zero)WindowsDeleteString(name);if(initialized>=0)RoUninitialize();}
  AccentFill=fill;AccentText=text;
  SetBrush(app,"Brush.Accent",fill);SetBrush(app,"Brush.AccentText",text);
  SetBrush(app,"Brush.AccentHover",Blend(fill,Colors.White,0.08));SetBrush(app,"Brush.AccentPressed",Blend(fill,Colors.Black,0.12));SetBrush(app,"Brush.OnAccent",Colors.Black);
 }
 static Color Blend(Color color,Color toward,double fraction){return Color.FromRgb((byte)(color.R+(toward.R-color.R)*fraction),(byte)(color.G+(toward.G-color.G)*fraction),(byte)(color.B+(toward.B-color.B)*fraction));}
 static void SetBrush(Application app,string key,Color color){SolidColorBrush brush=new SolidColorBrush(color);brush.Freeze();app.Resources[key]=brush;}
 public static void Attach(Window window)
 {
  HwndSource source=null;HwndSourceHook hook=null;
  window.SourceInitialized+=delegate{
   IntPtr hwnd=new WindowInteropHelper(window).Handle;int dark=1;DwmSetWindowAttribute(hwnd,20,ref dark,4);
   source=HwndSource.FromHwnd(hwnd);
   hook=delegate(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled){if(message==0x320||message==0x1A){RefreshAccent(Application.Current);}return IntPtr.Zero;};
   if(source!=null)source.AddHook(hook);
   ApplyBackdrop(window,true);
  };
  window.Closed+=delegate{if(source!=null&&hook!=null)source.RemoveHook(hook);};
 }
 public static bool ApplyBackdrop(Window window,bool enabled)
 {
  IntPtr hwnd=new WindowInteropHelper(window).Handle;HwndSource source=HwndSource.FromHwnd(hwnd);bool available=false;
  // MAINWINDOW already supplies Mica behind the complete window. Extending the
  // legacy glass frame leaves a differently shaded non-client band below the caption.
  if(enabled&&hwnd!=IntPtr.Zero){try{int mica=2;Margins margins=new Margins();available=DwmExtendFrameIntoClientArea(hwnd,ref margins)==0&&DwmSetWindowAttribute(hwnd,38,ref mica,4)==0;}catch{}}
  if(!available&&hwnd!=IntPtr.Zero){int none=1;try{DwmSetWindowAttribute(hwnd,38,ref none,4);}catch{}}
  MicaEnabled=available;
  if(source!=null&&source.CompositionTarget!=null)source.CompositionTarget.BackgroundColor=available?Colors.Transparent:Color.FromRgb(32,32,32);
  window.Background=(Brush)window.FindResource("Brush.WindowBackdrop");return available;
 }
}

}
