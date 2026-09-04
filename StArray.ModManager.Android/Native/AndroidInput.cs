using System.Runtime.InteropServices;

namespace StArray.ModManager.Android.Native;

/// <summary>上游 Android NDK 输入 API 声明。仅提供 ABI，不安装输入 hook。</summary>
public static class AndroidInput
{
    [Flags]
    public enum PrepareFlags { None = 0, AllowNonCallbacks = 1 }
    public enum EventType { Key = 1, Motion = 2 }
    public enum KeyAction { Down = 0, Up = 1, Multiple = 2 }
    public enum MotionAction
    {
        Down = 0, Up = 1, Move = 2, Cancel = 3, Outside = 4,
        PointerDown = 5, PointerUp = 6, HoverMove = 7, Scroll = 8,
        HoverEnter = 9, HoverExit = 10, ButtonPress = 11, ButtonRelease = 12
    }
    [Flags]
    public enum MetaState
    {
        None = 0, ShiftOn = 1, AltOn = 2, AltLeftOn = 0x10,
        AltRightOn = 0x20, ShiftLeftOn = 0x40, ShiftRightOn = 0x80
    }
    public static class MotionMask
    {
        public const int Action = 0xff;
        public const int PointerIndex = 0xff00;
        public const int PointerIndexShift = 8;
    }

    public const int ActionMask = MotionMask.Action;
    public const int ActionPointerIndexMask = MotionMask.PointerIndex;
    public const int ActionPointerIndexShift = MotionMask.PointerIndexShift;

    [StructLayout(LayoutKind.Sequential)] public struct AInputEvent { }
    [StructLayout(LayoutKind.Sequential)] public struct AInputQueue { }
    [StructLayout(LayoutKind.Sequential)] public struct ALooper { }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int InputQueueCallback(int fd, int events, IntPtr data);

    private const string Lib = "libandroid.so";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ALooper_prepare(PrepareFlags opts);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void AInputQueue_attachLooper(IntPtr queue, IntPtr looper, int ident,
        InputQueueCallback callback, IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void AInputQueue_detachLooper(IntPtr queue);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AInputQueue_getEvent(IntPtr queue, out IntPtr outEvent);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void AInputQueue_finishEvent(IntPtr queue, IntPtr ev, int handled);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern EventType AInputEvent_getType(IntPtr ev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AKeyEvent_getKeyCode(IntPtr ev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern KeyAction AKeyEvent_getAction(IntPtr ev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern MetaState AKeyEvent_getMetaState(IntPtr ev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AKeyEvent_getRepeatCount(IntPtr ev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AMotionEvent_getAction(IntPtr ev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float AMotionEvent_getX(IntPtr ev, int pointerIndex);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float AMotionEvent_getY(IntPtr ev, int pointerIndex);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AMotionEvent_getPointerCount(IntPtr ev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AMotionEvent_getPointerId(IntPtr ev, int pointerIndex);
    /// <summary>事件时间，单位纳秒，时钟源为 CLOCK_MONOTONIC。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long AMotionEvent_getEventTime(IntPtr ev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long AMotionEvent_getDownTime(IntPtr ev);

    public static MotionAction GetMainAction(int rawAction) =>
        (MotionAction)(rawAction & ActionMask);

    public static int GetPointerIndex(int rawAction) =>
        (rawAction & ActionPointerIndexMask) >> ActionPointerIndexShift;

    public static MotionAction GetMainAction(this IntPtr ev)
    {
        if (AInputEvent_getType(ev) != EventType.Motion)
            throw new InvalidOperationException("Event is not a motion event.");
        return GetMainAction(AMotionEvent_getAction(ev));
    }

    public static int GetPointerIndex(this IntPtr ev) =>
        GetPointerIndex(AMotionEvent_getAction(ev));
}
