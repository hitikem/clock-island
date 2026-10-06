using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace PomodoroIsland;

/// <summary>
/// 会动的沙漏（岛和卡片共用）。目标就是"像真的在漏沙"：
///
///   · 玻璃轮廓是贝塞尔曲线，带通透渐变 + 一道高光
///   · **上罩沙面塌成漏斗形凹陷**（中间先漏空，越到后面凹陷越深）
///   · **中间一道沙流**从收口连到下面的沙堆锥顶
///   · **下罩堆成锥形沙堆**（中间高、两边平），不是平平地往上涨
///   · 沙粒**飘落**：重力加速 + 左右飘 + 自身翻滚
///   · 跑着时每 1 秒整体翻转 180°
///
/// 沙面/沙堆的形状每帧重算（形状很小，开销可忽略）；画在 14×20 坐标系里，
/// 外面套 Viewbox 缩放 —— 矢量图形，任何尺寸都高清。
/// 全部逐帧改属性：插件里禁止 Storyboard（属性路径动画会抛 0x800F1001）。
/// </summary>
public sealed class HourglassView : UserControl
{
    private const double CanvasWidth = 14;
    private const double CanvasHeight = 20;
    private const double NeckY = 9.8;        // 收口高度
    private const double BulbTopY = 3.6;     // 上罩下沿
    private const double BulbBottomY = 16.4; // 下罩上沿
    private const int GrainCount = 16;       // 粒子多才像"沙"

    /// <summary>每秒掉几趟（越大越快）。0.24 秒一趟，沙子看着是"哗哗往下淌"。</summary>
    private const double GrainFallSpeed = 4.2;

    /// <summary>一个循环里"漏完"占多久（秒）——沙子从上面全部落到下面。</summary>
    private const double DrainSeconds = 1.55;

    /// <summary>漏完之后翻转占多久（秒）。</summary>
    private const double FlipSeconds = 0.45;

