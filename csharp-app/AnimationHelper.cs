using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Typryt;

/// <summary>
/// Builds the splash animation: the logo "Typryt" is typed, one step at a time, with a
/// backspace correction on the way, and the cursor finishes as a full stop.
///
/// Timeline (milliseconds):
///   0 - 2850   the cursor blinks, about every 950 ms, fading rather than snapping
///   2850       t         (typed)
///   3000       ty
///   3150       t         (y deleted)
///   3250       (empty)   (t deleted)
///   3400       T         (corrected capital)
///   3580 - 4300  Ty, Typ, Typr, Typry, Typryt
///   4500       the cursor becomes a full stop
///   4900       the splash closes, after the full stop has held for 400 ms
/// </summary>
public static class AnimationHelper
{
    public const int TotalMilliseconds = 4900;

    private static readonly (int At, string Text)[] WordSteps =
    [
        (2850, "t"),
        (3000, "ty"),
        (3150, "t"),
        (3250, ""),
        (3400, "T"),
        (3580, "Ty"),
        (3760, "Typ"),
        (3940, "Typr"),
        (4120, "Typry"),
        (4300, "Typryt"),
    ];

    /// <summary>Builds the storyboard for a word and its cursor. The caller starts it.</summary>
    public static Storyboard Build(TextBlock word, TextBlock cursor)
    {
        var storyboard = new Storyboard();

        // The word is typed and corrected.
        var typing = new StringAnimationUsingKeyFrames { Duration = Duration(TotalMilliseconds) };
        typing.KeyFrames.Add(new DiscreteStringKeyFrame("", KeyTime.FromTimeSpan(TimeSpan.Zero)));
        foreach (var (at, text) in WordSteps)
        {
            typing.KeyFrames.Add(new DiscreteStringKeyFrame(text, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at))));
        }
        Add(storyboard, typing, word, TextBlock.TextProperty);

        // The cursor blinks until typing starts (fading in and out rather than snapping),
        // stays solid while typing, then becomes a full stop.
        var blink = new DoubleAnimationUsingKeyFrames { Duration = Duration(TotalMilliseconds) };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1050))));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800))));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1950))));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2850))));
        Add(storyboard, blink, cursor, UIElement.OpacityProperty);

        var cursorText = new StringAnimationUsingKeyFrames { Duration = Duration(TotalMilliseconds) };
        cursorText.KeyFrames.Add(new DiscreteStringKeyFrame("|", KeyTime.FromTimeSpan(TimeSpan.Zero)));
        cursorText.KeyFrames.Add(new DiscreteStringKeyFrame(".", KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(4500))));
        Add(storyboard, cursorText, cursor, TextBlock.TextProperty);

        return storyboard;
    }

    private static Duration Duration(int milliseconds) => new(TimeSpan.FromMilliseconds(milliseconds));

    private static void Add(Storyboard storyboard, Timeline animation, DependencyObject target, DependencyProperty property)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }
}
