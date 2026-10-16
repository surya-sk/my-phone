using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Messages.Controls;

/// <summary>
/// Horizontal swipes on a pane, Metro pivot style: a touch or pen flick, or a two-finger touchpad swipe.
/// Mouse drags are ignored so text selection and drag-and-drop keep working. Vertical scrolling is left
/// to the list underneath: a swipe only counts when it is clearly more sideways than up or down.
/// </summary>
public static class SwipeGesture
{
    private const double Distance = 70;       // Effective pixels a finger must travel.
    private const double WheelDistance = 220; // Accumulated touchpad delta for one swipe.

    /// <param name="onSwipe">-1 when the content is pushed left (go forward), +1 when pushed right (go back).</param>
    /// <param name="touch">Listen for finger and pen flicks. Off where rows have their own swipe actions.</param>
    /// <param name="touchpad">Listen for two-finger touchpad swipes.</param>
    public static void Attach(UIElement element, Action<int> onSwipe, bool touch = true, bool touchpad = true)
    {
        if (touch) AttachTouch(element, onSwipe);
        if (touchpad) AttachTouchpad(element, onSwipe);
    }

    private static void AttachTouch(UIElement element, Action<int> onSwipe)
    {
        uint? pointer = null;
        Windows.Foundation.Point start = default;

        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse) return;
            pointer = e.Pointer.PointerId;
            start = e.GetCurrentPoint(element).Position;
        }), handledEventsToo: true);

        void Finish(PointerRoutedEventArgs e)
        {
            if (pointer != e.Pointer.PointerId) return;
            pointer = null;
            var end = e.GetCurrentPoint(element).Position;
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            if (Math.Abs(dx) >= Distance && Math.Abs(dx) > 2 * Math.Abs(dy)) onSwipe(dx < 0 ? -1 : 1);
        }

        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => Finish(e)), handledEventsToo: true);
        // A list that starts scrolling takes the pointer away; that is a scroll, not a swipe.
        element.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => pointer = null), handledEventsToo: true);
    }

    private static void AttachTouchpad(UIElement element, Action<int> onSwipe)
    {
        double wheel = 0;
        var lastWheel = DateTime.MinValue;
        var blockedUntil = DateTime.MinValue;
        element.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(element);
            if (!point.Properties.IsHorizontalMouseWheel) return;
            var now = DateTime.UtcNow;
            if (now - lastWheel > TimeSpan.FromMilliseconds(300)) wheel = 0;  // A pause starts a new gesture.
            lastWheel = now;
            if (now < blockedUntil) return;  // The rest of a gesture that already switched.
            wheel += point.Properties.MouseWheelDelta;
            if (Math.Abs(wheel) < WheelDistance) return;
            // Touchpad: fingers moving left report a positive delta, matching content pushed left.
            onSwipe(wheel > 0 ? -1 : 1);
            wheel = 0;
            blockedUntil = now.AddMilliseconds(600);
        }), handledEventsToo: true);
    }
}