    private readonly Viewbox _box;
    private readonly RectangleGeometry _sandTopClip = new();
    private readonly RectangleGeometry _sandBottomClip = new();
    private readonly XamlPath _dip;      // 上罩沙面中间的漏斗凹陷（用玻璃色"掏"出来）
    private readonly XamlPath _pile;     // 下罩沙堆的锥顶
    private readonly Rectangle _stream;
    private readonly Ellipse[] _grains = new Ellipse[GrainCount];
    private readonly RotateTransform _flip = new();
    private readonly LinearGradientBrush _sandBrush = new() { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
    private readonly GradientStop _sandLight = new();
    private readonly GradientStop _sandDeep = new();
    private readonly LinearGradientBrush _glassBrush = new() { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
    private readonly GradientStop _glassLight = new();
    private readonly GradientStop _glassDeep = new();
    private readonly SolidColorBrush _strokeBrush = new();
    private readonly SolidColorBrush _highlightBrush = new();

    private readonly DispatcherQueueTimer? _timer;
    private readonly DateTimeOffset _born = DateTimeOffset.UtcNow;

    private double _targetProgress;
    private double _pilePeakY = BulbBottomY;
    private bool _running;
    private bool _wasRunning;
    private DateTimeOffset _cycleStart = DateTimeOffset.UtcNow;

    public HourglassView()
    {
        _sandBrush.GradientStops.Add(_sandLight);
        _sandBrush.GradientStops.Add(_sandDeep);
        _glassBrush.GradientStops.Add(_glassLight);
        _glassBrush.GradientStops.Add(_glassDeep);
        ApplyAccent(PomodoroUi.CountdownAccent(false));

        var canvas = new Canvas { Width = CanvasWidth, Height = CanvasHeight };

        canvas.Children.Add(new XamlPath
        {
            Data = GlassGeometry(),
            Fill = _glassBrush,
            Stroke = _strokeBrush,
            StrokeThickness = 0.5,          // 细描边才像玻璃，粗了像粗线图标
            StrokeLineJoin = PenLineJoin.Round,
        });

        canvas.Children.Add(new XamlPath
        {
            Data = HighlightGeometry(),
            Stroke = _highlightBrush,
            StrokeThickness = 0.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

        // 上罩沙子：整块内腔 + 矩形裁切定水位
        canvas.Children.Add(new XamlPath
        {
            Data = TopBulbGeometry(),
            Fill = _sandBrush,
            Clip = _sandTopClip,
        });

        // 上罩沙面的漏斗凹陷（玻璃色盖在沙面上，看着就是"中间塌下去"）
        _dip = new XamlPath { Fill = _glassBrush };
        canvas.Children.Add(_dip);

        // 下罩沙子：整块内腔 + 矩形裁切定堆高
        canvas.Children.Add(new XamlPath
        {
            Data = BottomBulbGeometry(),
            Fill = _sandBrush,
            Clip = _sandBottomClip,
        });

        // 下罩沙堆的锥顶（和下面的沙子同色，自然连成一座小山）
        _pile = new XamlPath { Fill = _sandBrush };
        canvas.Children.Add(_pile);

        // 沙流：从收口连到锥顶
        _stream = new Rectangle
        {
            Width = 1.0,
            RadiusX = 0.5,
            RadiusY = 0.5,
            Fill = _sandBrush,
            Opacity = 0.9,
        };
        Canvas.SetLeft(_stream, (CanvasWidth - 1.0) / 2);
        canvas.Children.Add(_stream);

        // 飘落的沙粒：小、多、大小不一；扁的 + 自带旋转，落下来是在翻滚
        var random = new Random(20261007);           // 固定种子：每次启动的沙粒分布一致
        for (var i = 0; i < GrainCount; i++)
        {
            var size = 0.45 + random.NextDouble() * 0.55;   // 0.45 ~ 1.0
            _grains[i] = new Ellipse
            {
                Width = size,
                Height = size * 0.66,
                Fill = _sandBrush,
                Opacity = 0,
                RenderTransform = new RotateTransform(),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            Canvas.SetLeft(_grains[i], (CanvasWidth - size) / 2);
            canvas.Children.Add(_grains[i]);
        }

        _box = new Viewbox
        {
            Child = canvas,
            Width = 20,
            Height = 20 * CanvasHeight / CanvasWidth,
            RenderTransform = _flip,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        Content = _box;

        _timer = DispatcherQueue?.CreateTimer();
        if (_timer is not null)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => OnTick();
            _timer.Start();     // 沙面 / 沙堆形状一直在动，常驻跑
        }

        Unloaded += (_, _) => _timer?.Stop();
        // 宿主隐藏 / 换掉岛内容时控件会 Unloaded（定时器被停），重新挂上可视树必须再拉起来，
        // 否则沙子会停在最后一帧、翻转也不再动。
        Loaded += (_, _) => _timer?.Start();
        ApplyProgress(0);
    }

    /// <summary>显示宽度（高度按沙漏比例自动算）。</summary>
    public double BoxWidth
    {
        set
        {
            _box.Width = Math.Max(0, value);
            _box.Height = Math.Max(0, value * CanvasHeight / CanvasWidth);
        }
    }

    /// <summary>主色（沙子与玻璃描边）。</summary>
    public Windows.UI.Color Accent
    {
        get => _strokeBrush.Color;
        set => ApplyAccent(value);
    }

    /// <summary>插件每秒调一次：0 = 沙全在上面，1 = 全漏到下面。</summary>
    public void SetState(double progress, bool running)
    {
        _targetProgress = Math.Clamp(progress, 0, 1);

        // 刚开跑：从"沙全在上面"重新起一个循环
        if (running && !_wasRunning) _cycleStart = DateTimeOffset.UtcNow;
        _wasRunning = running;
        _running = running;

        // 兜底：定时器可能被 Unloaded 停过，每次收到状态就确保它在跑
        _timer?.Start();
    }

    private void ApplyAccent(Windows.UI.Color accent)
    {
        _strokeBrush.Color = accent;
        _sandDeep.Color = accent;
        _sandLight.Color = Lighten(accent, 0.42);
        _glassLight.Color = WithAlpha(accent, 52);
        _glassDeep.Color = WithAlpha(accent, 12);
        _highlightBrush.Color = WithAlpha(Lighten(accent, 0.75), 150);
    }

    private void OnTick()
    {
        var now = DateTimeOffset.UtcNow;
        var seconds = (now - _born).TotalSeconds;

        double shown;
        double angle;

        if (_running)
        {
            // 循环：漏（沙子从上全部落到下）→ 漏完才翻 → 翻过来沙又在上，接着漏
            var t = (now - _cycleStart).TotalSeconds;

            if (t < DrainSeconds)
            {
                shown = t / DrainSeconds;                       // 一直在漏，中间绝不翻
                angle = 0;
            }
            else if (t < DrainSeconds + FlipSeconds)
            {
                shown = 1;                                      // 沙已经全在下面了，此刻才翻
                angle = 180 * EaseInOut((t - DrainSeconds) / FlipSeconds);
            }
            else
            {
                // 翻完了：角度归零 + 水位归零是"同一幅画面"（沙都在上面），接缝看不见
                _cycleStart = now;
                shown = 0;
                angle = 0;
            }
        }
        else
        {
            // 没在跑：停在真实进度上（暂停/空闲时沙位就是实际剩余量）
            shown = _targetProgress;
            angle = 0;
        }

        _flip.Angle = angle;
        ApplyProgress(shown);

        // 沙快漏完时，落沙自然变稀
        var flowing = _running && shown < 0.94;

        var streamTop = NeckY - 0.2;
        _stream.Height = Math.Max(0.6, _pilePeakY - streamTop);
        _stream.Opacity = flowing ? 0.55 + 0.35 * (0.5 + 0.5 * Math.Sin(seconds * 9)) : 0;

        if (flowing)
        {
            for (var i = 0; i < GrainCount; i++)
            {
                var phase = (seconds * GrainFallSpeed + i / (double)GrainCount) % 1.0;
                var size = _grains[i].Width;
                var fall = Math.Pow(phase, 1.7);                        // 重力加速：刚离口慢，越落越快
                var y = NeckY + 0.15 + fall * Math.Max(0.4, _pilePeakY - NeckY - size);

                // 越往下越散开：收口处是一股细流，落下来散成颗粒
                var spread = 0.25 + 1.15 * phase;
                var sway = spread * Math.Sin(phase * 6.2 + i * 2.1);

                Canvas.SetTop(_grains[i], y);
                Canvas.SetLeft(_grains[i], (CanvasWidth - size) / 2 + sway);

                if (_grains[i].RenderTransform is RotateTransform spin) spin.Angle = seconds * 430 + i * 47;
                _grains[i].Opacity = 0.95 * Math.Min(1, phase * 6) * (1 - phase * 0.7);
            }
        }
        else
        {
            foreach (var grain in _grains) grain.Opacity = 0;
        }
    }

    private void ApplyProgress(double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        var travel = NeckY - BulbTopY;                 // 约 6.2

        // ---- 上罩：水位下沉 + 中间凹陷 ----
        var surface = BulbTopY + 0.25 + travel * p;
        _sandTopClip.Rect = new Rect(0, surface, CanvasWidth, Math.Max(0, CanvasHeight - surface));

        var dip = 0.35 + 1.5 * p;
        var linkX = WallX(surface);                    // 该高度处玻璃内壁的横坐标

        var dipFigure = new PathFigure { StartPoint = new Point(linkX, surface) };
        dipFigure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(linkX + (7 - linkX) * 0.45, surface + dip * 0.55),
            Point2 = new Point(7, surface + dip),
            Point3 = new Point(7, surface + dip),
        });
        dipFigure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(7, surface + dip),
            Point2 = new Point(CanvasWidth - linkX - (7 - linkX) * 0.45, surface + dip * 0.55),
            Point3 = new Point(CanvasWidth - linkX, surface),
        });
        dipFigure.Segments.Add(new LineSegment { Point = new Point(linkX, surface) });
        dipFigure.IsClosed = true;
        dipFigure.IsFilled = true;

        var dipGeometry = new PathGeometry();
        dipGeometry.Figures.Add(dipFigure);
        _dip.Data = dipGeometry;

        // ---- 下罩：堆高上升 + 中间锥顶 ----
        var pileEdge = BulbBottomY - travel * p;
        _sandBottomClip.Rect = new Rect(0, pileEdge, CanvasWidth, Math.Max(0, CanvasHeight - pileEdge));

        var peakHeight = 0.4 + 1.7 * p;
        var peak = pileEdge - peakHeight;
        _pilePeakY = p <= 0.002 ? BulbBottomY : peak;

        var edgeX = WallX(pileEdge, top: false);
        var pileFigure = new PathFigure { StartPoint = new Point(edgeX, pileEdge + 0.5) };
        pileFigure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(edgeX + (7 - edgeX) * 0.4, peak + peakHeight * 0.25),
            Point2 = new Point(7, peak),
            Point3 = new Point(7, peak),
        });
        pileFigure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(7, peak),
            Point2 = new Point(CanvasWidth - edgeX - (7 - edgeX) * 0.4, peak + peakHeight * 0.25),
            Point3 = new Point(CanvasWidth - edgeX, pileEdge + 0.5),
        });
        pileFigure.Segments.Add(new LineSegment { Point = new Point(edgeX, pileEdge + 0.5) });
        pileFigure.IsClosed = true;
        pileFigure.IsFilled = true;

        var pileGeometry = new PathGeometry();
        pileGeometry.Figures.Add(pileFigure);
        _pile.Data = pileGeometry;
    }

    /// <summary>玻璃内壁在某个高度处的横坐标（左边那条），用线性近似足够。</summary>
    private static double WallX(double y, bool top = true)
    {
        var t = top
            ? (y - BulbTopY) / (NeckY - BulbTopY)
            : (y - NeckY) / (BulbBottomY - NeckY);
        t = Math.Clamp(t, 0, 1);

        // 上罩：3.6 处约 x=1.7，收口处 x=7；下罩：收口 x=7，16.4 处约 x=1.7
        return top ? 1.7 + 5.3 * t : 7.0 - 5.3 * t;
    }

    // ---- 几何 ----

    private static PathGeometry GlassGeometry()
    {
        var figure = new PathFigure { StartPoint = new Point(0.6, 1.2), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = new Point(13.4, 1.2) });
        figure.Segments.Add(new LineSegment { Point = new Point(11.9, 3.6) });
        figure.Segments.Add(new BezierSegment { Point1 = new Point(11.0, 6.3), Point2 = new Point(8.6, 8.3), Point3 = new Point(7.0, NeckY) });
        figure.Segments.Add(new BezierSegment { Point1 = new Point(5.4, 11.3), Point2 = new Point(3.0, 13.3), Point3 = new Point(2.1, 16.4) });
        figure.Segments.Add(new LineSegment { Point = new Point(0.6, 18.8) });
        figure.Segments.Add(new LineSegment { Point = new Point(13.4, 18.8) });
        figure.Segments.Add(new LineSegment { Point = new Point(11.9, 16.4) });
        figure.Segments.Add(new BezierSegment { Point1 = new Point(11.0, 13.3), Point2 = new Point(8.6, 11.3), Point3 = new Point(7.0, NeckY) });
        figure.Segments.Add(new BezierSegment { Point1 = new Point(5.4, 8.3), Point2 = new Point(3.0, 6.3), Point3 = new Point(2.1, 3.6) });
        figure.Segments.Add(new LineSegment { Point = new Point(0.6, 1.2) });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static PathGeometry HighlightGeometry()
    {
        var figure = new PathFigure { StartPoint = new Point(3.5, 4.8), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = new Point(4.0, 6.6), Point2 = new Point(5.2, 8.0), Point3 = new Point(6.0, 9.3) });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static PathGeometry TopBulbGeometry()
    {
        var figure = new PathFigure { StartPoint = new Point(1.6, BulbTopY), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = new Point(12.4, BulbTopY) });
        figure.Segments.Add(new BezierSegment { Point1 = new Point(11.2, 6.4), Point2 = new Point(8.6, 8.4), Point3 = new Point(7.0, NeckY - 0.2) });
        figure.Segments.Add(new BezierSegment { Point1 = new Point(5.4, 8.4), Point2 = new Point(2.8, 6.4), Point3 = new Point(1.6, BulbTopY) });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static PathGeometry BottomBulbGeometry()
    {
        var figure = new PathFigure { StartPoint = new Point(7.0, NeckY + 0.2), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new BezierSegment { Point1 = new Point(8.6, 11.2), Point2 = new Point(11.2, 13.2), Point3 = new Point(12.4, BulbBottomY) });
        figure.Segments.Add(new LineSegment { Point = new Point(1.6, BulbBottomY) });
        figure.Segments.Add(new BezierSegment { Point1 = new Point(2.8, 13.2), Point2 = new Point(5.4, 11.2), Point3 = new Point(7.0, NeckY + 0.2) });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static double EaseInOut(double t) => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) =>
        Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Windows.UI.Color Lighten(Windows.UI.Color color, double amount)
    {
        byte Mix(byte value) => (byte)Math.Clamp(value + (255 - value) * amount, 0, 255);
        return Windows.UI.Color.FromArgb(color.A, Mix(color.R), Mix(color.G), Mix(color.B));
    }
}
