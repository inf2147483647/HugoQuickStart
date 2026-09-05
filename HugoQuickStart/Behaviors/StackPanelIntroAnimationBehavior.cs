using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace HugoQuickStart.Behaviors;

/// <summary>
/// <see cref="Panel"/> 子元素"错峰入场"动画行为，参数严格移植自 ClassIsland 的
/// <c>StackPanelIntroAnimation.axaml</c>（每项间隔 25ms、单次 750ms、缓动 0,1,0,1，
/// 位移 TranslateTransform.Y: 50 → 0，位移走 RenderTransform 不干扰布局坐标）。
/// <para>
/// 实现为<b>纯代码本地值 + 手动插值时钟</b>：<see cref="Prepare"/> 把子元素立即写入
/// 隐藏态本地值（Opacity=0、Y=-25），<see cref="Play"/> 用手动计时器逐帧插值写回
/// 可见值。不依赖 Avalonia 样式动画系统，因此不存在"动画填充态清除延迟"导致的
/// "先显示最终态再重播"抽动问题；任何时刻可见帧取值都是确定的本地值。
/// </para>
/// </summary>
public static class StackPanelIntroAnimationBehavior
{
    private const double HiddenY = -25.0;
    private const double StartY = 50.0;
    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(750);
    private static readonly IEasing IntroEasing = Easing.Parse("0.00,1.00,0.00,1.00");

    private sealed class Entry
    {
        public required Control Control;
        public required TranslateTransform Transform;
        public long StartTimestamp;
    }

    private static readonly List<Entry> Entries = new();
    private static DispatcherTimer? _timer;

    /// <summary>
    /// 把面板内所有可见子元素立即写入"未播放"的隐藏态（Opacity=0、Y=-25）。
    /// 应在页面变为可见之前调用，保证内容可见首帧即为隐藏态。幂等。
    /// </summary>
    public static void Prepare(Panel panel)
    {
        foreach (var c in panel.Children.Where(x => x.IsVisible))
        {
            c.Opacity = 0;
            c.RenderTransform = new TranslateTransform { Y = HiddenY };
        }
    }

    /// <summary>停止并清空进行中的错峰动画。</summary>
    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
        Entries.Clear();
    }

    /// <summary>
    /// 以 25ms 间隔逐个触发子元素入场动画。调用前建议先 <see cref="Prepare"/>；
    /// 本方法内部也会先复位隐藏起点。重复调用会自动取代上一次动画。
    /// </summary>
    public static void Play(Panel panel)
    {
        // 取代上一次动画，避免旧时钟干扰新页面
        Stop();

        var targets = panel.Children.Where(x => x.IsVisible).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < targets.Count; i++)
        {
            var c = targets[i];
            // 动画起点：从下方 50px、透明开始（页面若先前已 Prepare，此处首帧即被覆盖，不会闪现）
            var transform = new TranslateTransform { Y = StartY };
            c.RenderTransform = transform;
            c.Opacity = 0;

            Entries.Add(new Entry
            {
                Control = c,
                Transform = transform,
                StartTimestamp = now + StepInterval.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond * i
            });
        }

        _timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var finished = true;

        foreach (var entry in Entries)
        {
            if (now < entry.StartTimestamp)
            {
                finished = false;
                continue;
            }

            var elapsed = (now - entry.StartTimestamp) / (double)Stopwatch.Frequency;
            var progress = elapsed / Duration.TotalSeconds;
            if (progress >= 1.0)
            {
                progress = 1.0;
            }
            else
            {
                finished = false;
            }

            var eased = IntroEasing.Ease(progress);
            entry.Control.Opacity = eased;
            entry.Transform.Y = StartY + (0.0 - StartY) * eased;
        }

        if (finished)
        {
            Stop();
        }
    }
}
