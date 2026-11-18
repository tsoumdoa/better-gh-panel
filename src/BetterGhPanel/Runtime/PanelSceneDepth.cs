using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using Rhino.Display;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // A borrowed CPU snapshot belongs to the foreground pass that requested it.
    internal readonly struct PanelSceneDepth : IDisposable
    {
        private const int MaxSparePixels = 4 * 1024 * 1024;
        private const double PointDepthBias = 1.0 / 65536;
        private readonly float[] _pixels;
        [ThreadStatic] private static float[]? _spare;
        [ThreadStatic] private static CaptureScratch? _scratch;
        private static volatile bool _depthUnavailable;
        private readonly Size _viewport;
        private readonly Rectangle _region;
        private readonly double _scaleX, _scaleY, _z, _w, _zOffset, _wOffset, _depthStart, _depthSpan, _modelTolerance, _precisionFloor;
        private readonly bool _reversed, _keepLarge;

        private PanelSceneDepth(float[] pixels, Size viewport, Rectangle region, Size screen,
            Transform clip, double[] range, double modelTolerance, int depthBits, bool keepLarge)
        {
            _pixels = pixels; _keepLarge = keepLarge; _viewport = viewport; _region = region;
            _scaleX = (double)viewport.Width / screen.Width;
            _scaleY = (double)viewport.Height / screen.Height;
            _z = clip.M22; _w = clip.M32;
            _zOffset = clip.M23; _wOffset = clip.M33;
            _depthStart = range[0]; _depthSpan = range[1] - range[0];
            _reversed = (_w * _zOffset - _z * _wOffset) * _depthSpan < 0;
            _modelTolerance = modelTolerance;
            // Allow four steps at the coarser of attachment or float readback precision.
            _precisionFloor = 4.0 / (1L << Math.Min(depthBits, 24));
        }

        private sealed class CaptureScratch
        {
            internal readonly int[] Bounds = new int[4];
            internal readonly double[] DepthRange = new double[2];
        }

        internal static PanelSceneDepth? Capture(DisplayPipeline pipeline, RhinoViewport viewport, Size screen, Rectangle anchors, Transform clip)
        {
            if (_depthUnavailable || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || screen.Width <= 0 || screen.Height <= 0) return null;
            try { return pipeline.IsOpenGL ? CaptureOpenGl(pipeline, viewport, screen, anchors, clip) : null; }
            catch (Exception)
            {
                // Missing entry points and unexpected host errors must leave
                // labels drawable and must not throw again on every redraw.
                _depthUnavailable = true;
                return null;
            }
        }

        private static PanelSceneDepth? CaptureOpenGl(DisplayPipeline pipeline, RhinoViewport viewport, Size screen, Rectangle anchors, Transform clip)
        {
            if (clip.M32 == 0 && clip.M33 == 0) return null;
            pipeline.Flush();
            var gl = Framebuffers.Current();
            if (gl == null) return null;
            // Avoid draining Rhino's error queue at entry. The checks after our
            // GL operations conservatively reject a capture on any error seen there.
            if (gl.HasClipControl)
            {
                glGetInteger(0x935D /* GL_CLIP_DEPTH_MODE */, out int clipDepth);
                if (glGetError() != 0 || clipDepth != 0x935E /* GL_NEGATIVE_ONE_TO_ONE */) return null;
            }

            var scratch = _scratch ??= new CaptureScratch();
            var port = scratch.Bounds;
            glGetIntegerv(0x0BA2 /* GL_VIEWPORT */, port);
            int originX = port[0], originY = port[1];
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
            source.Offset(originX, originY);
            glGetInteger(0x8CA6 /* GL_DRAW_FRAMEBUFFER_BINDING */, out int draw);
            glGetInteger(0x80A9 /* GL_SAMPLES */, out int samples);
            if (!gl.ClipSource(ref source, draw, pipeline.FrameSize, draw != 0 && samples > 0, port,
                out uint resolveFormat, out int depthBits)) return null;

            var range = scratch.DepthRange;
            glGetDoublev(0x0B70 /* GL_DEPTH_RANGE */, range);
            double modelTolerance = Math.Max(1e-6, Math.Min(.01, viewport.ParentView?.Document?.ModelAbsoluteTolerance ?? .001));

            int count = checked(source.Width * source.Height);
            // Interactive passes reuse even full 4K buffers until idle; one-off
            // view captures must not pin a large buffer afterwards.
            bool keepLarge = !pipeline.IsInViewCapture;
            var pixels = _spare;
            if (pixels != null && pixels.Length >= count) _spare = null;
#if NET7_0_OR_GREATER
            else pixels = GC.AllocateUninitializedArray<float>(count);
#else
            else pixels = new float[count];
#endif
            bool read = false;
            try
            {
                read = gl.Read(source, draw, resolveFormat, pixels);
                if (!read) return null;
                source.Offset(-originX, -originY);
                return new PanelSceneDepth(pixels, size, source, screen, clip, range, modelTolerance, depthBits, keepLarge);
            }
            finally { if (!read) ReturnPixels(pixels, keepLarge); }
        }

        public void Dispose() => ReturnPixels(_pixels, _keepLarge);

        private static void ReturnPixels(float[] pixels, bool keepLarge)
        {
            if ((keepLarge || pixels.Length <= MaxSparePixels) && (_spare == null || _spare.Length < pixels.Length)) _spare = pixels;
        }

        internal static void ReleaseIdleResources()
        {
            _spare = null;
            Framebuffers.ReleaseCurrentTarget();
        }

        private double WindowDepth(double cameraZ) =>
            _depthStart + ((_z * cameraZ + _zOffset) / (_w * cameraZ + _wOffset) + 1) * .5 * _depthSpan;

        internal bool IsVisible(Point2d screen, double cameraZ, bool point)
        {
            if (_w * cameraZ + _wOffset <= 0) return true;
            double depth = WindowDepth(cameraZ);
            // Rhino can round camera coordinates in its float draw transforms.
            // Allow for that error in model units, in addition to depth quantization.
            double allowance = Math.Min(.01, Math.Max(_modelTolerance, Math.Abs(cameraZ) / (1 << 22)));
            double towardCamera = WindowDepth(cameraZ + allowance);
            // With 24-bit depth the floor reaches one model unit at a parallel
            // span of 4,194,304 units with the standard 0–1 depth range.
            double epsilon = Math.Max(_precisionFloor, Math.Abs(towardCamera - depth));
            double cutoff = depth + (_reversed ? epsilon : -epsilon);
            // Rhino biases point sprites toward the camera. Match that depth
            // within the same epsilon without widening the tolerance for walls.
            double spriteDepth = depth + (_reversed ? PointDepthBias : -PointDepthBias);
            if (double.IsNaN(cutoff)) return true;
            int x = Math.Min(_viewport.Width - 1, (int)(screen.X * _scaleX)) - _region.Left;
            int y = Math.Max(0, _viewport.Height - 1 - (int)(screen.Y * _scaleY)) - _region.Top;
            if (x < 0 || x >= _region.Width || y < 0 || y >= _region.Height) return true;
            int right = Math.Min(_region.Width - 1, x + 1), top = Math.Min(_region.Height - 1, y + 1);
            for (int row = Math.Max(0, y - 1); row <= top; row++)
                for (int col = Math.Max(0, x - 1); col <= right; col++)
                {
                    float pixel = _pixels[row * _region.Width + col];
                    if ((_reversed ? pixel <= cutoff : pixel >= cutoff) ||
                        point && Math.Abs(pixel - spriteDepth) <= epsilon) return true;
                }
            return false;
        }

        private const uint DepthComponent = 0x1902, Float = 0x1406, ScissorTest = 0x0C11;
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);
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
            private const int MaxContexts = 16;
            private const uint Draw = 0x8CA9, ReadTarget = 0x8CA8, Renderbuffer = 0x8D41, PixelPackBuffer = 0x88EB;
            private const uint DepthAttachment = 0x8D00, DepthStencilAttachment = 0x821A;
            private const int LargeResolvePixels = 4 * 1024 * 1024;
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
            private delegate void BufferParameter(uint target, uint parameter, out int value);
            private delegate void TextureParameter(int name, int level, uint parameter, out int value);
            private delegate void Blit(int x0, int y0, int x1, int y1, int dx0, int dy0, int dx1, int dy1, uint mask, uint filter);
            private delegate byte IsEnabledIndexed(uint capability, uint index);
            private delegate void SetEnabledIndexed(uint capability, uint index);
            private delegate IntPtr GetStringIndexed(uint name, uint index);

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
            private readonly BufferParameter _renderbuffer = Load<BufferParameter>("glGetRenderbufferParameteriv");
            private readonly TextureParameter? _texture = TryLoad<TextureParameter>("glGetTextureLevelParameteriv");
            private readonly Blit _blit = Load<Blit>("glBlitFramebuffer");
            private readonly IsEnabledIndexed _enabled = Load<IsEnabledIndexed>("glIsEnabledi");
            private readonly SetEnabledIndexed _enable = Load<SetEnabledIndexed>("glEnablei");
            private readonly SetEnabledIndexed _disable = Load<SetEnabledIndexed>("glDisablei");
            private readonly int[] _pack = new int[PackSettings.Length];
            internal readonly bool HasClipControl = SupportsClipControl();
            private int _targetFramebuffer, _targetBuffer;
            private Size _targetSize;
            private uint _targetFormat;
            private long _lastUse;
            private static readonly object ContextLock = new object();
            private static Dictionary<IntPtr, Framebuffers>? _contexts;
            private static long _use;

            private static T Load<T>(string name) where T : Delegate => TryLoad<T>(name) ?? throw new NotSupportedException(name);

            private static T? TryLoad<T>(string name) where T : Delegate
            {
                var address = wglGetProcAddress(name);
                if (address.ToInt64() is 0 or 1 or 2 or 3 or -1) return null;
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }

            private static bool SupportsClipControl()
            {
                glGetInteger(0x821B /* GL_MAJOR_VERSION */, out int major);
                glGetInteger(0x821C /* GL_MINOR_VERSION */, out int minor);
                if (major > 4 || major == 4 && minor >= 5) return true;
                var getString = TryLoad<GetStringIndexed>("glGetStringi");
                if (getString == null) return false;
                glGetInteger(0x821D /* GL_NUM_EXTENSIONS */, out int count);
                for (uint i = 0; i < count; i++)
                    if (Marshal.PtrToStringAnsi(getString(0x1F03 /* GL_EXTENSIONS */, i)) == "GL_ARB_clip_control") return true;
                return false;
            }

            internal static Framebuffers? Current()
            {
                var context = wglGetCurrentContext();
                if (context == IntPtr.Zero) return null;
                lock (ContextLock)
                {
                    var contexts = _contexts ??= new Dictionary<IntPtr, Framebuffers>();
                    if (!contexts.TryGetValue(context, out var gl))
                    {
                        if (contexts.Count == MaxContexts)
                        {
                            IntPtr oldest = IntPtr.Zero;
                            long lastUse = long.MaxValue;
                            foreach (var item in contexts)
                                if (item.Value._lastUse < lastUse)
                                { oldest = item.Key; lastUse = item.Value._lastUse; }
                            // Never delete names belonging to another context.
                            contexts.Remove(oldest);
                        }
                        gl = new Framebuffers();
                        contexts.Add(context, gl);
                    }
                    gl._lastUse = ++_use;
                    return gl;
                }
            }

            internal static void ReleaseCurrentTarget()
            {
                // Idle often has no current context. Keep the other entries so
                // the next draw can safely reuse their targets.
                Framebuffers? gl;
                lock (ContextLock)
                    if (_contexts == null || !_contexts.TryGetValue(wglGetCurrentContext(), out gl)) return;
                if (gl.ValidateTarget(out _)) gl.DeleteTarget();
            }

            private void DeleteTarget()
            {
                if (_targetFramebuffer != 0) _deleteFramebuffers(1, ref _targetFramebuffer);
                if (_targetBuffer != 0) _deleteRenderbuffers(1, ref _targetBuffer);
                _targetFramebuffer = _targetBuffer = 0;
            }

            private bool ValidateTarget(out bool error)
            {
                error = false;
                if (_targetFramebuffer == 0) return false;
                bool valid = false;
                if (_isFramebuffer(_targetFramebuffer) != 0)
                {
                    glGetInteger(0x8CA6 /* GL_DRAW_FRAMEBUFFER_BINDING */, out int previousDraw);
                    glGetInteger(0x8CA7 /* GL_RENDERBUFFER_BINDING */, out int previousBuffer);
                    try
                    {
                        _bindFramebuffer(Draw, _targetFramebuffer);
                        _attachment(Draw, DepthAttachment, 0x8CD0 /* OBJECT_TYPE */, out int kind);
                        if (kind == Renderbuffer)
                        {
                            _attachment(Draw, DepthAttachment, 0x8CD1 /* OBJECT_NAME */, out int name);
                            if (name == _targetBuffer)
                            {
                                _bindRenderbuffer(Renderbuffer, name);
                                _renderbuffer(Renderbuffer, 0x8D42 /* WIDTH */, out int width);
                                _renderbuffer(Renderbuffer, 0x8D43 /* HEIGHT */, out int height);
                                _renderbuffer(Renderbuffer, 0x8D44 /* INTERNAL_FORMAT */, out int format);
                                valid = width == _targetSize.Width && height == _targetSize.Height && format == _targetFormat;
                            }
                        }
                    }
                    finally
                    {
                        _bindRenderbuffer(Renderbuffer, previousBuffer);
                        _bindFramebuffer(Draw, previousDraw);
                    }
                }
                error = glGetError() != 0;
                if (valid) return !error;
                // A recycled HGLRC can refer to unrelated names. Never delete
                // a target once its identity checks fail.
                _targetFramebuffer = _targetBuffer = 0;
                return false;
            }

            internal bool ClipSource(ref Rectangle source, int draw, Size frameSize, bool resolve, int[] tile,
                out uint format, out int depth)
            {
                int width = frameSize.Width, height = frameSize.Height;
                format = 0;
                uint attachment = draw == 0 ? 0x1801 /* GL_DEPTH */ : DepthAttachment;
                _attachment(Draw, attachment, 0x8216 /* DEPTH_SIZE */, out depth);
                bool valid = depth > 0;
                if (draw != 0)
                {
                    _attachment(Draw, DepthAttachment, 0x8CD0 /* OBJECT_TYPE */, out int kind);
                    if (kind == Renderbuffer)
                    {
                        _attachment(Draw, DepthAttachment, 0x8CD1 /* OBJECT_NAME */, out int name);
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
                        _attachment(Draw, DepthAttachment, 0x8CD1 /* OBJECT_NAME */, out int name);
                        _attachment(Draw, DepthAttachment, 0x8CD2 /* TEXTURE_LEVEL */, out int level);
                        _texture(name, level, 0x1000 /* WIDTH */, out width);
                        _texture(name, level, 0x1001 /* HEIGHT */, out height);
                    }
                    else valid = false;

                    if (resolve && valid)
                    {
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
                        valid = format != 0;
                    }
                }

                source.Intersect(new Rectangle(0, 0, width, height));
                // A detail or capture tile can constrain rendering further.
                if (_enabled(ScissorTest, 0) != 0)
                {
                    glGetIntegerv(0x0C10 /* GL_SCISSOR_BOX, index zero */, tile);
                    source.Intersect(new Rectangle(tile[0], tile[1], tile[2], tile[3]));
                }
                uint error = glGetError();
                return error == 0 && valid && source.Width > 0 && source.Height > 0;
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
                bool scissor = _enabled(ScissorTest, 0) != 0;
                bool large = (long)source.Width * source.Height >= LargeResolvePixels;
                bool retained = _targetFormat == format && source.Width <= _targetSize.Width && source.Height <= _targetSize.Height;
                bool validTarget = false;
                try
                {
                    if (large || retained)
                    {
                        validTarget = ValidateTarget(out bool error);
                        if (error) return false;
                    }
                    retained &= validTarget;
                    if (retained) framebuffer = _targetFramebuffer;
                    else
                    {
                        // Allocating a full 4K resolve cost over 3 ms. Only
                        // retain a target for reads large enough to benefit.
                        if (large && validTarget) DeleteTarget();
                        _genFramebuffers(1, out framebuffer);
                        _genRenderbuffers(1, out buffer);
                        if (framebuffer == 0 || buffer == 0)
                        {
                            glGetError();
                            return false;
                        }
                        _bindRenderbuffer(Renderbuffer, buffer);
                        _storage(Renderbuffer, format, source.Width, source.Height);
                        _bindFramebuffer(Draw, framebuffer);
                        _bindFramebuffer(ReadTarget, framebuffer);
                        _attach(Draw, format == 0x88F0 || format == 0x8CAD ? DepthStencilAttachment : DepthAttachment, Renderbuffer, buffer);
                        glDrawBuffer(0 /* GL_NONE */);
                        glReadBuffer(0 /* GL_NONE */);
                        uint status = _status(Draw);
                        uint error = glGetError();
                        if (status != 0x8CD5 /* GL_FRAMEBUFFER_COMPLETE */ || error != 0) return false;
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
