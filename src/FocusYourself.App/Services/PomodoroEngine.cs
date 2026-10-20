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

    public bool IsFocus => Phase == Phase.Focus;
    public bool IsBreak => Phase is Phase.ShortBreak or Phase.LongBreak;

    public TimeSpan Remaining =>
        EndsAtUtc is { } end ? TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((end - _utcNow()).TotalSeconds))) : TimeSpan.Zero;

    public TimeSpan PhaseDuration =>
        EndsAtUtc is { } end && PhaseStartedUtc is { } start ? end - start : TimeSpan.Zero;

    /// <summary>当前阶段已完成的比例 0~1。</summary>
    public double Progress =>
        PhaseDuration.TotalSeconds > 0 ? Math.Clamp(1 - Remaining.TotalSeconds / PhaseDuration.TotalSeconds, 0, 1) : 0;

    public event EventHandler<PhaseChangedEventArgs>? PhaseChanged;

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

    public void Tick()
    {
        if (Phase == Phase.Idle || EndsAtUtc is not { } end || _utcNow() < end) return;

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
        CompletedFocusRounds = CompletedFocusRounds,
    };

    /// <summary>从持久化状态恢复（例如程序被结束后重新打开，专注继续）。</summary>
    public void Restore(FocusState state)
    {
        CompletedFocusRounds = state.CompletedFocusRounds;
        if (state.Phase == Phase.Idle || state.EndsAtUtc is not { } end || end <= _utcNow())
        {
            Phase = Phase.Idle;
            EndsAtUtc = null;
            PhaseStartedUtc = null;
            return;
        }
        Phase = state.Phase;
        EndsAtUtc = end;
        PhaseStartedUtc = state.PhaseStartedUtc ?? _utcNow();
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
        PhaseStartedUtc = next == Phase.Idle ? null : now;
        EndsAtUtc = next == Phase.Idle ? null : now.AddMinutes(Math.Max(1, minutes));
        PhaseChanged?.Invoke(this, new PhaseChangedEventArgs(old, next, naturalEnd));
    }
}
