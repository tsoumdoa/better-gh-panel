using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Rhino.Display;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // A borrowed CPU snapshot belongs to the foreground pass that requested it.
    internal sealed class PanelSceneDepth : IDisposable
    {
        private float[] _pixels;
        [ThreadStatic] private static float[]? _spare;
        private static volatile bool _depthUnavailable;
        private readonly Size _viewport;
        private readonly Rectangle _region;
        private readonly double _scaleX, _scaleY, _z, _w, _zOffset, _wOffset, _depthStart, _depthSpan, _modelTolerance;
        private readonly bool _reversed;

        private PanelSceneDepth(float[] pixels, Size viewport, Rectangle region, Size screen,
            float[] projection, double[] range, double modelTolerance)
        {
            _pixels = pixels; _viewport = viewport; _region = region;
            _scaleX = (double)viewport.Width / screen.Width;
            _scaleY = (double)viewport.Height / screen.Height;
            _z = projection[10]; _w = projection[11];
            _zOffset = projection[14]; _wOffset = projection[15];
            _depthStart = range[0]; _depthSpan = range[1] - range[0];
            _reversed = (_w * _zOffset - _z * _wOffset) * _depthSpan < 0;
            _modelTolerance = modelTolerance;
        }

        internal static PanelSceneDepth? Capture(DisplayPipeline pipeline, RhinoViewport viewport, Size screen, Rectangle anchors)
        {
            if (_depthUnavailable || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || screen.Width <= 0 || screen.Height <= 0) return null;
            try { return pipeline.IsOpenGL ? CaptureOpenGl(pipeline, viewport, screen, anchors) : null; }
            catch (Exception)
            {
                // Missing entry points and unexpected host errors must leave
                // labels drawable and must not throw again on every redraw.
                _depthUnavailable = true;
                return null;
            }
        }

        private static PanelSceneDepth? CaptureOpenGl(DisplayPipeline pipeline, RhinoViewport viewport, Size screen, Rectangle anchors)
        {
            pipeline.Flush();
            var gl = Framebuffers.Current();
            if (gl == null || glGetError() != 0) return null;
            glGetInteger(0x935D /* GL_CLIP_DEPTH_MODE */, out int clipDepth);
            uint clipError = glGetError();
            // Before GL 4.5 the query can be unavailable and the depth mode is fixed.
            if (clipDepth == 0x935F /* GL_ZERO_TO_ONE */ || clipError != 0 && clipError != 0x0500 /* GL_INVALID_ENUM */) return null;

            var port = new int[4];
            glGetIntegerv(0x0BA2 /* GL_VIEWPORT */, port);
            var size = new Size(port[2], port[3]);
            if (size.Width <= 0 || size.Height <= 0) return null;
            double sx = (double)size.Width / screen.Width, sy = (double)size.Height / screen.Height;
            // Convert viewport-local, Y-down screen coordinates to local GL
            // pixels, leaving one physical pixel around the projected anchors.
            var source = Rectangle.FromLTRB(
                Math.Max(0, (int)(anchors.Left * sx) - 1),
                Math.Max(0, size.Height - (int)Math.Ceiling(anchors.Bottom * sy) - 1),
                Math.Min(size.Width, (int)Math.Ceiling(anchors.Right * sx) + 1),
                Math.Min(size.Height, size.Height - (int)(anchors.Top * sy) + 1));
            source.Offset(port[0], port[1]);
            glGetInteger(0x8CA6 /* GL_DRAW_FRAMEBUFFER_BINDING */, out int draw);
            glGetInteger(0x80A9 /* GL_SAMPLES */, out int samples);
            if (!gl.ClipSource(ref source, draw, pipeline.FrameSize, draw != 0 && samples > 0, out uint resolveFormat)) return null;

            var projection = pipeline.GetOpenGLCameraToClip();
            if (projection[11] == 0 && projection[15] == 0) return null;
            var range = new double[2];
            glGetDoublev(0x0B70 /* GL_DEPTH_RANGE */, range);
            double modelTolerance = Math.Max(1e-6, Math.Min(.01, viewport.ParentView?.Document?.ModelAbsoluteTolerance ?? .001));

            int count = checked(source.Width * source.Height);
            var pixels = _spare;
            _spare = null;
            if (pixels == null || pixels.Length < count) pixels = new float[count];
            bool read = false;
            try
            {
                read = gl.Read(source, draw, resolveFormat, pixels);
                if (!read) return null;
                source.Offset(-port[0], -port[1]);
                return new PanelSceneDepth(pixels, size, source, screen, projection, range, modelTolerance);
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

        internal static void ForgetResolveTarget() => Framebuffers.Forget();

        private double WindowDepth(double cameraZ) =>
            _depthStart + ((_z * cameraZ + _zOffset) / (_w * cameraZ + _wOffset) + 1) * .5 * _depthSpan;

        internal bool IsVisible(Point2d screen, double cameraZ)
        {
            if (_w * cameraZ + _wOffset <= 0) return true;
            double depth = WindowDepth(cameraZ);
            double towardCamera = WindowDepth(cameraZ + _modelTolerance);
            // Allow precision error in window depth, bounded by a tiny model
            // distance. Parallel projection uses the same bound at any range.
            double epsilon = Math.Min(2e-6, Math.Abs(towardCamera - depth));
            float cutoff = (float)(depth + (_reversed ? epsilon : -epsilon));
            if (float.IsNaN(cutoff)) return true;
            int x = Math.Min(_viewport.Width - 1, (int)(screen.X * _scaleX)) - _region.Left;
            int y = Math.Max(0, _viewport.Height - 1 - (int)(screen.Y * _scaleY)) - _region.Top;
            if (x < 0 || x >= _region.Width || y < 0 || y >= _region.Height) return true;
            int right = Math.Min(_region.Width - 1, x + 1), top = Math.Min(_region.Height - 1, y + 1);
            for (int row = Math.Max(0, y - 1); row <= top; row++)
                for (int col = Math.Max(0, x - 1); col <= right; col++)
                {
                    float pixel = _pixels[row * _region.Width + col];
                    if (_reversed ? pixel <= cutoff : pixel >= cutoff) return true;
                }
            return false;
        }

        private const uint DepthComponent = 0x1902, Float = 0x1406, ScissorTest = 0x0C11;
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentDC();
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);
        [DllImport("gdi32.dll")] private static extern int GetPixelFormat(IntPtr dc);
        [DllImport("opengl32.dll")] private static extern void glGetIntegerv(uint name, [Out] int[] values);
        [DllImport("opengl32.dll", EntryPoint = "glGetIntegerv")] private static extern void glGetInteger(uint name, out int value);
        [DllImport("opengl32.dll")] private static extern void glGetDoublev(uint name, [Out] double[] values);
        [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, [Out] float[] pixels);
        [DllImport("opengl32.dll")] private static extern void glReadBuffer(uint mode);
        [DllImport("opengl32.dll")] private static extern void glDrawBuffer(uint mode);
        [DllImport("opengl32.dll")] private static extern void glPixelStorei(uint name, int value);
        [DllImport("opengl32.dll")] private static extern uint glGetError();

        private sealed class Framebuffers
        {
            private const uint Draw = 0x8CA9, ReadTarget = 0x8CA8, Renderbuffer = 0x8D41, PixelPackBuffer = 0x88EB;
            private const uint DepthAttachment = 0x8D00, DepthStencilAttachment = 0x821A;
            private const int LargeResolvePixels = 4 * 1024 * 1024;
            private static readonly uint[] PackSettings =
                { 0x0D05 /* ALIGNMENT */, 0x0D02 /* ROW_LENGTH */, 0x0D03 /* SKIP_ROWS */, 0x0D04 /* SKIP_PIXELS */,
                  0x806B /* SKIP_IMAGES */, 0x806C /* IMAGE_HEIGHT */, 0x0D00 /* SWAP_BYTES */ };

            private delegate void Bind(uint target, int name);
            private delegate void Create(int count, out int name);
            private delegate void Delete(int count, ref int name);
            private delegate void Storage(uint target, uint format, int width, int height);
            private delegate void Attach(uint target, uint attachment, uint renderbufferTarget, int name);
            private delegate uint Status(uint target);
            private delegate void AttachmentParameter(uint target, uint attachment, uint parameter, out int value);
            private delegate void BufferParameter(uint target, uint parameter, out int value);
            private delegate void TextureParameter(int name, int level, uint parameter, out int value);
            private delegate void Blit(int x0, int y0, int x1, int y1, int dx0, int dy0, int dx1, int dy1, uint mask, uint filter);
            private delegate byte IsEnabledIndexed(uint capability, uint index);
            private delegate void SetEnabledIndexed(uint capability, uint index);

            private readonly Bind _bindFramebuffer = Load<Bind>("glBindFramebuffer");
            private readonly Bind _bindRenderbuffer = Load<Bind>("glBindRenderbuffer");
            private readonly Bind _bindBuffer = Load<Bind>("glBindBuffer");
            private readonly Create _genFramebuffers = Load<Create>("glGenFramebuffers");
            private readonly Create _genRenderbuffers = Load<Create>("glGenRenderbuffers");
            private readonly Delete _deleteFramebuffers = Load<Delete>("glDeleteFramebuffers");
            private readonly Delete _deleteRenderbuffers = Load<Delete>("glDeleteRenderbuffers");
            private readonly Storage _storage = Load<Storage>("glRenderbufferStorage");
            private readonly Attach _attach = Load<Attach>("glFramebufferRenderbuffer");
            private readonly Status _status = Load<Status>("glCheckFramebufferStatus");
            private readonly AttachmentParameter _attachment = Load<AttachmentParameter>("glGetFramebufferAttachmentParameteriv");
            private readonly BufferParameter _renderbuffer = Load<BufferParameter>("glGetRenderbufferParameteriv");
            private readonly TextureParameter? _texture = TryLoad<TextureParameter>("glGetTextureLevelParameteriv");
            private readonly Blit _blit = Load<Blit>("glBlitFramebuffer");
            private readonly IsEnabledIndexed _enabled = Load<IsEnabledIndexed>("glIsEnabledi");
            private readonly SetEnabledIndexed _enable = Load<SetEnabledIndexed>("glEnablei");
            private readonly SetEnabledIndexed _disable = Load<SetEnabledIndexed>("glDisablei");
            private readonly int[] _pack = new int[PackSettings.Length];
            private readonly (IntPtr Context, IntPtr Dc, int PixelFormat) _key;
            private int _targetFramebuffer, _targetBuffer;
            private Size _targetSize;
            private uint _targetFormat;
            [ThreadStatic] private static Framebuffers? _current;

            private Framebuffers((IntPtr, IntPtr, int) key) { _key = key; }

            private static T Load<T>(string name) where T : Delegate => TryLoad<T>(name) ?? throw new NotSupportedException(name);

            private static T? TryLoad<T>(string name) where T : Delegate
            {
                var address = wglGetProcAddress(name);
                if (address.ToInt64() is 0 or 1 or 2 or 3 or -1) return null;
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }

            internal static Framebuffers? Current()
            {
                var context = wglGetCurrentContext();
                if (context == IntPtr.Zero) return null;
                var dc = wglGetCurrentDC();
                var key = (context, dc, GetPixelFormat(dc));
                // Never make another context current to dispose an old target.
                // On a context change we forget its names and the driver owns their lifetime.
                return _current?._key == key ? _current : _current = new Framebuffers(key);
            }

            internal static void Forget()
            {
                var current = _current;
                _current = null;
                if (current != null && wglGetCurrentContext() == current._key.Context &&
                    wglGetCurrentDC() == current._key.Dc) current.DeleteTarget();
            }

            private void DeleteTarget()
            {
                if (_targetFramebuffer != 0) _deleteFramebuffers(1, ref _targetFramebuffer);
                if (_targetBuffer != 0) _deleteRenderbuffers(1, ref _targetBuffer);
                _targetFramebuffer = _targetBuffer = 0;
            }

            internal bool ClipSource(ref Rectangle source, int draw, Size frameSize, bool resolve, out uint format)
            {
                int width = frameSize.Width, height = frameSize.Height;
                format = 0;
                if (draw != 0)
                {
                    _attachment(Draw, DepthAttachment, 0x8CD0 /* OBJECT_TYPE */, out int kind);
                    _attachment(Draw, DepthAttachment, 0x8CD1 /* OBJECT_NAME */, out int name);
                    if (kind == Renderbuffer)
                    {
                        glGetInteger(0x8CA7 /* GL_RENDERBUFFER_BINDING */, out int previous);
                        _bindRenderbuffer(Renderbuffer, name);
                        try
                        {
                            _renderbuffer(Renderbuffer, 0x8D42 /* WIDTH */, out width);
                            _renderbuffer(Renderbuffer, 0x8D43 /* HEIGHT */, out height);
                        }
                        finally { _bindRenderbuffer(Renderbuffer, previous); }
                    }
                    else if (kind == 0x1702 /* GL_TEXTURE */ && _texture != null)
                    {
                        _attachment(Draw, DepthAttachment, 0x8CD2 /* TEXTURE_LEVEL */, out int level);
                        _texture(name, level, 0x1000 /* WIDTH */, out width);
                        _texture(name, level, 0x1001 /* HEIGHT */, out height);
                    }
                    else return false;

                    if (resolve)
                    {
                        _attachment(Draw, DepthAttachment, 0x8216 /* DEPTH_SIZE */, out int depth);
                        _attachment(Draw, DepthAttachment, 0x8217 /* STENCIL_SIZE */, out int stencil);
                        _attachment(Draw, DepthAttachment, 0x8211 /* COMPONENT_TYPE */, out int type);
                        format = (depth, stencil, type) switch
                        {
                            (16, 0, 0x8C17) => 0x81A5, // GL_UNSIGNED_NORMALIZED
                            (24, 0, 0x8C17) => 0x81A6,
                            (32, 0, 0x8C17) => 0x81A7,
                            (32, 0, (int)Float) => 0x8CAC,
                            (24, 8, 0x8C17) => 0x88F0,
                            (32, 8, (int)Float) => 0x8CAD,
                            _ => 0
                        };
                        if (format == 0) return false;
                    }
                }

                source.Intersect(new Rectangle(0, 0, width, height));
                // A detail or capture tile can constrain rendering further.
                if (_enabled(ScissorTest, 0) != 0)
                {
                    var tile = new int[4];
                    glGetIntegerv(0x0C10 /* GL_SCISSOR_BOX, index zero */, tile);
                    source.Intersect(new Rectangle(tile[0], tile[1], tile[2], tile[3]));
                }
                return glGetError() == 0 && source.Width > 0 && source.Height > 0;
            }

            internal bool Read(Rectangle source, int draw, uint resolveFormat, float[] pixels)
            {
                glGetInteger(0x8CAA /* GL_READ_FRAMEBUFFER_BINDING */, out int read);
                glGetInteger(0x88ED /* GL_PIXEL_PACK_BUFFER_BINDING */, out int packBuffer);
                for (int i = 0; i < PackSettings.Length; i++) glGetInteger(PackSettings[i], out _pack[i]);
                try
                {
                    for (int i = 0; i < PackSettings.Length; i++) glPixelStorei(PackSettings[i], i == 0 ? 4 : 0);
                    if (packBuffer != 0) _bindBuffer(PixelPackBuffer, 0);
                    _bindFramebuffer(ReadTarget, draw);
                    if (resolveFormat != 0) return ResolveAndRead(source, draw, resolveFormat, pixels);
                    glReadPixels(source.X, source.Y, source.Width, source.Height, DepthComponent, Float, pixels);
                    return glGetError() == 0;
                }
                finally
                {
                    _bindFramebuffer(ReadTarget, read);
                    if (packBuffer != 0) _bindBuffer(PixelPackBuffer, packBuffer);
                    for (int i = 0; i < PackSettings.Length; i++) glPixelStorei(PackSettings[i], _pack[i]);
                }
            }

            private bool ResolveAndRead(Rectangle source, int draw, uint format, float[] pixels)
            {
                glGetInteger(0x8CA7 /* GL_RENDERBUFFER_BINDING */, out int previousBuffer);
                int framebuffer = 0, buffer = 0;
                bool retained = _targetFramebuffer != 0 && _targetFormat == format &&
                    source.Width <= _targetSize.Width && source.Height <= _targetSize.Height;
                bool scissor = _enabled(ScissorTest, 0) != 0;
                try
                {
                    if (retained) framebuffer = _targetFramebuffer;
                    else
                    {
                        // Allocating a full 4K resolve cost over 3 ms. Only
                        // retain a target for reads large enough to benefit.
                        bool large = (long)source.Width * source.Height >= LargeResolvePixels;
                        if (large) DeleteTarget();
                        _genFramebuffers(1, out framebuffer);
                        _genRenderbuffers(1, out buffer);
                        if (framebuffer == 0 || buffer == 0) return false;
                        _bindRenderbuffer(Renderbuffer, buffer);
                        _storage(Renderbuffer, format, source.Width, source.Height);
                        _bindFramebuffer(Draw, framebuffer);
                        _bindFramebuffer(ReadTarget, framebuffer);
                        _attach(Draw, format == 0x88F0 || format == 0x8CAD ? DepthStencilAttachment : DepthAttachment, Renderbuffer, buffer);
                        glDrawBuffer(0 /* GL_NONE */);
                        glReadBuffer(0 /* GL_NONE */);
                        if (_status(Draw) != 0x8CD5 /* GL_FRAMEBUFFER_COMPLETE */ || glGetError() != 0) return false;
                        if (large)
                        {
                            _targetFramebuffer = framebuffer; _targetBuffer = buffer;
                            _targetSize = source.Size; _targetFormat = format;
                            retained = true;
                        }
                    }

                    _bindFramebuffer(Draw, framebuffer);
                    _bindFramebuffer(ReadTarget, draw);
                    if (scissor) _disable(ScissorTest, 0);
                    _blit(source.Left, source.Top, source.Right, source.Bottom, 0, 0, source.Width, source.Height,
                        0x0100 /* GL_DEPTH_BUFFER_BIT */, 0x2600 /* GL_NEAREST */);
                    if (glGetError() != 0) return false;
                    _bindFramebuffer(ReadTarget, framebuffer);
                    glReadPixels(0, 0, source.Width, source.Height, DepthComponent, Float, pixels);
                    return glGetError() == 0;
                }
                finally
                {
                    _bindFramebuffer(Draw, draw);
                    _bindFramebuffer(ReadTarget, draw);
                    _bindRenderbuffer(Renderbuffer, previousBuffer);
                    if (scissor) _enable(ScissorTest, 0);
                    if (!retained)
                    {
                        if (framebuffer != 0) _deleteFramebuffers(1, ref framebuffer);
                        if (buffer != 0) _deleteRenderbuffers(1, ref buffer);
                    }
                }
            }
        }
    }
}
