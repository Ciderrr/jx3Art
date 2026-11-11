using System.Runtime.InteropServices;

namespace JX3Artist;

internal enum TrayAction : uint { None, Open, Settings, Exit }

internal static class TrayMenu
{
    [DllImport("user32.dll")]static extern nint CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool AppendMenu(nint menu,uint flags,nuint id,string text);
    [DllImport("user32.dll")]static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]static extern uint TrackPopupMenuEx(nint menu,uint flags,int x,int y,nint window,nint parameters);
    [DllImport("user32.dll")]static extern bool PostMessage(nint window,uint message,nint w,nint l);
    [DllImport("user32.dll")]static extern bool GetCursorPos(out Point position);
    [DllImport("user32.dll")]static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetMenuString(nint menu,uint item,System.Text.StringBuilder label,int size,uint flags);
    [StructLayout(LayoutKind.Sequential)]struct Point{public int X,Y;}

    static nint Build()
    {
        nint menu=CreatePopupMenu();
        if(menu==0)return 0;
        if(AppendMenu(menu,0,(nuint)TrayAction.Open,"打开助手窗口")&&AppendMenu(menu,0,(nuint)TrayAction.Settings,"设置")&&
            AppendMenu(menu,0,(nuint)TrayAction.Exit,"退出助手"))return menu;
        DestroyMenu(menu);
        return 0;
    }

    internal static TrayAction Show(nint window,nint coordinates,bool shellAnchor)
    {
        Point point;
        if(shellAnchor)
        {
            long value=coordinates;
            point=new(){X=unchecked((short)value),Y=unchecked((short)(value>>16))};
        }
        else if(!GetCursorPos(out point))return TrayAction.None;
        nint menu=Build();
        if(menu==0)return TrayAction.None;
        try
        {
            SetForegroundWindow(window);
            const uint flags=0x0100|0x0080|0x0002;
            uint result=TrackPopupMenuEx(menu,flags,point.X,point.Y,window,0);
            return result is >=1 and <=3?(TrayAction)result:TrayAction.None;
        }
        finally
        {
            PostMessage(window,0,0,0);
            DestroyMenu(menu);
        }
    }

    internal static void AddTests(Action<string,Action> test)
    {
        test("native tray menu exposes three commands in the requested order",()=>
        {
            nint menu=Build();
            if(menu==0)throw new InvalidOperationException("native menu creation failed");
            try
            {
                var labels=new List<string>();
                for(uint i=0;i<GetMenuItemCount(menu);i++)
                {
                    var label=new System.Text.StringBuilder(64);
                    GetMenuString(menu,i,label,label.Capacity,0x0400);
                    labels.Add(label.ToString());
                }
                if(!labels.SequenceEqual(new[]{"打开助手窗口","设置","退出助手"}))
                    throw new InvalidOperationException("unexpected native tray menu");
            }
            finally{DestroyMenu(menu);}
        });
    }
}
