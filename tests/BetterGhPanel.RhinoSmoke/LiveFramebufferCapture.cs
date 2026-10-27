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
    [DllImport("opengl32.dll")] private static extern void glGetFloatv(uint name, float[] value);
    [DllImport("opengl32.dll")] private static extern uint glGetError();
    [DllImport("opengl32.dll")] private static extern byte glIsEnabled(uint capability);
    [DllImport("opengl32.dll")] private static extern void glEnable(uint capability);
    [DllImport("opengl32.dll")] private static extern void glDisable(uint capability);
    [DllImport("opengl32.dll")] private static extern void glColorMask(byte red, byte green, byte blue, byte alpha);
    [DllImport("opengl32.dll")] private static extern void glClearColor(float red, float green, float blue, float alpha);
    [DllImport("opengl32.dll")] private static extern void glClear(uint mask);
    [DllImport("opengl32.dll")] private static extern void glViewport(int x, int y, int width, int height);
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
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenBuffers(int count, out uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteBuffers(int count, ref uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindBuffer(uint target, uint id);

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

    internal static Bitmap Capture(Action? afterAttachmentForTesting = null)
    {
        if (wglGetCurrentContext() == IntPtr.Zero) throw new InvalidOperationException("No current OpenGL context.");
        var viewport = new int[4]; glGetIntegerv(0x0BA2, viewport); // GL_VIEWPORT
        int width = viewport[2], height = viewport[3];
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            throw new InvalidOperationException("Unexpected OpenGL viewport size.");
        if (Integer(0x88ED) != 0) throw new InvalidOperationException("A pixel pack buffer is already bound.");
        if (Integer(0x88EF) != 0) throw new InvalidOperationException("A pixel unpack buffer is already bound.");

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
            afterAttachmentForTesting?.Invoke();
            // Rhino draws to the draw binding, which can differ from the
            // ambient read binding. Preserve the latter and resolve the former.
            bind(0x8CA8, (uint)previousDraw);
            if (status(0x8CA8) != 0x8CD5) throw new InvalidOperationException("OpenGL source framebuffer is incomplete.");
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

    // Exercise different read/draw sources and failure cleanup in the actual
    // viewport context, using private sentinel framebuffers and restoring it.
    internal static void VerifyState(Action<bool, string> check)
    {
        var gen = Function<GenFramebuffers>("glGenFramebuffers");
        var delete = Function<DeleteFramebuffers>("glDeleteFramebuffers");
        var bind = Function<BindFramebuffer>("glBindFramebuffer");
        var attach = Function<FramebufferTexture2D>("glFramebufferTexture2D");
        var status = Function<CheckFramebufferStatus>("glCheckFramebufferStatus");
        var genBuffers = Function<GenBuffers>("glGenBuffers");
        var deleteBuffers = Function<DeleteBuffers>("glDeleteBuffers");
        var bindBuffer = Function<BindBuffer>("glBindBuffer");
        int previousRead = Integer(0x8CAA), previousDraw = Integer(0x8CA6), previousTexture = Integer(0x8069);
        int previousUnpack = Integer(0x88EF), previousPack = Integer(0x88ED);
        if (previousUnpack != 0 || previousPack != 0) throw new InvalidOperationException("Unexpected initial pixel buffer binding.");
        var viewport = new int[4]; glGetIntegerv(0x0BA2, viewport);
        var colorMask = new int[4]; glGetIntegerv(0x0C23, colorMask);
        var clearColor = new float[4]; glGetFloatv(0x0C22, clearColor);
        bool scissor = glIsEnabled(0x0C11) != 0;
        gen(1, out uint redFramebuffer); gen(1, out uint greenFramebuffer);
        glGenTextures(1, out uint redTexture); glGenTextures(1, out uint greenTexture);
        genBuffers(1, out uint unpackBuffer);
        try
        {
            glViewport(0, 0, 16, 16); glDisable(0x0C11); glColorMask(1, 1, 1, 1);
            void Prepare(uint framebuffer, uint texture, float red, float green)
            {
                glBindTexture(0x0DE1, texture);
                glTexParameteri(0x0DE1, 0x2801, 0x2600); glTexParameteri(0x0DE1, 0x2800, 0x2600);
                glTexImage2D(0x0DE1, 0, 0x8058, 16, 16, 0, 0x1908, 0x1401, IntPtr.Zero);
                bind(0x8D40, framebuffer); attach(0x8D40, 0x8CE0, 0x0DE1, texture, 0);
                if (status(0x8D40) != 0x8CD5) throw new InvalidOperationException("Sentinel framebuffer is incomplete.");
                glClearColor(red, green, 0, 1); glClear(0x00004000);
            }
            Prepare(redFramebuffer, redTexture, 1, 0);
            Prepare(greenFramebuffer, greenTexture, 0, 1);
            bind(0x8CA8, redFramebuffer); bind(0x8CA9, greenFramebuffer);
            using (var image = Capture())
            {
                Color pixel = image.GetPixel(8, 8);
                check(pixel.G > 200 && pixel.R < 30 && Integer(0x8CAA) == redFramebuffer && Integer(0x8CA6) == greenFramebuffer,
                    "framebuffer diagnostic reads the draw binding and restores a different read binding");
            }
            bind(0x8CA8, greenFramebuffer); bind(0x8CA9, redFramebuffer);
            using (var image = Capture())
            {
                Color pixel = image.GetPixel(8, 8);
                check(pixel.R > 200 && pixel.G < 30 && Integer(0x8CAA) == greenFramebuffer && Integer(0x8CA6) == redFramebuffer,
                    "framebuffer diagnostic follows alternating draw bindings and restores both bindings");
            }
            int textureBeforeFailure = Integer(0x8069), alignmentBeforeFailure = Integer(0x0D05);
            bool injected = false;
            try { using var ignored = Capture(() => throw new InvalidOperationException("injected readback failure")); }
            catch (InvalidOperationException error) { injected = error.Message == "injected readback failure"; }
            check(injected && Integer(0x8CAA) == greenFramebuffer && Integer(0x8CA6) == redFramebuffer &&
                Integer(0x8069) == textureBeforeFailure && Integer(0x0D05) == alignmentBeforeFailure,
                "framebuffer diagnostic restores bindings and pixel-pack state after a readback failure");

            bindBuffer(0x88EC, unpackBuffer); // GL_PIXEL_UNPACK_BUFFER
            bool refused = false;
            try { using var ignored = Capture(); }
            catch (InvalidOperationException error) { refused = error.Message.Contains("unpack buffer"); }
            check(refused && Integer(0x88EF) == unpackBuffer && Integer(0x8CAA) == greenFramebuffer &&
                Integer(0x8CA6) == redFramebuffer && Integer(0x8069) == textureBeforeFailure,
                "framebuffer diagnostic rejects a bound unpack buffer without changing the bindings");
        }
        finally
        {
            bindBuffer(0x88EC, (uint)previousUnpack);
            bind(0x8CA8, (uint)previousRead); bind(0x8CA9, (uint)previousDraw);
            glBindTexture(0x0DE1, (uint)previousTexture);
            glViewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            glClearColor(clearColor[0], clearColor[1], clearColor[2], clearColor[3]);
            glColorMask((byte)colorMask[0], (byte)colorMask[1], (byte)colorMask[2], (byte)colorMask[3]);
            if (scissor) glEnable(0x0C11); else glDisable(0x0C11);
            deleteBuffers(1, ref unpackBuffer); delete(1, ref redFramebuffer); delete(1, ref greenFramebuffer);
            glDeleteTextures(1, ref redTexture); glDeleteTextures(1, ref greenTexture);
        }
    }
}
