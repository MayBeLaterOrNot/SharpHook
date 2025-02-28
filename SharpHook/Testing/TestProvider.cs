namespace SharpHook.Testing;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using SharpHook.Native;
using SharpHook.Providers;

/// <summary>
/// A provider of low-level functionality which can be used in tests.
/// </summary>
public class TestProvider :
    IGlobalHookProvider,
    ILoggingProvider,
    IEventSimulationProvider,
    IScreenInfoProvider,
    IMouseInfoProvider
{
    private readonly BlockingCollection<UioHookEvent> hookEvents = new();
    private readonly BlockingCollection<bool> suppressNextEvent = new();

    private CancellationTokenSource? cancellationTokenSource;

    private readonly List<UioHookEvent> postedEvents = new();
    private readonly List<string> postedText = new();

    private DispatchProc? dispatchProc;
    private IntPtr userData;
    private ScreenData[] screenInfo = Array.Empty<ScreenData>();

    /// <summary>
    /// Gets the events that have been posted using <see cref="PostEvent(ref UioHookEvent)" />.
    /// </summary>
    public IReadOnlyList<UioHookEvent> PostedEvents =>
        this.postedEvents.AsReadOnly();

    /// <summary>
    /// Gets the text that has been posted using <see cref="PostText(string)" />.
    /// </summary>
    public IReadOnlyList<string> PostedText =>
        this.postedText.AsReadOnly();

    /// <summary>
    /// Gets the value which indicates whether this provider runs the testing hook.
    /// </summary>
    /// <value><see langword="true" /> if the testing hook is running. Otherwise, <see langword="false" />.</value>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Gets or sets the result of the <see cref="Run()" /> method. If anything other than
    /// <see cref="UioHookResult.Success" /> is set, then the testing hook won't be started.
    /// </summary>
    /// <value>The result of the <see cref="Run()" /> method.</value>
    public UioHookResult RunResult { get; set; } = UioHookResult.Success;

    /// <summary>
    /// Gets or sets the result of the <see cref="Stop()" /> method. If anything other than
    /// <see cref="UioHookResult.Success" /> is set, then the testing hook won't be stopped.
    /// </summary>
    /// <value>The result of the <see cref="Stop()" /> method.</value>
    public UioHookResult StopResult { get; set; } = UioHookResult.Success;

    /// <summary>
    /// Gets or sets the result of the <see cref="PostEvent(ref UioHookEvent)" /> method. If anything other than
    /// <see cref="UioHookResult.Success" /> is set, then the event won't be posted.
    /// </summary>
    /// <value>The result of the <see cref="PostEvent(ref UioHookEvent)" /> method.</value>
    public UioHookResult PostEventResult { get; set; } = UioHookResult.Success;

    /// <summary>
    /// Gets or sets the result of the <see cref="PostText(string)" /> method. If anything other than
    /// <see cref="UioHookResult.Success" /> is set, then the text won't be posted.
    /// </summary>
    /// <value>The result of the <see cref="PostText(string)" /> method.</value>
    public UioHookResult PostTextResult { get; set; } = UioHookResult.Success;

    /// <summary>
    /// Gets or sets the delay (in nanoseconds) between posting individual characters when posting text on Linux.
    /// </summary>
    /// <returns>The delay (in nanoseconds) between posting individual characters when posting text on Linux.</returns>
    public ulong PostTextDelayX11 { get; set; }

    /// <summary>
    /// Gets or sets the information about screens for testing.
    /// </summary>
    /// <returns>The information about screens.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is <see langword="nul" />.</exception>
    public ScreenData[] ScreenInfo
    {
        get => this.screenInfo;
        set => this.screenInfo = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets or sets the auto-repeat rate for testing.
    /// </summary>
    /// <returns>The auto-repeat rate.</returns>
    public int AutoRepeatRate { get; set; }

    /// <summary>
    /// Gets or sets the auto-repeat delay for testing.
    /// </summary>
    /// <returns>The auto-repeat rate.</returns>
    public int AutoRepeatDelay { get; set; }

    /// <summary>
    /// Gets or sets the pointer acceleration multiplier for testing.
    /// </summary>
    /// <returns>The pointer acceleration multiplier.</returns>
    public int PointerAccelerationMultiplier { get; set; }

    /// <summary>
    /// Gets or sets the pointer acceleration threshold for testing.
    /// </summary>
    /// <returns>The pointer acceleration threshold.</returns>
    public int PointerAccelerationThreshold { get; set; }

    /// <summary>
    /// Gets or sets the pointer sensitivity for testing.
    /// </summary>
    /// <returns>The pointer sensitivity.</returns>
    public int PointerSensitivity { get; set; }

    /// <summary>
    /// Gets or sets the multi-click time for testing.
    /// </summary>
    /// <returns>The multi-click time.</returns>
    public int MultiClickTime { get; set; }

    /// <summary>
    /// Sets the hook callback function for testing.
    /// </summary>
    /// <param name="dispatchProc">
    /// The function to call when an event is raised, or <see langword="null" /> to unset the function.
    /// </param>
    /// <param name="userData">
    /// Custom data to pass to the callback.
    /// </param>
    public void SetDispatchProc(DispatchProc? dispatchProc, IntPtr userData)
    {
        this.dispatchProc = dispatchProc;
        this.userData = userData;
    }

    /// <summary>
    /// Runs the testing hook if <see cref="RunResult" /> is set to <see cref="UioHookResult.Success" />. Otherwise,
    /// does nothing.
    /// </summary>
    /// <returns>The value of <see cref="RunResult" />.</returns>
    /// <remarks>
    /// The testing hook is an event loop which receives events from the <see cref="PostEvent(ref UioHookEvent)" />
    /// method. The hook can be started and stopped multiple times, but cannot be started when it's already running.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The hook is already running.</exception>
    public UioHookResult Run()
    {
        if (this.IsRunning)
        {
            throw new InvalidOperationException("The testing hook is already running");
        }

        var result = this.RunResult;
        if (result != UioHookResult.Success)
        {
            return result;
        }

        this.cancellationTokenSource = new();
        this.IsRunning = true;

        while (this.IsRunning)
        {
            try
            {
                var currentEvent = this.hookEvents.Take(cancellationTokenSource.Token);
                this.dispatchProc?.Invoke(ref currentEvent, this.userData);
                this.suppressNextEvent.Add(currentEvent.Reserved.HasFlag(EventReservedValueMask.SuppressEvent));
            } catch (OperationCanceledException) { }
        }

        return result;
    }

    /// <summary>
    /// Stops the testing hook if <see cref="StopResult" /> is set to <see cref="UioHookResult.Success" />. Otherwise,
    /// does nothing.
    /// </summary>
    /// <returns>The value of <see cref="StopResult" />.</returns>
    public UioHookResult Stop()
    {
        var result = this.StopResult;

        if (result == UioHookResult.Success)
        {
            this.IsRunning = false;
            if (this.cancellationTokenSource is not null)
            {
                this.cancellationTokenSource.Cancel();
                this.cancellationTokenSource.Dispose();
                this.cancellationTokenSource = null;
            }
        }

        return result;
    }

    /// <summary>
    /// Posts an input event if <see cref="PostEventResult" /> is set to <see cref="UioHookResult.Success" /> -
    /// this event will be dispatched if the provider is running. Otherwise, does nothing.
    /// </summary>
    /// <param name="e">The event to post.</param>
    /// <returns>The value of <see cref="PostEventResult" />.</returns>
    /// <remarks>This method returns after the event has been handled.</remarks>
    public UioHookResult PostEvent(ref UioHookEvent e)
    {
        var result = this.PostEventResult;

        if (result != UioHookResult.Success)
        {
            return result;
        }

        this.postedEvents.Add(e);

        if (this.IsRunning && cancellationTokenSource is not null)
        {
            this.hookEvents.Add(e);

            try
            {
                bool suppress = this.suppressNextEvent.Take(cancellationTokenSource.Token);

                if (suppress)
                {
                    e.Reserved |= EventReservedValueMask.SuppressEvent;
                }
            } catch (OperationCanceledException) { }
        }

        return result;
    }

    /// <summary>
    /// Simulates the input of arbitrary Unicode characters if <see cref="PostTextResult" /> is set to
    /// <see cref="UioHookResult.Success" />. Otherwise, does nothing.
    /// </summary>
    /// <param name="text">The text to simulate.</param>
    /// <returns>
    /// The value of <see cref="PostTextResult" />, or <see cref="UioHookResult.ErrorPostTextNull" /> if
    /// <paramref name="text" /> is <see langword="null" />.
    /// </returns>
    /// <remarks>This method doesn't cause any input events to be created, unlike real text entry simulation.</remarks>
    public UioHookResult PostText(string text)
    {
        if (text is null)
        {
            return UioHookResult.ErrorPostTextNull;
        }

        var result = this.PostTextResult;

        if (result == UioHookResult.Success)
        {
            this.postedText.Add(text);
        }

        return result;
    }

    void ILoggingProvider.SetLoggerProc(LoggerProc? loggerProc, IntPtr userData)
    { }

    ulong IEventSimulationProvider.GetPostTextDelayX11() =>
        this.PostTextDelayX11;

    void IEventSimulationProvider.SetPostTextDelayX11(ulong delayNanoseconds) =>
        this.PostTextDelayX11 = delayNanoseconds;

    ScreenData[] IScreenInfoProvider.CreateScreenInfo() =>
        this.ScreenInfo;

    int IMouseInfoProvider.GetAutoRepeatRate() =>
        this.AutoRepeatRate;

    int IMouseInfoProvider.GetAutoRepeatDelay() =>
        this.AutoRepeatDelay;

    int IMouseInfoProvider.GetPointerAccelerationMultiplier() =>
        this.PointerAccelerationMultiplier;

    int IMouseInfoProvider.GetPointerAccelerationThreshold() =>
        this.PointerAccelerationThreshold;

    int IMouseInfoProvider.GetPointerSensitivity() =>
        this.PointerSensitivity;

    int IMouseInfoProvider.GetMultiClickTime() =>
        this.MultiClickTime;
}
