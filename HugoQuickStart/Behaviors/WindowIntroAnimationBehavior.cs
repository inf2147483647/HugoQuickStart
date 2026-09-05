using System;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace HugoQuickStart.Behaviors;

/// <summary>
/// 窗口级入场动画。参数移植自 ClassIsland 的 <c>PopupIntroAnimation</c>：
/// 150ms 内完成 Opacity 0 → 1 与 Scale 0.925 → 1.0，缓动曲线 0.22,1,0.36,1。
/// <para>
/// 使用前先调用 <see cref="Prepare"/>（此时窗口尚未显示，可无闪烁地设置初始态），
/// 窗口 Opened 后调用 <see cref="Play"/> 播放动画。
/// </para>
/// </summary>
public static class WindowIntroAnimationBehavior
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(0.15);
    private static readonly IEasing EasingCurve = Easing.Parse("0.22, 1, 0.36, 1");

    /// <summary>在窗口显示前设置淡入 + 缩放的初始状态（窗口尚未 attach 合成器时为空操作）。</summary>
    public static void Prepare(Window window)
    {
        var visual = ElementComposition.GetElementVisual(window);
        if (visual == null)
        {
            return;
        }

        SetInitialState(window, visual);
    }

    /// <summary>播放窗口淡入 + 缩放入场动画。</summary>
    public static void Play(Window window)
    {
        var visual = ElementComposition.GetElementVisual(window);
        if (visual == null)
        {
            return;
        }

        // 确保从隐藏 + 缩小态起步（即使此前 Prepare 未生效），并按当前尺寸更新缩放中心
        SetInitialState(window, visual);

        var compositor = visual.Compositor;

        var animOpacity = compositor.CreateScalarKeyFrameAnimation();
        animOpacity.Target = nameof(visual.Opacity);
        animOpacity.Duration = Duration;
        animOpacity.InsertKeyFrame(0f, 0f);
        animOpacity.InsertKeyFrame(1f, 1f, EasingCurve);
        visual.StartAnimation(nameof(visual.Opacity), animOpacity);

        var animScale = compositor.CreateVector3DKeyFrameAnimation();
        animScale.Target = nameof(visual.Scale);
        animScale.Duration = Duration;
        animScale.InsertKeyFrame(0f, new Vector3D(0.925f, 0.925f, 1f));
        animScale.InsertKeyFrame(1f, new Vector3D(1f, 1f, 1f), EasingCurve);
        visual.StartAnimation(nameof(visual.Scale), animScale);
    }

    private static void SetInitialState(Window window, CompositionVisual visual)
    {
        visual.Opacity = 0;
        visual.Scale = new Vector3D(0.925f, 0.925f, 1f);
        visual.CenterPoint = new Vector3D(
            (float)(window.ClientSize.Width / 2.0),
            (float)(window.ClientSize.Height / 2.0),
            0);
    }
}
