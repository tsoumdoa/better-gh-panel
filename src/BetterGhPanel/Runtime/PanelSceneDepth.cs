using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Rhino.Display;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // Read the live scene once, after Grasshopper's preview callbacks and before
    // the foreground overlays. ZBufferCapture would start another display pass.
    internal sealed class PanelSceneDepth
    {
        [ThreadStatic] private static PanelSceneDepth? _frame;
        private DisplayPipeline? _pipeline;
        private Guid _viewport;
        private bool _valid;
        private float[] _pixels = Array.Empty<float>();
        private readonly int[] _port = new int[4];
        private readonly double[] _range = new double[2];
        private int _width, _height;
        private double _scaleX, _scaleY, _z, _w, _zOffset, _wOffset, _clear;
        private bool _reversed;
        private Point3d _camera;
        private Vector3d _direction;

        internal static void BeginFrame()
        {
            if (_frame != null) _frame._pipeline = null;
        }

        internal static PanelSceneDepth? Capture(DisplayPipeline pipeline, RhinoViewport viewport, Size screenSize)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !pipeline.IsOpenGL) return null;
            var frame = _frame ??= new PanelSceneDepth();
            if (!ReferenceEquals(frame._pipeline, pipeline) || frame._viewport != viewport.Id)
            {
                frame._pipeline = pipeline;
                frame._viewport = viewport.Id;
                frame._valid = frame.Read(pipeline, viewport, screenSize);
            }
            return frame._valid ? frame : null;
        }

        private bool Read(DisplayPipeline pipeline, RhinoViewport viewport, Size screenSize)
        {
            if (wglGetCurrentContext() == IntPtr.Zero) return false;
            pipeline.Flush();
            glGetIntegerv(0x0BA2 /* GL_VIEWPORT */, _port);
            _width = _port[2]; _height = _port[3];
            if (_width <= 0 || _height <= 0 || screenSize.Width <= 0 || screenSize.Height <= 0) return false;
            _scaleX = (double)_width / screenSize.Width;
            _scaleY = (double)_height / screenSize.Height;
            int count = checked(_width * _height);
            if (_pixels.Length < count) _pixels = new float[count];

            glGetInteger(0x8CA6 /* GL_DRAW_FRAMEBUFFER_BINDING */, out int draw);
            glGetInteger(0x8CAA /* GL_READ_FRAMEBUFFER_BINDING */, out int read);
            glGetInteger(0x80A9 /* GL_SAMPLES */, out int samples);
            _pixels[0] = float.NaN;
            if (draw != read || draw != 0 && samples > 0)
            {
                var framebuffers = Framebuffers.Get();
                if (framebuffers == null) return false;
                framebuffers.Read(draw, read, samples, _port, _pixels);
            }
            else glReadPixels(_port[0], _port[1], _width, _height, DepthComponent, Float, _pixels);
            if (float.IsNaN(_pixels[0])) return false;

            // OpenGL's projection is column-major, with forward camera depth
            // along negative Z. Compare in the same depth range the GPU wrote.
            var projection = pipeline.GetOpenGLCameraToClip();
            glGetDoublev(0x0B70 /* GL_DEPTH_RANGE */, _range);
            glGetDouble(0x0B73 /* GL_DEPTH_CLEAR_VALUE */, out _clear);
            _z = -projection[10]; _w = -projection[11];
            _zOffset = projection[14]; _wOffset = projection[15];
            _reversed = (_z * _wOffset - _w * _zOffset) * (_range[1] - _range[0]) < 0;
            _camera = viewport.CameraLocation;
            _direction = viewport.CameraDirection;
            return _direction.Unitize() && (_w != 0 || _wOffset != 0);
        }

        internal bool IsVisible(Point2d screen, Point3d anchor)
        {
            // Move the comparison point forward by the depth tolerance. This
            // keeps the test in camera units even with non-linear perspective Z.
            double depth = ((anchor - _camera) * _direction - .05) / 1.01;
            if (depth <= 0) return true;
            double clip = (_z * depth + _zOffset) / (_w * depth + _wOffset);
            double cutoff = _range[0] + (clip + 1) * .5 * (_range[1] - _range[0]);
            int x = Math.Min(_width - 1, (int)(screen.X * _scaleX));
            int y = Math.Max(0, _height - 1 - (int)(screen.Y * _scaleY));
            // A clear or farther pixel in the 3x3 neighbourhood keeps the dot
            // visible, including anchors on silhouettes and sloped surfaces.
            int right = Math.Min(_width - 1, x + 1), top = Math.Min(_height - 1, y + 1);
            for (int row = Math.Max(0, y - 1); row <= top; row++)
                for (int col = Math.Max(0, x - 1); col <= right; col++)
                {
                    float pixel = _pixels[row * _width + col];
                    if (pixel == _clear || (_reversed ? pixel <= cutoff : pixel >= cutoff)) return true;
                }
            return false;
        }

        private const uint DepthComponent = 0x1902, Float = 0x1406;
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);
        [DllImport("opengl32.dll")] private static extern void glGetIntegerv(uint name, [Out] int[] values);
        [DllImport("opengl32.dll", EntryPoint = "glGetIntegerv")] private static extern void glGetInteger(uint name, out int value);
        [DllImport("opengl32.dll")] private static extern void glGetDoublev(uint name, [Out] double[] values);
        [DllImport("opengl32.dll", EntryPoint = "glGetDoublev")] private static extern void glGetDouble(uint name, out double value);
        [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, [Out] float[] pixels);
        [DllImport("opengl32.dll")] private static extern void glReadBuffer(uint mode);
        [DllImport("opengl32.dll")] private static extern void glDrawBuffer(uint mode);

        private sealed class Framebuffers
        {
            // GL forbids glReadPixels on multisampled FBOs. Resolve the depth
            // attachment into a temporary, same-format, single-sample target.
            private const uint Draw = 0x8CA9, ReadTarget = 0x8CA8, Renderbuffer = 0x8D41;
            private const uint DepthAttachment = 0x8D00, DepthStencilAttachment = 0x821A;
            private delegate void Bind(uint target, int name);
            private delegate void Create(int count, out int name);
            private delegate void Delete(int count, ref int name);
            private delegate void Storage(uint target, uint format, int width, int height);
            private delegate void Attach(uint target, uint attachment, uint renderbufferTarget, int name);
            private delegate uint Status(uint target);
            private delegate void AttachmentParameter(uint target, uint attachment, uint parameter, out int value);
            private delegate void Blit(int x0, int y0, int x1, int y1, int dx0, int dy0, int dx1, int dy1, uint mask, uint filter);
            private readonly Bind _bindFramebuffer = Load<Bind>("glBindFramebuffer");
            private readonly Bind _bindRenderbuffer = Load<Bind>("glBindRenderbuffer");
            private readonly Create _genFramebuffers = Load<Create>("glGenFramebuffers");
            private readonly Create _genRenderbuffers = Load<Create>("glGenRenderbuffers");
            private readonly Delete _deleteFramebuffers = Load<Delete>("glDeleteFramebuffers");
            private readonly Delete _deleteRenderbuffers = Load<Delete>("glDeleteRenderbuffers");
            private readonly Storage _storage = Load<Storage>("glRenderbufferStorage");
            private readonly Attach _attach = Load<Attach>("glFramebufferRenderbuffer");
            private readonly Status _status = Load<Status>("glCheckFramebufferStatus");
            private readonly AttachmentParameter _attachment = Load<AttachmentParameter>("glGetFramebufferAttachmentParameteriv");
            private readonly Blit _blit = Load<Blit>("glBlitFramebuffer");
            private static Framebuffers? _instance;

            private static T Load<T>(string name) where T : Delegate
            {
                var address = wglGetProcAddress(name);
                if (address.ToInt64() is 0 or 1 or 2 or 3 or -1) throw new NotSupportedException(name);
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }

            internal static Framebuffers? Get()
            {
                try { return _instance ??= new Framebuffers(); }
                catch (NotSupportedException) { return null; }
            }

            internal void Read(int draw, int read, int samples, int[] port, float[] pixels)
            {
                int x = port[0], y = port[1], width = port[2], height = port[3];
                int framebuffer = 0, buffer = 0;
                glGetInteger(0x8CA7 /* GL_RENDERBUFFER_BINDING */, out int previousBuffer);
                try
                {
                    _bindFramebuffer(ReadTarget, draw);
                    if (draw != 0 && samples > 0)
                    {
                        glGetInteger(0x0D56 /* GL_DEPTH_BITS */, out int bits);
                        glGetInteger(0x0D57 /* GL_STENCIL_BITS */, out int stencil);
                        if (bits == 0) return;
                        _attachment(Draw, DepthAttachment, 0x8211 /* GL_FRAMEBUFFER_ATTACHMENT_COMPONENT_TYPE */, out int type);
                        uint format = stencil > 0 ? (type == Float ? 0x8CADu : 0x88F0u) :
                            type == Float ? 0x8CACu : bits == 16 ? 0x81A5u : bits == 24 ? 0x81A6u : 0x81A7u;
                        _genFramebuffers(1, out framebuffer);
                        _genRenderbuffers(1, out buffer);
                        _bindRenderbuffer(Renderbuffer, buffer);
                        _storage(Renderbuffer, format, width, height);
                        _bindFramebuffer(Draw, framebuffer);
                        _bindFramebuffer(ReadTarget, framebuffer);
                        _attach(Draw, stencil > 0 ? DepthStencilAttachment : DepthAttachment, Renderbuffer, buffer);
                        glDrawBuffer(0 /* GL_NONE */);
                        glReadBuffer(0 /* GL_NONE */);
                        if (_status(Draw) != 0x8CD5 /* GL_FRAMEBUFFER_COMPLETE */) return;
                        _bindFramebuffer(ReadTarget, draw);
                        _blit(x, y, x + width, y + height, 0, 0, width, height, 0x0100 /* GL_DEPTH_BUFFER_BIT */, 0x2600 /* GL_NEAREST */);
                        _bindFramebuffer(ReadTarget, framebuffer);
                        x = y = 0;
                    }
                    glReadPixels(x, y, width, height, DepthComponent, Float, pixels);
                }
                finally
                {
                    _bindFramebuffer(ReadTarget, read);
                    if (framebuffer != 0)
                    {
                        _bindFramebuffer(Draw, draw);
                        _deleteFramebuffers(1, ref framebuffer);
                    }
                    if (buffer != 0)
                    {
                        _bindRenderbuffer(Renderbuffer, previousBuffer);
                        _deleteRenderbuffers(1, ref buffer);
                    }
                }
            }
        }
    }
}
