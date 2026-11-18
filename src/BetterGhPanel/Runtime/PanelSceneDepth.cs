using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // One borrowed CPU snapshot belongs to the foreground pass that requested it.
    internal sealed class PanelSceneDepth : IDisposable
    {
        private float[] _pixels;
        [ThreadStatic] private static float[]? _spare;
        private readonly Size _viewport;
        private readonly Rectangle _region;
        private readonly double _scaleX, _scaleY, _z, _w, _zOffset, _wOffset, _clear, _depthStart, _depthSpan;
        private readonly bool _reversed;
        private readonly Point3d _camera;
        private readonly Vector3d _direction;

        private PanelSceneDepth(float[] pixels, Size viewport, Rectangle region, Size screen,
            float[] projection, double[] range, double clear, Point3d camera, Vector3d direction)
        {
            _pixels = pixels; _viewport = viewport; _region = region;
            _scaleX = (double)viewport.Width / screen.Width;
            _scaleY = (double)viewport.Height / screen.Height;
            _z = -projection[10]; _w = -projection[11];
            _zOffset = projection[14]; _wOffset = projection[15];
            _depthStart = range[0]; _depthSpan = range[1] - range[0];
            _reversed = (_z * _wOffset - _w * _zOffset) * _depthSpan < 0;
            _clear = clear; _camera = camera; _direction = direction;
        }

        internal static PanelSceneDepth? Capture(DisplayPipeline pipeline, RhinoViewport viewport, Size screen, Rectangle anchors)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !pipeline.IsOpenGL ||
                screen.Width <= 0 || screen.Height <= 0) return null;
            pipeline.Flush();
            var gl = Framebuffers.Current();
            if (gl == null) return null;

            var port = new int[4];
            glGetIntegerv(0x0BA2 /* GL_VIEWPORT */, port);
            var size = new Size(port[2], port[3]);
            if (size.Width <= 0 || size.Height <= 0) return null;
            double sx = (double)size.Width / screen.Width, sy = (double)size.Height / screen.Height;
            // Screen coordinates are viewport-local and Y-down. Convert once
            // to local GL pixels and allow one physical pixel for each neighbour.
            var region = Rectangle.FromLTRB(
                Math.Max(0, (int)(anchors.Left * sx) - 1),
                Math.Max(0, size.Height - (int)Math.Ceiling(anchors.Bottom * sy) - 1),
                Math.Min(size.Width, (int)Math.Ceiling(anchors.Right * sx) + 1),
                Math.Min(size.Height, size.Height - (int)(anchors.Top * sy) + 1));
            if (region.IsEmpty) return null;

            var direction = viewport.CameraDirection;
            var projection = pipeline.GetOpenGLCameraToClip();
            if (!direction.Unitize() || projection[11] == 0 && projection[15] == 0) return null;
            var range = new double[2];
            glGetDoublev(0x0B70 /* GL_DEPTH_RANGE */, range);
            glGetDouble(0x0B73 /* GL_DEPTH_CLEAR_VALUE */, out double clear);

            int count = checked(region.Width * region.Height);
            var pixels = _spare;
            _spare = null;
            if (pixels == null || pixels.Length < count) pixels = new float[count];
            bool read = false;
            try
            {
                var source = region;
                source.Offset(port[0], port[1]);
                read = gl.Read(source, size, pixels);
                return read ? new PanelSceneDepth(pixels, size, region, screen, projection, range, clear, viewport.CameraLocation, direction) : null;
            }
            finally { if (!read) ReturnPixels(pixels); }
        }

        public void Dispose()
        {
            ReturnPixels(_pixels);
            _pixels = Array.Empty<float>();
        }

        private static void ReturnPixels(float[] pixels)
        {
            if (_spare == null || _spare.Length < pixels.Length) _spare = pixels;
        }

        internal bool IsVisible(Point2d screen, Point3d anchor, Point3d wireAnchor)
        {
            double depth = (anchor - _camera) * _direction;
            if (wireAnchor != anchor) depth = Math.Min(depth, (wireAnchor - _camera) * _direction);
            // Apply the tolerance in camera units before projecting to the
            // non-linear perspective Z buffer.
            depth = (depth - .05) / 1.01;
            if (depth <= 0) return true;
            double clip = (_z * depth + _zOffset) / (_w * depth + _wOffset);
            double cutoff = _depthStart + (clip + 1) * .5 * _depthSpan;
            int x = Math.Min(_viewport.Width - 1, (int)(screen.X * _scaleX)) - _region.Left;
            int y = Math.Max(0, _viewport.Height - 1 - (int)(screen.Y * _scaleY)) - _region.Top;
            if (x < 0 || x >= _region.Width || y < 0 || y >= _region.Height) return true;
            int right = Math.Min(_region.Width - 1, x + 1), top = Math.Min(_region.Height - 1, y + 1);
            for (int row = Math.Max(0, y - 1); row <= top; row++)
                for (int col = Math.Max(0, x - 1); col <= right; col++)
                {
                    float pixel = _pixels[row * _region.Width + col];
                    if (pixel == _clear || (_reversed ? pixel <= cutoff : pixel >= cutoff)) return true;
                }
            return false;
        }

        private const uint DepthComponent = 0x1902, Float = 0x1406, ScissorTest = 0x0C11;
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentDC();
        [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);
        [DllImport("gdi32.dll")] private static extern int GetPixelFormat(IntPtr dc);
        [DllImport("opengl32.dll")] private static extern void glGetIntegerv(uint name, [Out] int[] values);
        [DllImport("opengl32.dll", EntryPoint = "glGetIntegerv")] private static extern void glGetInteger(uint name, out int value);
        [DllImport("opengl32.dll")] private static extern void glGetDoublev(uint name, [Out] double[] values);
        [DllImport("opengl32.dll", EntryPoint = "glGetDoublev")] private static extern void glGetDouble(uint name, out double value);
        [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, [Out] float[] pixels);
        [DllImport("opengl32.dll")] private static extern void glReadBuffer(uint mode);
        [DllImport("opengl32.dll")] private static extern void glDrawBuffer(uint mode);
        [DllImport("opengl32.dll")] private static extern void glPixelStorei(uint name, int value);
        [DllImport("opengl32.dll")] private static extern byte glIsEnabled(uint name);
        [DllImport("opengl32.dll")] private static extern void glDisable(uint name);
        [DllImport("opengl32.dll")] private static extern void glEnable(uint name);
        [DllImport("opengl32.dll")] private static extern uint glGetError();

        private sealed class Framebuffers
        {
            private const uint Draw = 0x8CA9, ReadTarget = 0x8CA8, Renderbuffer = 0x8D41, PixelPackBuffer = 0x88EB;
            private const uint DepthAttachment = 0x8D00, DepthStencilAttachment = 0x821A;
            private static readonly uint[] PackSettings =
                { 0x0D05 /* ALIGNMENT */, 0x0D02 /* ROW_LENGTH */, 0x0D03 /* SKIP_ROWS */, 0x0D04 /* SKIP_PIXELS */,
                  0x806B /* SKIP_IMAGES */, 0x806C /* IMAGE_HEIGHT */, 0x0D00 /* SWAP_BYTES */ };

            private delegate void Bind(uint target, int name);
            private delegate void Create(int count, out int name);
            private delegate void Delete(int count, ref int name);
            private delegate byte IsFramebuffer(int name);
            private delegate void Storage(uint target, uint format, int width, int height);
            private delegate void Attach(uint target, uint attachment, uint renderbufferTarget, int name);
            private delegate uint Status(uint target);
            private delegate void AttachmentParameter(uint target, uint attachment, uint parameter, out int value);
            private delegate void Blit(int x0, int y0, int x1, int y1, int dx0, int dy0, int dx1, int dy1, uint mask, uint filter);
            private readonly Bind _bindFramebuffer = Load<Bind>("glBindFramebuffer");
            private readonly Bind _bindRenderbuffer = Load<Bind>("glBindRenderbuffer");
            private readonly Bind _bindBuffer = Load<Bind>("glBindBuffer");
            private readonly Create _genFramebuffers = Load<Create>("glGenFramebuffers");
            private readonly Create _genRenderbuffers = Load<Create>("glGenRenderbuffers");
            private readonly Delete _deleteFramebuffers = Load<Delete>("glDeleteFramebuffers");
            private readonly Delete _deleteRenderbuffers = Load<Delete>("glDeleteRenderbuffers");
            private readonly IsFramebuffer _isFramebuffer = Load<IsFramebuffer>("glIsFramebuffer");
            private readonly Storage _storage = Load<Storage>("glRenderbufferStorage");
            private readonly Attach _attach = Load<Attach>("glFramebufferRenderbuffer");
            private readonly Status _status = Load<Status>("glCheckFramebufferStatus");
            private readonly AttachmentParameter _attachment = Load<AttachmentParameter>("glGetFramebufferAttachmentParameteriv");
            private readonly Blit _blit = Load<Blit>("glBlitFramebuffer");
            private readonly int[] _pack = new int[PackSettings.Length];
            private readonly IntPtr _context;
            private readonly int _pixelFormat;
            private IntPtr _dc;
            private int _framebuffer, _buffer;
            private Size _size;
            private uint _format;
            [ThreadStatic] private static List<Framebuffers>? _contexts;

            static Framebuffers()
            {
                RhinoApp.Closing += (_, __) =>
                {
                    if (_contexts == null) return;
                    foreach (var entry in _contexts) entry.Release();
                    _contexts.Clear();
                };
            }
            private Framebuffers(IntPtr context, IntPtr dc, int pixelFormat)
            { _context = context; _dc = dc; _pixelFormat = pixelFormat; }

            private static T Load<T>(string name) where T : Delegate
            {
                var address = wglGetProcAddress(name);
                if (address.ToInt64() is 0 or 1 or 2 or 3 or -1) throw new NotSupportedException(name);
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }

            internal static Framebuffers? Current()
            {
                var context = wglGetCurrentContext();
                if (context == IntPtr.Zero) return null;
                var dc = wglGetCurrentDC();
                int pixelFormat = GetPixelFormat(dc);
                var contexts = _contexts ??= new List<Framebuffers>();
                for (int i = contexts.Count - 1; i >= 0; i--)
                {
                    var entry = contexts[i];
                    if (entry._context != context) continue;
                    contexts.RemoveAt(i);
                    // A recycled handle with a different pixel format belongs
                    // to a new context, with its own GL names and delegates.
                    if (entry._pixelFormat != pixelFormat) break;
                    entry._dc = dc;
                    contexts.Add(entry);
                    return entry;
                }
                if (contexts.Count == 8)
                {
                    contexts[0].Release();
                    contexts.RemoveAt(0);
                    if (wglGetCurrentContext() != context) return null;
                }
                try
                {
                    var entry = new Framebuffers(context, dc, pixelFormat);
                    contexts.Add(entry);
                    return entry;
                }
                catch (NotSupportedException) { return null; }
            }

            private void Release()
            {
                if (_framebuffer == 0) return;
                var context = wglGetCurrentContext();
                var dc = wglGetCurrentDC();
                // A destroyed context already released its objects. Only use
                // GL names and delegates with their owning context current.
                if (context != _context && !wglMakeCurrent(_dc, _context)) return;
                try { if (GetPixelFormat(wglGetCurrentDC()) == _pixelFormat && _isFramebuffer(_framebuffer) != 0) DeleteTarget(); }
                finally { if (context != _context) wglMakeCurrent(dc, context); }
            }

            internal bool Read(Rectangle source, Size size, float[] pixels)
            {
                if (glGetError() != 0) return false;
                glGetInteger(0x8CA6 /* GL_DRAW_FRAMEBUFFER_BINDING */, out int draw);
                glGetInteger(0x8CAA /* GL_READ_FRAMEBUFFER_BINDING */, out int read);
                glGetInteger(0x80A9 /* GL_SAMPLES */, out int samples);
                glGetInteger(0x88ED /* GL_PIXEL_PACK_BUFFER_BINDING */, out int packBuffer);
                for (int i = 0; i < PackSettings.Length; i++) glGetInteger(PackSettings[i], out _pack[i]);
                bool scissor = glIsEnabled(ScissorTest) != 0;
                try
                {
                    for (int i = 0; i < PackSettings.Length; i++) glPixelStorei(PackSettings[i], i == 0 ? 4 : 0);
                    if (packBuffer != 0) _bindBuffer(PixelPackBuffer, 0);
                    if (scissor) glDisable(ScissorTest);
                    _bindFramebuffer(ReadTarget, draw);
                    if (draw != 0 && samples > 0)
                    {
                        if (!EnsureTarget(size)) return false;
                        _bindFramebuffer(Draw, _framebuffer);
                        _blit(source.Left, source.Top, source.Right, source.Bottom, 0, 0, source.Width, source.Height,
                            0x0100 /* GL_DEPTH_BUFFER_BIT */, 0x2600 /* GL_NEAREST */);
                        if (glGetError() != 0) return false;
                        _bindFramebuffer(ReadTarget, _framebuffer);
                        source.Location = System.Drawing.Point.Empty;
                    }
                    glReadPixels(source.X, source.Y, source.Width, source.Height, DepthComponent, Float, pixels);
                    return glGetError() == 0;
                }
                finally
                {
                    _bindFramebuffer(Draw, draw);
                    _bindFramebuffer(ReadTarget, read);
                    if (packBuffer != 0) _bindBuffer(PixelPackBuffer, packBuffer);
                    if (scissor) glEnable(ScissorTest);
                    for (int i = 0; i < PackSettings.Length; i++) glPixelStorei(PackSettings[i], _pack[i]);
                }
            }

            private bool EnsureTarget(Size size)
            {
                glGetInteger(0x0D56 /* GL_DEPTH_BITS */, out int bits);
                glGetInteger(0x0D57 /* GL_STENCIL_BITS */, out int stencil);
                if (bits == 0) return false;
                _attachment(Draw, DepthAttachment, 0x8211 /* GL_FRAMEBUFFER_ATTACHMENT_COMPONENT_TYPE */, out int type);
                uint format = stencil > 0 ? (type == Float ? 0x8CADu : 0x88F0u) :
                    type == Float ? 0x8CACu : bits == 16 ? 0x81A5u : bits == 24 ? 0x81A6u : 0x81A7u;
                if (_framebuffer != 0 && _isFramebuffer(_framebuffer) == 0) _framebuffer = _buffer = 0;
                if (_framebuffer != 0 && _size == size && _format == format) return true;
                DeleteTarget();

                glGetInteger(0x8CA7 /* GL_RENDERBUFFER_BINDING */, out int previousBuffer);
                glGetInteger(0x8CA6 /* GL_DRAW_FRAMEBUFFER_BINDING */, out int previousDraw);
                glGetInteger(0x8CAA /* GL_READ_FRAMEBUFFER_BINDING */, out int previousRead);
                try
                {
                    _genFramebuffers(1, out _framebuffer);
                    _genRenderbuffers(1, out _buffer);
                    _bindRenderbuffer(Renderbuffer, _buffer);
                    _storage(Renderbuffer, format, size.Width, size.Height);
                    _bindFramebuffer(Draw, _framebuffer);
                    _bindFramebuffer(ReadTarget, _framebuffer);
                    _attach(Draw, stencil > 0 ? DepthStencilAttachment : DepthAttachment, Renderbuffer, _buffer);
                    glDrawBuffer(0 /* GL_NONE */);
                    glReadBuffer(0 /* GL_NONE */);
                    if (_status(Draw) != 0x8CD5 /* GL_FRAMEBUFFER_COMPLETE */ || glGetError() != 0)
                    { DeleteTarget(); return false; }
                    _format = format; _size = size;
                    return true;
                }
                finally
                {
                    _bindRenderbuffer(Renderbuffer, previousBuffer);
                    _bindFramebuffer(Draw, previousDraw);
                    _bindFramebuffer(ReadTarget, previousRead);
                }
            }

            private void DeleteTarget()
            {
                if (_framebuffer != 0) _deleteFramebuffers(1, ref _framebuffer);
                if (_buffer != 0) _deleteRenderbuffers(1, ref _buffer);
                _framebuffer = _buffer = 0;
            }
        }
    }
}
