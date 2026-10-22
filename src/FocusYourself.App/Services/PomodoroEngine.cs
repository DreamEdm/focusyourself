using FocusYourself.Core;

namespace FocusYourself.Services;

public sealed class PhaseChangedEventArgs(Phase oldPhase, Phase newPhase, bool naturalEnd) : EventArgs
{
    public Phase OldPhase { get; } = oldPhase;
    public Phase NewPhase { get; } = newPhase;
    /// <summary>true 表示倒计时自然结束切换，false 表示用户手动开始/跳过/停止。</summary>
    public bool NaturalEnd { get; } = naturalEnd;
}

/// <summary>
/// 番茄钟状态机。用绝对结束时间计算剩余时间，电脑休眠后醒来也不会漂移。
/// 不依赖 UI 线程：由外部定时调用 <see cref="Tick"/>。
/// </summary>
public sealed class PomodoroEngine
{
    readonly Func<DateTime> _utcNow;

    public PomodoroEngine(PomodoroSettings settings, Func<DateTime>? utcNow = null)
    {
        Settings = settings;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public PomodoroSettings Settings { get; set; }
    public Phase Phase { get; private set; } = Phase.Idle;
    public DateTime? EndsAtUtc { get; private set; }
    public DateTime? PhaseStartedUtc { get; private set; }
    public int CompletedFocusRounds { get; private set; }
    /// <summary>暂停的时刻；为 null 表示没有暂停。暂停期间倒计时冻结，阶段（以及专注期的拦截）保持不变。</summary>
    public DateTime? PausedAtUtc { get; private set; }

    public bool IsFocus => Phase == Phase.Focus;
    public bool IsBreak => Phase is Phase.ShortBreak or Phase.LongBreak;
    public bool IsPaused => PausedAtUtc is not null;

    /// <summary>已经暂停了多久（没暂停为 0）。</summary>
    public TimeSpan PausedFor => PausedAtUtc is { } at ? _utcNow() - at : TimeSpan.Zero;

    /// <summary>计算剩余时间用的“当前时刻”：暂停时停在暂停那一刻。</summary>
    DateTime ClockNow => PausedAtUtc ?? _utcNow();

    public TimeSpan Remaining =>
        EndsAtUtc is { } end ? TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((end - ClockNow).TotalSeconds))) : TimeSpan.Zero;

    public TimeSpan PhaseDuration =>
        EndsAtUtc is { } end && PhaseStartedUtc is { } start ? end - start : TimeSpan.Zero;

    /// <summary>当前阶段已完成的比例 0~1。</summary>
    public double Progress =>
        PhaseDuration.TotalSeconds > 0 ? Math.Clamp(1 - Remaining.TotalSeconds / PhaseDuration.TotalSeconds, 0, 1) : 0;

    public event EventHandler<PhaseChangedEventArgs>? PhaseChanged;
    /// <summary>暂停或继续时触发。</summary>
    public event EventHandler? PauseChanged;

    public void StartFocus() => Enter(Phase.Focus, naturalEnd: false);

    public void StartBreak() => Enter(NextBreakPhase(), naturalEnd: false);

    /// <summary>跳过当前阶段，进入下一阶段（跳过专注不计入完成轮数）。</summary>
    public void Skip()
    {
        switch (Phase)
        {
            case Phase.Focus: Enter(NextBreakPhase(), naturalEnd: false); break;
            case Phase.ShortBreak or Phase.LongBreak: Enter(Phase.Focus, naturalEnd: false); break;
        }
    }

    public void Stop() => Enter(Phase.Idle, naturalEnd: false);

    /// <summary>暂停当前阶段的倒计时（空闲或已暂停时无效）。</summary>
    public void Pause()
    {
        if (Phase == Phase.Idle || IsPaused) return;
        PausedAtUtc = _utcNow();
        PauseChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>继续倒计时：开始和结束时间一起顺延暂停的时长，剩余时间和进度保持暂停时的样子。</summary>
    public void Resume()
    {
        if (PausedAtUtc is not { } at) return;
        var shift = _utcNow() - at;
        EndsAtUtc += shift;
        PhaseStartedUtc += shift;
        PausedAtUtc = null;
        PauseChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePause()
    {
        if (IsPaused) Resume();
        else Pause();
    }

    public void Tick()
    {
        if (Phase == Phase.Idle || IsPaused || EndsAtUtc is not { } end || _utcNow() < end) return;

        if (Phase == Phase.Focus)
        {
            CompletedFocusRounds++;
            Enter(NextBreakPhase(), naturalEnd: true);
        }
        else
        {
            Enter(Settings.AutoStartNextFocus ? Phase.Focus : Phase.Idle, naturalEnd: true);
        }
    }

    public FocusState Snapshot() => new()
    {
        Phase = Phase,
        EndsAtUtc = EndsAtUtc,
        PhaseStartedUtc = PhaseStartedUtc,
        PausedAtUtc = PausedAtUtc,
        CompletedFocusRounds = CompletedFocusRounds,
    };

    /// <summary>从持久化状态恢复（例如程序被结束后重新打开，专注继续）。</summary>
    public void Restore(FocusState state)
    {
        CompletedFocusRounds = state.CompletedFocusRounds;
        // 暂停中的阶段不会过期：按暂停那一刻判断还有没有剩余时间
        var pausedAt = state.PausedAtUtc is { } p && p <= _utcNow() ? p : (DateTime?)null;
        if (state.Phase == Phase.Idle || state.EndsAtUtc is not { } end || end <= (pausedAt ?? _utcNow()))
        {
            Phase = Phase.Idle;
            EndsAtUtc = null;
            PhaseStartedUtc = null;
            PausedAtUtc = null;
            return;
        }
        Phase = state.Phase;
        EndsAtUtc = end;
        PhaseStartedUtc = state.PhaseStartedUtc ?? pausedAt ?? _utcNow();
        PausedAtUtc = pausedAt;
    }

    Phase NextBreakPhase()
    {
        var rounds = Math.Max(1, Settings.RoundsBeforeLongBreak);
        // 刚完成的轮数是 rounds 的倍数时进入长休息
        return CompletedFocusRounds > 0 && CompletedFocusRounds % rounds == 0 ? Phase.LongBreak : Phase.ShortBreak;
    }

    void Enter(Phase next, bool naturalEnd)
    {
        var old = Phase;
        Phase = next;
        var now = _utcNow();
        var minutes = next switch
        {
            Phase.Focus => Settings.FocusMinutes,
            Phase.ShortBreak => Settings.ShortBreakMinutes,
            Phase.LongBreak => Settings.LongBreakMinutes,
            _ => 0,
        };
        PausedAtUtc = null;
        PhaseStartedUtc = next == Phase.Idle ? null : now;
        EndsAtUtc = next == Phase.Idle ? null : now.AddMinutes(Math.Max(1, minutes));
        PhaseChanged?.Invoke(this, new PhaseChangedEventArgs(old, next, naturalEnd));
    }
}
