using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SoundRadar.Core;
using SoundRadar.Interop;

namespace SoundRadar.Overlay
{
    /// <summary>
    /// A click-through, always-on-top bar on the left or right screen edge. It's a raw
    /// layered window (no taskbar/Alt-Tab entry, never takes focus) that we paint with GDI+
    /// into a premultiplied-alpha bitmap and push with UpdateLayeredWindow.
    /// </summary>
    internal sealed class BarWindow : IDisposable
    {
        private const string ClassName = "SoundRadarBar";
        private const int SmoothGradientStops = 16;
        private static Native.WndProc _wndProc; // must outlive every window of the class
        private static bool _classRegistered;

        private IntPtr _hwnd;
        private Rectangle _outer; // window rect in screen pixels (bar + background padding)
        private RectangleF _bar; // bar rect inside the window
        private double _scale = 1;
        private IntPtr _memDc, _dib, _oldBitmap;
        private Bitmap _bitmap;
        private Graphics _graphics;

        public BarWindow(bool left)
        {
            IsLeft = left;
            EnsureClass();
            _hwnd = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
                ClassName, left ? "SoundRadar left bar" : "SoundRadar right bar", Native.WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new Win32Exception();
        }

        public bool IsLeft { get; }
        public bool Visible { get; private set; }

        public double Level; // 0-1
        public string ColorName = "Rainbow";
        public Rgb? SolidColor; // overrides the gradient (Frequency scheme)
        public string Mode = "Bottom";
        public int Segments = 21;
        public double Opacity = 0.9;
        public double BackgroundOpacity; // dark backing so bars show on bright screens

        public Rectangle Bounds => _outer;

        private static void EnsureClass()
        {
            if (_classRegistered)
                return;
            _wndProc = (hwnd, msg, wParam, lParam) =>
                msg == Native.WM_NCHITTEST ? new IntPtr(Native.HTTRANSPARENT) : Native.DefWindowProc(hwnd, msg, wParam, lParam);
            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = _wndProc,
                hInstance = Native.GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            if (Native.RegisterClassEx(ref wc) == 0)
                throw new Win32Exception();
            _classRegistered = true;
        }

        /// <summary>Anchor to a screen edge. Width and margin are in pixels at 100% scaling.</summary>
        public void Place(Rectangle screen, int width, int lengthPercent, int margin)
        {
            _scale = Native.MonitorScale(screen);
            var barWidth = Math.Max(1, (int)Math.Round(width * _scale));
            var edgeGap = (int)Math.Round(margin * _scale);
            var length = Math.Max(20, screen.Height * lengthPercent / 100);
            var y = screen.Top + (screen.Height - length) / 2;
            var x = IsLeft ? screen.Left + edgeGap : screen.Right - edgeGap - barWidth;
            var bar = new Rectangle(x, y, barWidth, length);

            // The window extends past the bar so the background can frame it; clip to this
            // screen so the padding never spills onto a neighbouring monitor.
            var pad = Math.Max((int)Math.Round(2 * _scale), (int)Math.Round(barWidth * 0.3));
            var outer = Rectangle.Intersect(Rectangle.Inflate(bar, pad, pad), screen);
            _outer = outer;
            _bar = new RectangleF(bar.X - outer.X, bar.Y - outer.Y, bar.Width, bar.Height);
            EnsureSurface(outer.Size);
        }

