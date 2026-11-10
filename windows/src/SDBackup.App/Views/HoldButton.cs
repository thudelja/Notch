using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SDBackup.App;

/// <summary>
/// A button that runs its command only after being held for 1.5 seconds, so a shoot isn't finished by a stray click. A ring
/// fills while it's held and empties again if it's released early; when the button appears, the ring starts filling and
/// empties again once, a hint that it's held rather than clicked. Screen readers invoke it directly.
/// </summary>
public sealed class HoldButton : Button
{
    public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1.5);

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(HoldButton), new PropertyMetadata("", (d, e) => ((HoldButton)d)._text.Text = (string)e.NewValue));

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(HoldButton), new PropertyMetadata(0.0, (d, _) => ((HoldButton)d).UpdateRing()));

    private const double RingSize = 14;
    private const double RingThickness = 2;
    private readonly Ellipse _ring = new() { Width = RingSize, Height = RingSize, StrokeThickness = RingThickness, StrokeDashCap = PenLineCap.Round };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private bool _holding;

    public HoldButton()
    {
        var track = new Ellipse { Width = RingSize, Height = RingSize, StrokeThickness = RingThickness, Opacity = 0.35 };
        track.SetResourceReference(Shape.StrokeProperty, "TextSecondaryBrush");
        _ring.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        _ring.RenderTransform = new RotateTransform(-90, RingSize / 2, RingSize / 2);
        var ring = new Grid { Width = RingSize, Height = RingSize, VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(track);
        ring.Children.Add(_ring);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(ring);
        content.Children.Add(_text);
        Content = content;
        SetResourceReference(StyleProperty, "SubtleButton");
        ToolTip = "Hold for 1.5 seconds";
        UpdateRing();
        // When it appears (the row's choice changes to a shoot), not when it's loaded hidden.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Hint();
        };
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>How far the ring is filled, 0 to 1.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>A click alone does nothing: the button is held.</summary>
    protected override void OnClick()
    {
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HoldButtonAutomationPeer(this);

    internal void RunFromAutomation() => Run();

    private void Run()
    {
        if (Command?.CanExecute(CommandParameter) == true) Command.Execute(CommandParameter);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Focus();
        CaptureMouse();
        StartHolding();
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        ReleaseMouseCapture();
        StopHolding();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        StopHolding();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter && !e.IsRepeat)
        {
            e.Handled = true;
            StartHolding();
            return;
        }
        if (e.Key is Key.Space or Key.Enter) e.Handled = true;
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            e.Handled = true;
            StopHolding();
            return;
        }
        base.OnKeyUp(e);
    }

    private void StartHolding()
    {
        if (_holding || !IsEnabled) return;
        _holding = true;
        var fill = new DoubleAnimation(1, new Duration(HoldTime.Multiply(1 - Progress)));
        fill.Completed += (_, _) =>
        {
            if (!_holding) return;
            _holding = false;
            Run();
        };
        BeginAnimation(ProgressProperty, fill);
    }

    private void StopHolding()
    {
        if (!_holding) return;
        _holding = false;
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(250))));
    }

    /// <summary>Fills a third of the ring and empties it again: this button is held, not clicked.</summary>
    private void Hint()
    {
        var hint = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        hint.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400))));
        hint.KeyFrames.Add(new EasingDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(750))) { EasingFunction = new SineEase() });
        hint.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1150))) { EasingFunction = new SineEase() });
        BeginAnimation(ProgressProperty, hint);
    }

    /// <summary>A dash as long as the filled part of the circle, measured in stroke widths as WPF requires.</summary>
    private void UpdateRing()
    {
        var circumference = Math.PI * (RingSize - RingThickness) / RingThickness;
        var filled = Math.Clamp(Progress, 0, 1) * circumference;
        _ring.StrokeDashArray = new DoubleCollection { filled, circumference + 1 };
        _ring.Visibility = filled <= 0.01 ? Visibility.Hidden : Visibility.Visible;
    }
}

/// <summary>Invoking the button from a screen reader or UI Automation runs its command: there is nothing to hold.</summary>
internal sealed class HoldButtonAutomationPeer(HoldButton owner) : ButtonAutomationPeer(owner), IInvokeProvider
{
    protected override string GetHelpTextCore() => "Hold for 1.5 seconds";

    void IInvokeProvider.Invoke()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        owner.Dispatcher.BeginInvoke(new Action(owner.RunFromAutomation));
    }
}
