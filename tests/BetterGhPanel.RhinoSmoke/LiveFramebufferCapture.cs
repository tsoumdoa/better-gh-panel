using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Test-only readback of the active OpenGL viewport. This is not a Rhino
// end-of-frame notification and does not establish preview-resource lifetime.
internal static class LiveFramebufferCapture
{
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("opengl32.dll")] private static extern void glGetIntegerv(uint name, int[] value);
    [DllImport("opengl32.dll")] private static extern uint glGetError();
    [DllImport("opengl32.dll")] private static extern void glPixelStorei(uint name, int value);
    [DllImport("opengl32.dll")] private static extern void glGenTextures(int count, out uint texture);
    [DllImport("opengl32.dll")] private static extern void glDeleteTextures(int count, ref uint texture);
    [DllImport("opengl32.dll")] private static extern void glBindTexture(uint target, uint texture);
    [DllImport("opengl32.dll")] private static extern void glTexParameteri(uint target, uint name, int value);
    [DllImport("opengl32.dll")] private static extern void glTexImage2D(uint target, int level, int format, int width,
        int height, int border, uint pixelFormat, uint pixelType, IntPtr pixels);
    [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int width, int height,
        uint format, uint type, [Out] byte[] pixels);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenFramebuffers(int count, out uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteFramebuffers(int count, ref uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindFramebuffer(uint target, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void FramebufferTexture2D(uint target, uint attachment,
        uint textureTarget, uint texture, int level);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint CheckFramebufferStatus(uint target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BlitFramebuffer(int sx0, int sy0, int sx1, int sy1,
        int dx0, int dy0, int dx1, int dy1, uint mask, uint filter);

    private static T Function<T>(string name) where T : Delegate
    {
        var pointer = wglGetProcAddress(name);
        if (pointer == IntPtr.Zero || pointer == new IntPtr(1) || pointer == new IntPtr(2) ||
            pointer == new IntPtr(3) || pointer == new IntPtr(-1))
            throw new InvalidOperationException("OpenGL function unavailable: " + name);
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static int Integer(uint name)
    {
        var value = new int[1]; glGetIntegerv(name, value); return value[0];
    }

    internal static Bitmap Capture()
    {
        if (wglGetCurrentContext() == IntPtr.Zero) throw new InvalidOperationException("No current OpenGL context.");
        var viewport = new int[4]; glGetIntegerv(0x0BA2, viewport); // GL_VIEWPORT
        int width = viewport[2], height = viewport[3];
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            throw new InvalidOperationException("Unexpected OpenGL viewport size.");
        if (Integer(0x88ED) != 0) throw new InvalidOperationException("A pixel pack buffer is already bound.");

        var gen = Function<GenFramebuffers>("glGenFramebuffers");
        var delete = Function<DeleteFramebuffers>("glDeleteFramebuffers");
        var bind = Function<BindFramebuffer>("glBindFramebuffer");
        var attach = Function<FramebufferTexture2D>("glFramebufferTexture2D");
        var status = Function<CheckFramebufferStatus>("glCheckFramebufferStatus");
        var blit = Function<BlitFramebuffer>("glBlitFramebuffer");
        int previousRead = Integer(0x8CAA), previousDraw = Integer(0x8CA6), previousTexture = Integer(0x8069);
        uint[] packNames = { 0x0D05, 0x0D02, 0x0D03, 0x0D04 }; // alignment, row length, row/pixel skips
        var previousPack = new int[packNames.Length];
        for (int i = 0; i < packNames.Length; i++) previousPack[i] = Integer(packNames[i]);
        gen(1, out uint framebuffer); glGenTextures(1, out uint texture);
        try
        {
            glBindTexture(0x0DE1, texture); // GL_TEXTURE_2D
            glTexParameteri(0x0DE1, 0x2801, 0x2600); glTexParameteri(0x0DE1, 0x2800, 0x2600); // nearest
            glTexImage2D(0x0DE1, 0, 0x8058, width, height, 0, 0x1908, 0x1401, IntPtr.Zero); // RGBA8
            bind(0x8CA9, framebuffer); // GL_DRAW_FRAMEBUFFER
            attach(0x8CA9, 0x8CE0, 0x0DE1, texture, 0);
            if (status(0x8CA9) != 0x8CD5) throw new InvalidOperationException("OpenGL readback framebuffer is incomplete.");
            blit(viewport[0], viewport[1], viewport[0] + width, viewport[1] + height,
                0, 0, width, height, 0x00004000, 0x2600); // color, nearest: resolves MSAA
            if (glGetError() != 0) throw new InvalidOperationException("OpenGL framebuffer resolve failed.");
            bind(0x8CA8, framebuffer); // GL_READ_FRAMEBUFFER
            glPixelStorei(packNames[0], 4);
            for (int i = 1; i < packNames.Length; i++) glPixelStorei(packNames[i], 0);
            var pixels = new byte[checked(width * height * 4)];
            glReadPixels(0, 0, width, height, 0x1908, 0x1401, pixels);
            if (glGetError() != 0) throw new InvalidOperationException("OpenGL framebuffer read failed.");
            return ToBitmap(pixels, width, height);
        }
        finally
        {
            for (int i = 0; i < packNames.Length; i++) glPixelStorei(packNames[i], previousPack[i]);
            bind(0x8CA8, (uint)previousRead); bind(0x8CA9, (uint)previousDraw);
            glBindTexture(0x0DE1, (uint)previousTexture);
            delete(1, ref framebuffer); glDeleteTextures(1, ref texture);
        }
    }

    private static Bitmap ToBitmap(byte[] pixels, int width, int height)
    {
        var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                int source = (height - y - 1) * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int p = source + x * 4, q = x * 4;
                    row[q] = pixels[p + 2]; row[q + 1] = pixels[p + 1]; row[q + 2] = pixels[p]; row[q + 3] = pixels[p + 3];
                }
                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
            }
        }
        catch { image.UnlockBits(data); image.Dispose(); throw; }
        image.UnlockBits(data);
        return image;
    }
}