        public void SetVisible(bool visible)
        {
            if (visible == Visible)
                return;
            Visible = visible;
            if (visible)
                Render(); // a layered window shows nothing until its first UpdateLayeredWindow
            Native.ShowWindow(_hwnd, visible ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
        }

        /// <summary>Re-assert topmost; some games push themselves above other topmost windows.</summary>
        public void KeepOnTop()
        {
            if (Visible)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        // ---- surface --------------------------------------------------------------------

        private void EnsureSurface(Size size)
        {
            if (_bitmap != null && _bitmap.Width == size.Width && _bitmap.Height == size.Height)
                return;
            FreeSurface();
            if (size.Width <= 0 || size.Height <= 0)
                return;
            var header = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = size.Width,
                biHeight = -size.Height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            var screenDc = Native.GetDC(IntPtr.Zero);
            try
            {
                _memDc = Native.CreateCompatibleDC(screenDc);
                _dib = Native.CreateDIBSection(screenDc, ref header, 0, out var bits, IntPtr.Zero, 0);
                _oldBitmap = Native.SelectObject(_memDc, _dib);
                // GDI+ draws straight into the DIB's memory as premultiplied ARGB, which is
                // exactly what UpdateLayeredWindow's per-pixel alpha expects.
                _bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
                _graphics = Graphics.FromImage(_bitmap);
                _graphics.SmoothingMode = SmoothingMode.AntiAlias;
                _graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void FreeSurface()
        {
            _graphics?.Dispose();
            _bitmap?.Dispose();
            _graphics = null;
            _bitmap = null;
            if (_memDc != IntPtr.Zero)
            {
                Native.SelectObject(_memDc, _oldBitmap);
                Native.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_dib != IntPtr.Zero)
            {
                Native.DeleteObject(_dib);
                _dib = IntPtr.Zero;
            }
        }

        // ---- painting -------------------------------------------------------------------

        public void Render()
        {
            if (_graphics == null)
                return;
            var g = _graphics;
            g.Clear(Color.Transparent);

            if (BackgroundOpacity > 0)
            {
                using (var brush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * BackgroundOpacity), 0, 0, 0)))
                    FillRounded(g, brush, 0, 0, _outer.Width, _outer.Height, (float)Math.Min(_outer.Width / 2.0, 5 * _scale));
            }

            if (Level > 0.002)
            {
                g.TranslateTransform(_bar.X, _bar.Y);
                PaintBar(g, _bar.Width, _bar.Height);
                g.ResetTransform();
            }
            g.Flush(FlushIntention.Sync);

            var screenDc = Native.GetDC(IntPtr.Zero);
            try
            {
                var dst = new Native.POINT(_outer.X, _outer.Y);
                var size = new Native.SIZE(_outer.Width, _outer.Height);
                var src = new Native.POINT(0, 0);
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    SourceConstantAlpha = 255,
                    AlphaFormat = Native.AC_SRC_ALPHA,
                };
                Native.UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void PaintBar(Graphics g, float width, float height)
        {
            var radius = (float)Math.Min(width / 2, 4 * _scale);
            (float origin, int direction, float length)[] runs;
            var segments = Segments;
            if (Mode == "Center")
            {
                var half = height / 2;
                runs = new[] { (half, -1, half), (half, 1, half) };
                segments = Segments > 0 ? Math.Max(1, (int)Math.Round(Segments / 2.0)) : 0;
            }
            else if (Mode == "Top")
            {
                runs = new[] { (0f, 1, height) };
            }
            else
            {
                runs = new[] { (height, -1, height) };
            }

            foreach (var (origin, direction, length) in runs)
            {
                if (segments > 0)
                    PaintSegments(g, origin, direction, length, segments, width);
                else
                    PaintSmooth(g, origin, direction, length, width, radius);
            }
        }

        /// <summary>LED-strip look: <paramref name="count"/> blocks, the top one dimmed by how far it's lit.</summary>
        private void PaintSegments(Graphics g, float origin, int direction, float length, int count, float width)
        {
            var pitch = length / count;
            var gap = (float)Math.Min(3 * _scale, Math.Max(1, pitch * 0.2));
            var lit = Level * count;
            var radius = (float)Math.Min(Math.Min(width / 2, pitch / 2), 3 * _scale);
            for (var i = 0; i < Math.Ceiling(lit); i++)
            {
                var color = ColorAt(i * 255.0 / count, Opacity * Math.Min(1, lit - i));
                if (color.A == 0)
                    continue;
                var near = origin + direction * i * pitch;
                var far = origin + direction * ((i + 1) * pitch - gap);
                using (var brush = new SolidBrush(color))
                    FillRounded(g, brush, 0, Math.Min(near, far), width, Math.Abs(far - near), radius);
            }
        }

        private void PaintSmooth(Graphics g, float origin, int direction, float length, float width, float radius)
        {
            var far = origin + (float)(direction * length * Level);
            var lit = Math.Abs(far - origin);
            if (lit < 0.5f)
                return;
            var blend = new ColorBlend(SmoothGradientStops + 1)
            {
                Colors = new Color[SmoothGradientStops + 1],
                Positions = new float[SmoothGradientStops + 1],
            };
            for (var step = 0; step <= SmoothGradientStops; step++)
            {
                var t = step / (float)SmoothGradientStops;
                blend.Positions[step] = t;
                blend.Colors[step] = ColorAt(t * 255, Opacity);
            }
            var start = new PointF(0, origin);
            var end = new PointF(0, origin + direction * length);
            using (var brush = new LinearGradientBrush(start, end, blend.Colors[0], blend.Colors[SmoothGradientStops]))
            {
                brush.InterpolationColors = blend;
                brush.WrapMode = WrapMode.TileFlipXY; // avoids a seam on the first/last pixel
                FillRounded(g, brush, 0, Math.Min(origin, far), width, lit, radius);
            }
        }

        private Color ColorAt(double pos, double alpha)
        {
            Rgb rgb;
            if (SolidColor.HasValue)
            {
                rgb = SolidColor.Value;
            }
            else
            {
                var gradient = ColorSchemes.Gradient(ColorName);
                if (gradient == null)
                    return Color.Transparent; // Frequency before its first color arrives
                rgb = gradient((int)Math.Max(0, Math.Min(255, pos)));
            }
            var a = (int)Math.Round(255 * Math.Max(0, Math.Min(1, alpha)));
            return Color.FromArgb(a, rgb.R, rgb.G, rgb.B);
        }

        private static void FillRounded(Graphics g, Brush brush, float x, float y, float width, float height, float radius)
        {
            radius = Math.Min(radius, Math.Min(width, height) / 2);
            if (radius < 0.75f)
            {
                g.FillRectangle(brush, x, y, width, height);
                return;
            }
            var d = radius * 2;
            using (var path = new GraphicsPath())
            {
                path.AddArc(x, y, d, d, 180, 90);
                path.AddArc(x + width - d, y, d, d, 270, 90);
                path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
                path.AddArc(x, y + height - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }

        public void Dispose()
        {
            FreeSurface();
            if (_hwnd != IntPtr.Zero)
            {
                Native.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
