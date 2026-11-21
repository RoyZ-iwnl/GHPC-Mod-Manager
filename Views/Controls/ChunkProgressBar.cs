using System.Windows;
using System.Windows.Media;
using GHPC_Mod_Manager.Models;

namespace GHPC_Mod_Manager.Views.Controls;

/// <summary>
/// IDM 风格分块进度条。多线程下载时显示每个分块的下载状态，单线程时退化为普通进度条。
/// 相邻同状态段合并为一条路径填充，消除缝隙。
/// </summary>
public class ChunkProgressBar : FrameworkElement
{
    public ChunkProgressBar()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(nameof(Segments), typeof(IReadOnlyList<ChunkSegmentInfo>), typeof(ChunkProgressBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalBytesProperty =
        DependencyProperty.Register(nameof(TotalBytes), typeof(long), typeof(ChunkProgressBar),
            new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SimpleProgressProperty =
        DependencyProperty.Register(nameof(SimpleProgress), typeof(double), typeof(ChunkProgressBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompletedBrushProperty =
        DependencyProperty.Register(nameof(CompletedBrush), typeof(Brush), typeof(ChunkProgressBar),
            new FrameworkPropertyMetadata(Brushes.Blue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownloadingBrushProperty =
        DependencyProperty.Register(nameof(DownloadingBrush), typeof(Brush), typeof(ChunkProgressBar),
            new FrameworkPropertyMetadata(Brushes.LightBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(ChunkProgressBar),
            new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(ChunkProgressBar),
            new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChunkSegmentInfo>? Segments
    {
        get => (IReadOnlyList<ChunkSegmentInfo>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long TotalBytes
    {
        get => (long)GetValue(TotalBytesProperty);
        set => SetValue(TotalBytesProperty, value);
    }

    public double SimpleProgress
    {
        get => (double)GetValue(SimpleProgressProperty);
        set => SetValue(SimpleProgressProperty, value);
    }

    public Brush CompletedBrush
    {
        get => (Brush)GetValue(CompletedBrushProperty);
        set => SetValue(CompletedBrushProperty, value);
    }

    public Brush DownloadingBrush
    {
        get => (Brush)GetValue(DownloadingBrushProperty);
        set => SetValue(DownloadingBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var r = Math.Min(CornerRadius, Math.Min(w / 2, h / 2));
        var fullRect = new Rect(0, 0, w, h);

        // 底层轨道（圆角背景）
        dc.DrawRoundedRectangle(TrackBrush, null, fullRect, r, r);

        var segments = Segments;
        var totalBytes = TotalBytes;

        if (totalBytes > 0 && segments?.Count > 1)
        {
            var clipGeometry = new RectangleGeometry(fullRect, r, r);
            dc.PushClip(clipGeometry);

            // 把所有 Completed 段合并为一条连续路径，一次填充
            // 这样相邻段之间不会有任何缝隙
            var completedGeo = new StreamGeometry();
            using (var ctx = completedGeo.Open())
            {
                ctx.BeginFigure(new Point(0, 0), false, false);
                foreach (var seg in segments)
                {
                    if (seg.SegmentStatus != ChunkSegmentStatus.Completed || seg.TotalSize <= 0)
                        continue;
                    var x1 = (double)seg.StartByte / totalBytes * w;
                    var x2 = (double)(seg.EndByte + 1) / totalBytes * w;
                    // 用矩形图：移动到左上 → 右上 → 右下 → 左下 → 关闭
                    ctx.BeginFigure(new Point(x1, 0), true, true);
                    ctx.LineTo(new Point(x2, 0), false, false);
                    ctx.LineTo(new Point(x2, h), false, false);
                    ctx.LineTo(new Point(x1, h), false, false);
                }
            }
            completedGeo.Freeze();
            dc.DrawGeometry(CompletedBrush, null, completedGeo);

            // Downloading 段：每个画已下载部分，浅色
            foreach (var seg in segments)
            {
                if (seg.SegmentStatus != ChunkSegmentStatus.Downloading || seg.TotalSize <= 0)
                    continue;
                var startX = (double)seg.StartByte / totalBytes * w;
                var segW = (double)(seg.EndByte + 1 - seg.StartByte) / totalBytes * w;
                var filledW = segW * seg.Progress;
                if (filledW > 0)
                {
                    // 用 Ceil 确保 filledW 覆盖到完整像素，与左侧 Completed 段无缝衔接
                    var fillRect = new Rect(startX, 0, Math.Ceiling(filledW), h);
                    dc.DrawRectangle(DownloadingBrush, null, fillRect);
                }
            }

            dc.Pop();
        }
        else
        {
            // 单线程/无分块：简单进度条
            var filledW = SimpleProgress / 100.0 * w;
            if (filledW > 0)
            {
                var clipGeometry = new RectangleGeometry(fullRect, r, r);
                dc.PushClip(clipGeometry);
                dc.DrawRectangle(CompletedBrush, null, new Rect(0, 0, Math.Ceiling(filledW), h));
                dc.Pop();
            }
        }
    }
}
